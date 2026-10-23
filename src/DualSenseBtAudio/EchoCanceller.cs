using System.Reflection;
using System.Runtime.InteropServices;
using SpeexDSPSharp.Core;

// Mic clean-up chain (SpeexDSP), like a PS5 does for headsets:
// 1. acoustic echo cancellation: the headphone audio (your friends) is the reference
//    and its leak into the headset mic is subtracted,
// 2. preprocessor: residual echo suppression (linked to the echo canceller), noise
//    suppression, automatic gain (even voice level) and de-reverb.
// 48 kHz mono, 10 ms frames, 200 ms echo tail. Fail-safe: any error disables a stage
// and the mic keeps working.
internal static class EchoCanceller
{
    private const int FrameSize = 480;
    private const int TailSamples = 9600;
    private const int SpeexPreprocessSetEchoState = 24;

    private static readonly object Lock = new();
    private static readonly Queue<short> Playback = new();
    private static SpeexDSPEchoCanceler? _aec;
    private static SpeexDSPPreprocessor? _pre;
    private static readonly short[] _farFrame = new short[FrameSize];
    private static readonly short[] _cleaned = new short[FrameSize];

    public static bool Enabled { get; private set; }
    private static bool _preEnabled;

    [DllImport("speexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_preprocess_ctl(IntPtr state, int request, IntPtr ptr);

    public static void Start(bool noiseSuppression)
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

        try
        {
            _pre = new SpeexDSPPreprocessor(FrameSize, 48000, null);
            int on = 1, off = 0;
            int denoise = noiseSuppression ? 1 : 0;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref denoise);
            int noiseDb = -25;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref noiseDb);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref on);
            float agcLevel = 12000f;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_LEVEL, ref agcLevel);
            int agcMaxGain = 18;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_MAX_GAIN, ref agcMaxGain);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DEREVERB, ref on);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_VAD, ref off);
            int echoDb = -40, echoActiveDb = -15;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_ECHO_SUPPRESS, ref echoDb);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_ECHO_SUPPRESS_ACTIVE, ref echoActiveDb);
            _preEnabled = true;

            if (_aec is not null && LinkEchoState())
            {
                Console.WriteLine("Mic: residual echo suppression on.");
            }
            else if (_aec is not null)
            {
                Console.WriteLine("Mic: residual echo suppression unavailable (echo canceller still on).");
            }
            Console.WriteLine($"Mic: voice processing on (auto gain, de-reverb{(noiseSuppression ? ", noise suppression" : "")}).");
        }
        catch (Exception ex)
        {
            _preEnabled = false;
            Console.WriteLine($"Mic: voice processing unavailable ({ex.Message}).");
        }
    }

    // speex_preprocess_ctl(SET_ECHO_STATE) needs the raw echo state pointer, which the
    // wrapper keeps private; read both native handles and call the C API directly.
    private static bool LinkEchoState()
    {
        try
        {
            IntPtr preState = NativeHandle(_pre!, "_preprocessor");
            IntPtr echoState = NativeHandle(_aec!, "_echoCanceler");
            if (preState == IntPtr.Zero || echoState == IntPtr.Zero)
            {
                return false;
            }
            return speex_preprocess_ctl(preState, SpeexPreprocessSetEchoState, echoState) == 0;
        }
        catch
        {
            return false;
        }
    }

    // SpeexDSPSharp layout: wrapper._echoCanceler / ._preprocessor -> implementation
    // (Static or Dynamic) -> _handler (SafeHandle around the native state pointer).
    private static IntPtr NativeHandle(object owner, string field)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object? impl = owner.GetType().GetField(field, Any)?.GetValue(owner);
        object? handler = impl is SafeHandle ? impl : impl?.GetType().GetField("_handler", Any)?.GetValue(impl);
        return handler is SafeHandle safe && !safe.IsInvalid ? safe.DangerousGetHandle() : IntPtr.Zero;
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
        catch (Exception ex)
        {
            Enabled = false;
            Console.WriteLine($"Mic: echo cancellation turned off ({ex.Message}).");
        }
    }

    /// <summary>Echo cancellation + voice processing for one 10 ms mic frame (in place).</summary>
    public static void Process(short[] mic, int count)
    {
        if (count != FrameSize)
        {
            return;
        }

        if (Enabled)
        {
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
                Enabled = false;
                Console.WriteLine($"Mic: echo cancellation turned off ({ex.Message}).");
            }
        }

        if (_preEnabled)
        {
            try
            {
                lock (Lock)
                {
                    _pre!.Run(mic);
                }
            }
            catch (Exception ex)
            {
                _preEnabled = false;
                Console.WriteLine($"Mic: voice processing turned off ({ex.Message}).");
            }
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            Enabled = false;
            _preEnabled = false;
            _pre?.Dispose();
            _pre = null;
            _aec?.Dispose();
            _aec = null;
            Playback.Clear();
        }
    }
}
