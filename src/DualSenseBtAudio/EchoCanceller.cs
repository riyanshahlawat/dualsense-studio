using SpeexDSPSharp.Core;

// Acoustic echo cancellation (SpeexDSP), like a PS5 does for headsets: the headphone
// audio (what your friends say) is the reference, and its leak into the headset
// mic is subtracted so they don't hear themselves.
// 48 kHz mono, 10 ms frames, 200 ms echo tail.
internal static class EchoCanceller
{
    private const int FrameSize = 480;
    private const int TailSamples = 9600;

    private static readonly object Lock = new();
    private static readonly Queue<short> Playback = new();
    private static SpeexDSPEchoCanceler? _aec;
    private static readonly short[] _farFrame = new short[FrameSize];
    private static readonly short[] _cleaned = new short[FrameSize];

    public static bool Enabled { get; private set; }

    public static void Start()
    {
        try
        {
            _aec = new SpeexDSPEchoCanceler(FrameSize, TailSamples, null);
            int rate = 48000;
            _aec.Ctl(EchoCancellationCtl.SPEEX_ECHO_SET_SAMPLING_RATE, ref rate);
            Enabled = true;
            Console.WriteLine("Mic: echo cancellation on.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mic: echo cancellation unavailable ({ex.Message}).");
        }
    }

    /// <summary>Headphone audio (interleaved stereo float, 48 kHz) as the echo reference.</summary>
    public static void AddPlayback(float[] stereo)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            AddPlaybackCore(stereo);
        }
        catch (Exception ex)
        {
            Disable(ex);
        }
    }

    private static void AddPlaybackCore(float[] stereo)
    {
        lock (Lock)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                float mono = (stereo[i] + stereo[i + 1]) * 0.5f;
                Playback.Enqueue((short)Math.Clamp(mono * 32767f, -32768f, 32767f));
            }

            // Keep at most ~300 ms of reference so a stall can't misalign it badly.
            while (Playback.Count > 14400)
            {
                Playback.Dequeue();
            }

            while (Playback.Count >= FrameSize)
            {
                for (int i = 0; i < FrameSize; i++)
                {
                    _farFrame[i] = Playback.Dequeue();
                }
                _aec!.EchoPlayback(_farFrame);
            }
        }
    }

    /// <summary>Removes headphone echo from one 10 ms mic frame (in place).</summary>
    public static void Process(short[] mic, int count)
    {
        if (!Enabled || count != FrameSize)
        {
            return;
        }

        try
        {
            lock (Lock)
            {
                _aec!.EchoCapture(mic, _cleaned);
            }
            Array.Copy(_cleaned, mic, FrameSize);
        }
        catch (Exception ex)
        {
            Disable(ex);
        }
    }

    // Fail safe: any error turns echo cancellation off; the mic keeps working.
    private static void Disable(Exception ex)
    {
        Enabled = false;
        Console.WriteLine($"Mic: echo cancellation turned off ({ex.Message}).");
    }

    public static void Stop()
    {
        lock (Lock)
        {
            Enabled = false;
            _aec?.Dispose();
            _aec = null;
            Playback.Clear();
        }
    }
}
