using HidApi;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpusSharp.Core;

// Receives the DualSense microphone over Bluetooth and plays it into a virtual
// cable (VB-Audio Hi-Fi Cable) so Windows apps can record it from
// "Hi-Fi Cable Output". Protocol follows vDS (hurryman2212/vds):
// 0x32 mic-open report, then 0x31 input reports whose header low nibble is 0x2
// carry one 10 ms mono 48 kHz Opus frame (71 bytes) after the header.
internal sealed class MicBridge : IDisposable
{
    public static bool Enabled;
    public static bool NoiseRemoval;
    public static bool Sidetone;

    // Loudness of what's playing in the headphones (set by the audio engine), for echo reduction.
    public static volatile float PlaybackLevel;
    private float _duck = 1f;

    // Sidetone: a little of your own voice mixed into the headphone stream.
    private const float SidetoneGain = 0.35f;
    private static readonly object SidetoneLock = new();
    private static readonly Queue<float> SidetoneQueue = new();

    public static void MixSidetone(float[] stereo)
    {
        if (!Sidetone)
        {
            return;
        }

        lock (SidetoneLock)
        {
            // Keep sidetone near-live: drop backlog beyond ~30 ms.
            while (SidetoneQueue.Count > 1440)
            {
                SidetoneQueue.Dequeue();
            }

            for (int i = 0; i + 1 < stereo.Length && SidetoneQueue.Count > 0; i += 2)
            {
                float v = SidetoneQueue.Dequeue() * SidetoneGain;
                stereo[i] += v;
                stereo[i + 1] += v;
            }
        }
    }

    // Noise removal: 100 Hz high-pass (hum/rumble) + a soft noise gate that
    // follows the background level, so fans and keys don't get through.
    private float _hpPrevIn, _hpPrevOut, _noiseFloor = 0.01f, _gate;

    private void CleanUp(short[] pcm, int count)
    {
        const float hp = 0.987f;
        float energy = 0;
        var buffer = new float[count];
        for (int i = 0; i < count; i++)
        {
            float x = pcm[i] / 32768f;
            float y = hp * (_hpPrevOut + x - _hpPrevIn);
            _hpPrevIn = x;
            _hpPrevOut = y;
            buffer[i] = y;
            energy += y * y;
        }

        float rms = MathF.Sqrt(energy / Math.Max(1, count));
        // Floor tracks slowly upward and quickly downward.
        _noiseFloor = rms < _noiseFloor ? rms : _noiseFloor + (rms - _noiseFloor) * 0.002f;
        float target = rms > _noiseFloor * 2.2f + 0.0015f ? 1f : 0f; // gentler gate: keeps soft word endings
        for (int i = 0; i < count; i++)
        {
            _gate += (target - _gate) * (target > _gate ? 0.05f : 0.002f);
            pcm[i] = (short)Math.Clamp(buffer[i] * _gate * 32767f, -32768f, 32767f);
        }
    }

    private const int OpusSize = 71;
    private const int FrameSamples = 480;
    private const string DefaultTargetName = "Hi-Fi Cable";

    private readonly Device _hid;
    private readonly OpusDecoder _decoder = new(48000, 1);
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly MMDevice _target;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private byte _sequence;
    private long _frames;
    private long _decodeErrors;
    private int _peak;
    private bool _muted;
    private bool _mutePressed;

    private MicBridge(string targetName)
    {
        using var enumerator = new MMDeviceEnumerator();
        _target = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(targetName, StringComparison.OrdinalIgnoreCase)
                                 || d.FriendlyName.Contains("DualSense mic link", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Mic target \"{targetName}\" not found. Install VB-Audio Hi-Fi Cable.");

        _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true
        };
        _output = new WasapiOut(_target, AudioClientShareMode.Shared, true, 10);
        _output.Init(_buffer);

