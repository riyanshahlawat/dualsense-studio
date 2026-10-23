using NAudio.Dsp;

// Continuous 51.2 kHz -> 48 kHz stereo resampler (the DualSense "ClockFix":
// 1024 PCM frames per 960 Opus frames), matching DS5Dongle's WDL setup.
// Unlike a per-report linear resample, it keeps filter state across reports,
// so there is no click at every report boundary.
internal sealed class ClockResampler
{
    private const int Channels = 2;
    private readonly WdlResampler _resampler = new();
    private readonly Queue<float> _pending = new();
    private float[] _scratch = new float[4096];
    private bool _primed;

    public ClockResampler()
    {
        // Longer sinc filter: cleaner highs, less aliasing (cheap on a PC).
        _resampler.SetMode(true, 0, true, 64, 32);
        _resampler.SetRates(51200, 48000);
        _resampler.SetFeedMode(true);
    }

    public void Process(float[] input, int inputFrames, float[] output, int outputFrames)
    {
        int needed = _resampler.ResamplePrepare(inputFrames, Channels, out float[] inBuffer, out int inOffset);
        Array.Copy(input, 0, inBuffer, inOffset, needed * Channels);

        int maxOut = outputFrames * 2;
        if (_scratch.Length < maxOut * Channels)
        {
            _scratch = new float[maxOut * Channels];
        }
        int produced = _resampler.ResampleOut(_scratch, 0, needed, maxOut, Channels);
        for (int i = 0; i < produced * Channels; i++)
        {
            _pending.Enqueue(_scratch[i]);
        }

        // Bound drift: never let more than two reports of output pile up.
        while (_pending.Count > outputFrames * Channels * 2)
        {
            _pending.Dequeue();
        }

        int samples = outputFrames * Channels;

        // Prime with half a report of slack to absorb the filter delay;
        // otherwise every report would be a few frames short and click.
        if (!_primed)
        {
            if (_pending.Count < samples + samples / 2)
            {
                Array.Clear(output, 0, samples);
                return;
            }
            _primed = true;
        }

        int missing = Math.Max(0, samples - _pending.Count);
        Array.Clear(output, 0, missing);
        for (int i = missing; i < samples; i++)
        {
            output[i] = _pending.Dequeue();
        }
    }
}
