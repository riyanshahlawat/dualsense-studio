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
    private static SpeexDSPEchoCanceler? _aec;
    private static SpeexDSPPreprocessor? _pre;
    private static readonly short[] _farFrame = new short[FrameSize];
    private static readonly short[] _cleaned = new short[FrameSize];

    public static bool Enabled { get; private set; }
    private static bool _preEnabled;
    private static bool _voiceGate;
    private static int _holdFrames;
    private static float _gate;

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
            int noiseDb = -35; // stronger noise suppression
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref noiseDb);
            // No auto gain: it boosted keyboard clicks and room noise.
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref off);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DEREVERB, ref on);
            // Voice detection: with noise removal on, the mic only opens while you talk.
            int vad = noiseSuppression ? 1 : 0;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_VAD, ref vad);
            int probStart = 85, probContinue = 65;
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_PROB_START, ref probStart);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_PROB_CONTINUE, ref probContinue);
            _voiceGate = noiseSuppression;
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
            Console.WriteLine($"Mic: voice processing on (de-reverb{(noiseSuppression ? ", noise suppression, voice-only gate" : "")}).");
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

    // --- Echo delay alignment ---------------------------------------------------
    // Over Bluetooth the echo comes back much later than a normal headset (PC buffer +
    // controller buffer + radio, both ways), often beyond the canceller's 200 ms tail.
    // So the headphone reference is kept in a 2.5 s history, the real echo delay is
    // measured by cross-correlating it with the mic every few seconds, and the
    // canceller is fed the reference from that moment (with 20 ms to spare).
    private const int RefHistory = 120000;          // 2.5 s at 48 kHz
    private const int MicHistory = 48000;           // 1 s
    private const int Decimate = 12;                // 48 kHz -> 4 kHz for the search
    private const int MarginSamples = 960;          // 20 ms
    private static readonly short[] _ref = new short[RefHistory];
    private static readonly short[] _micHist = new short[MicHistory];
    private static long _refWrite, _micWrite;
    private static int _delaySamples = 48 * 300;    // start guess: ~300 ms (measured on Bluetooth)
    private static int _candidateMs = -1000;
    private static long _nextEstimate;
    private static int _estimating;

    public static int MeasuredDelayMs { get; private set; } = -1;

    /// <summary>Headphone audio (interleaved stereo float, 48 kHz) as the echo reference.</summary>
    public static void AddPlayback(float[] stereo)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Lock)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                float mono = (stereo[i] + stereo[i + 1]) * 0.5f;
                _ref[_refWrite % RefHistory] = (short)Math.Clamp(mono * 32767f, -32768f, 32767f);
                _refWrite++;
            }
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
                bool cancelled = false;
                lock (Lock)
                {
                    for (int i = 0; i < FrameSize; i++)
                    {
                        _micHist[_micWrite % MicHistory] = mic[i];
                        _micWrite++;
                    }

                    long start = _refWrite - _delaySamples - FrameSize;
                    if (start >= 0 && _refWrite - start < RefHistory)
                    {
                        for (int i = 0; i < FrameSize; i++)
                        {
                            _farFrame[i] = _ref[(start + i) % RefHistory];
                        }
                        _aec!.EchoCancel(mic, _farFrame, _cleaned);
                        cancelled = true;
                    }
                }

                if (cancelled)
                {
                    Array.Copy(_cleaned, mic, FrameSize);
                }

                MaybeEstimateDelay();
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
                int speech;
                lock (Lock)
                {
                    speech = _pre!.Run(mic);
                }

                if (_voiceGate)
                {
                    // Open instantly on speech, stay open 400 ms after (word endings),
                    // then fade out: keyboard clicks, fans and breathing stay silent.
                    if (speech == 1) _holdFrames = 40;
                    else if (_holdFrames > 0) _holdFrames--;
                    float target = _holdFrames > 0 ? 1f : 0f;
                    for (int i = 0; i < FrameSize; i++)
                    {
                        _gate += (target - _gate) * (target > _gate ? 0.02f : 0.002f);
                        mic[i] = (short)(mic[i] * _gate);
                    }
                }
            }
            catch (Exception ex)
            {
                _preEnabled = false;
                Console.WriteLine($"Mic: voice processing turned off ({ex.Message}).");
            }
        }
    }

    // Every 3 s: find the lag where the mic best matches the headphone audio.
    private static void MaybeEstimateDelay()
    {
        long now = Environment.TickCount64;
        if (now < _nextEstimate || _micWrite < MicHistory || _refWrite < RefHistory)
        {
            return;
        }
        _nextEstimate = now + 3000;
        if (Interlocked.Exchange(ref _estimating, 1) == 1)
        {
            return;
        }

        int micLen = MicHistory / Decimate, refLen = RefHistory / Decimate;
        var mic4 = new float[micLen];
        var ref4 = new float[refLen];
        lock (Lock)
        {
            Downsample(_micHist, _micWrite, MicHistory, mic4);
            Downsample(_ref, _refWrite, RefHistory, ref4);
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                double micEnergy = 0;
                foreach (float v in mic4) micEnergy += v * v;
                if (micEnergy < 1e-3)
                {
                    return;
                }

                int maxLag = refLen - micLen;          // up to 1.5 s
                // The Bluetooth round trip can't be under ~100 ms; shorter "matches" are
                // your own voice (e.g. hear-myself), not echo.
                int minLag = 100 * 48 / Decimate;
                int bestLag = -1;
                double best = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    int offset = refLen - micLen - lag; // mic[i] vs ref[offset + i]
                    double dot = 0, refEnergy = 0;
                    for (int i = 0; i < micLen; i++)
                    {
                        float r = ref4[offset + i];
                        dot += mic4[i] * r;
                        refEnergy += r * r;
                    }
                    if (refEnergy < 1e-3) continue;
                    double corr = Math.Abs(dot) / Math.Sqrt(micEnergy * refEnergy);
                    if (corr > best)
                    {
                        best = corr;
                        bestLag = lag;
                    }
                }

                if (bestLag < 0 || best < 0.3)
                {
                    return; // no clear echo right now (silence or you talking)
                }

                int delay = Math.Max(0, bestLag * Decimate - MarginSamples);
                int ms = bestLag * Decimate / 48;

                // Only re-align when two measurements in a row agree (within 20 ms);
                // one-off false matches would otherwise reset the canceller.
                bool confirmed = Math.Abs(ms - _candidateMs) <= 20;
                _candidateMs = ms;
                if (!confirmed)
                {
                    return;
                }

                lock (Lock)
                {
                    if (Math.Abs(delay - _delaySamples) > 48 * 15)
                    {
                        _delaySamples = delay;
                        _aec?.Reset(); // re-learn at the new alignment
                        Console.WriteLine($"Mic: echo delay measured at {ms} ms (match {best:0.00}); canceller aligned.");
                    }
                }
                MeasuredDelayMs = ms;
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _estimating, 0);
            }
        });
    }

    private static void Downsample(short[] ring, long write, int size, float[] target)
    {
        long start = write - size;
        for (int j = 0; j < target.Length; j++)
        {
            float sum = 0;
            for (int k = 0; k < Decimate; k++)
            {
                sum += ring[(start + j * Decimate + k) % size] / 32768f;
            }
            target[j] = sum / Decimate;
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
        }
    }
}
