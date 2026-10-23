// Audio-to-haptics: low-passes the game audio (bass, impacts) and downsamples
// a 512-frame window to 32 stereo int8 frames for the DualSense haptic motors
// (vDS resample_haptics_pcm layout).
internal static class Haptics
{
    public static bool Enabled;

    private const float Gain = 2.2f;
    private static float _lowLeft, _lowRight;

    public static void Fill(float[] window, Span<byte> haptics)
    {
        haptics.Clear();
        int frames = window.Length / 2;
        if (frames == 0)
        {
            return;
        }

        // One-pole low-pass around 160 Hz keeps what you feel, drops voices/music detail.
        const float alpha = 0.02f;
        const int outFrames = 32;
        for (int frame = 0; frame < outFrames; frame++)
        {
            int begin = frame * frames / outFrames;
            int end = (frame + 1) * frames / outFrames;
            float left = 0, right = 0;
            for (int i = begin; i < end; i++)
            {
                _lowLeft += alpha * (window[i * 2] - _lowLeft);
                _lowRight += alpha * (window[i * 2 + 1] - _lowRight);
                left += _lowLeft;
                right += _lowRight;
            }

            int count = Math.Max(1, end - begin);
            haptics[frame * 2] = (byte)(sbyte)Math.Clamp(left / count * Gain * 127f, -127f, 127f);
            haptics[frame * 2 + 1] = (byte)(sbyte)Math.Clamp(right / count * Gain * 127f, -127f, 127f);
        }
    }
}