        _hid = new Device(ProtocolConstants.VendorId, ProtocolConstants.ProductId);
        _thread = new Thread(Run) { IsBackground = true, Name = "MicBridge", Priority = ThreadPriority.AboveNormal };
    }

    public static MicBridge Start(string? targetName = null)
    {
        var bridge = new MicBridge(targetName ?? DefaultTargetName);
        bridge._output.Play();
        bridge._thread.Start();
        Console.WriteLine($"Mic: DualSense mic -> \"{bridge._target.FriendlyName}\".");
        Console.WriteLine("Mic: select \"Hi-Fi Cable Output\" as the microphone in Discord/OBS/games.");
        return bridge;
    }

    private void Run()
    {
        var pcm = new short[FrameSamples];
        var stereo = new byte[FrameSamples * 4];
        var opus = new byte[OpusSize];
        long nextOpen = 0;
        int opensSent = 0;
        long nextStatus = Environment.TickCount64 + 5000;

        while (!_stopping)
        {
            long now = Environment.TickCount64;
            // Open the mic stream only at start (as vDS); re-sending it
            // periodically restarts the controller's audio and causes dropouts.
            if (opensSent < 3 && now >= nextOpen)
            {
                SendMicOpen(true);
                opensSent++;
                nextOpen = now + 100;
            }

            ReadOnlySpan<byte> data;
            try
            {
                data = _hid.ReadTimeout(547, 50);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mic: HID read failed: {ex.Message}");
                Thread.Sleep(200);
                continue;
            }

            // Control frame: watch the mute button (USB buttons[2] bit 0x04, at BT offset 2 + 9).
            if (data.Length > 11 && data[0] == 0x31 && (data[1] & 0x0F) == 0x01)
            {
                bool pressed = (data[11] & 0x04) != 0;
                if (pressed && !_mutePressed)
                {
                    _muted = !_muted;
                    SendMuteState();
                    Console.WriteLine(_muted ? "Mic: muted (controller button)." : "Mic: unmuted (controller button).");
                }
                _mutePressed = pressed;
            }

            if (data.Length >= 3 + OpusSize && data[0] == 0x31 && (data[1] & 0x0F) == 0x02)
            {
                if (_muted)
                {
                    continue;
                }

                data.Slice(3, OpusSize).CopyTo(opus);
                int decoded;
                try
                {
                    decoded = _decoder.Decode(opus, OpusSize, pcm, FrameSamples, false);
                }
                catch
                {
                    _decodeErrors++;
                    continue;
                }

                if (NoiseRemoval)
                {
                    CleanUp(pcm, decoded);
                }

                // Echo reduction: when your headphones are playing (friends talking) and
                // the mic is only hearing that leak, turn the mic down so they don't
                // hear themselves. Your own voice is louder than the leak and passes.
                float micEnergy = 0;
                for (int i = 0; i < decoded; i++)
                {
                    float x = pcm[i] / 32768f;
                    micEnergy += x * x;
                }
                float micRms = MathF.Sqrt(micEnergy / Math.Max(1, decoded));
                float playing = PlaybackLevel;
                float duckTarget = playing > 0.015f && micRms < playing * 0.5f ? 0.12f : 1f;

                for (int i = 0; i < decoded; i++)
                {
                    _duck += (duckTarget - _duck) * (duckTarget < _duck ? 0.01f : 0.004f);
                    float x = pcm[i] / 32768f * _duck;
                    // Soft limiter: loud bursts round off smoothly instead of clipping harshly.
                    pcm[i] = (short)(MathF.Tanh(x * 1.15f) * 32000f);
                }

                if (Sidetone)
                {
                    lock (SidetoneLock)
                    {
                        for (int i = 0; i < decoded; i++)
                        {
                            SidetoneQueue.Enqueue(pcm[i] / 32768f);
                        }
                    }
                }

                for (int i = 0; i < decoded; i++)
                {
                    short s = pcm[i];
                    _peak = Math.Max(_peak, Math.Abs((int)s));
                    int o = i * 4;
                    stereo[o] = (byte)s;
                    stereo[o + 1] = (byte)(s >> 8);
                    stereo[o + 2] = (byte)s;
                    stereo[o + 3] = (byte)(s >> 8);
                }
                // Jitter buffer: keep a small cushion so late Bluetooth packets don't
                // cause gaps, and trim gently (one 10 ms frame) when it grows too big.
                // Never clear it all at once - that is what made the voice cut out.
                double buffered = _buffer.BufferedDuration.TotalMilliseconds;
                double cushion = DualSenseAudioEngine.LowLatency ? 20 : 30;
                double ceiling = DualSenseAudioEngine.LowLatency ? 60 : 90;
                if (buffered > ceiling)
                {
                    continue; // drop this one frame to catch up
                }
                if (buffered < 5)
                {
                    // Ran dry: rebuild the cushion with a short bit of silence once.
                    var pad = new byte[(int)(48 * cushion) * 4];
                    _buffer.AddSamples(pad, 0, pad.Length);
                }
                _buffer.AddSamples(stereo, 0, decoded * 4);
                _frames++;
            }

            if (now >= nextStatus)
            {
                Console.WriteLine(
                    $"Mic: {_frames} frames, buffer {_buffer.BufferedDuration.TotalMilliseconds:0} ms, " +
                    $"decode errors {_decodeErrors}, peak level {_peak * 100 / 32768}%.");
                _peak = 0;
                nextStatus = now + 5000;
            }
        }
    }

    // 0x31 state report: mute LED + mic power-save mute bit (vDS build_bt_mic_state_report).
    private void SendMuteState()
    {
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = (byte)((_sequence & 0x0F) << 4);
        report[2] = 0x10;
        const int common = 3;
        report[common + 1] = 0x01 | 0x02;               // valid_flag1: mute LED + power save control
        report[common + 8] = _muted ? (byte)1 : (byte)0; // mute LED
        report[common + 9] = _muted ? (byte)0x10 : (byte)0; // power save: mic mute
        _sequence = (byte)((_sequence + 1) & 0x0F);
        uint crc = Crc32(report.AsSpan(0, 74));
        BitConverter.GetBytes(crc).CopyTo(report, 74);
        try
        {
            _hid.Write(report);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mic: mute state failed: {ex.Message}");
        }
    }

    private void SendMicOpen(bool open)
    {
        var report = new byte[142];
        report[0] = 0x32;
        report[1] = (byte)((_sequence & 0x0F) << 4);
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = open ? (byte)0xFF : (byte)0xFE;
        for (int i = 5; i <= 9; i++)
        {
            report[i] = 64;
        }
        report[10] = (byte)(_sequence & 0x0F);
        report[11] = 0x92;
        report[12] = 0x40;
        _sequence = (byte)((_sequence + 1) & 0x0F);
        uint crc = Crc32(report.AsSpan(0, 138));
        BitConverter.GetBytes(crc).CopyTo(report, 138);
        try
        {
            _hid.Write(report);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mic: open report failed: {ex.Message}");
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = Step(crc, 0xA2);
        foreach (byte b in data)
        {
            crc = Step(crc, b);
        }
        return ~crc;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(500);
        SendMicOpen(false);
        _output.Stop();
        _output.Dispose();
        _hid.Dispose();
        _target.Dispose();
        Console.WriteLine($"Mic: stopped after {_frames} frames.");
    }
}
