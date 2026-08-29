using System;

namespace Relight.Services;

/// <summary>
/// Robust low/high bounds for a disparity field, plus the exponential blend that keeps
/// the normalisation from flickering between frames (the WGSL `stabilizeRange` kernel).
/// </summary>
public sealed class DisparityRange
{
    private const float RangeBlend = 0.12f;
    private const int Bins = 512;
    private const float LowPercentile = 0.02f;
    private const float HighPercentile = 0.98f;

    private readonly int[] _histogram = new int[Bins];
    private bool _seeded;

    public float Low { get; private set; }

    public float High { get; private set; } = 1f;

    public float Span => MathF.Max(High - Low, 0.001f);

    public void Reset() => _seeded = false;

    /// <summary>Measures <paramref name="disparity"/> and blends the result into the stable range.</summary>
    public void Accumulate(ReadOnlySpan<float> disparity)
    {
        float minimum = float.MaxValue;
        float maximum = float.MinValue;
        foreach (float value in disparity)
        {
            if (float.IsNaN(value))
            {
                continue;
            }

            minimum = MathF.Min(minimum, value);
            maximum = MathF.Max(maximum, value);
        }

        if (minimum > maximum)
        {
            return;
        }

        float span = MathF.Max(maximum - minimum, 1e-6f);
        Array.Clear(_histogram);
        foreach (float value in disparity)
        {
            if (float.IsNaN(value))
            {
                continue;
            }

            int bin = (int)((value - minimum) / span * (Bins - 1));
            _histogram[Math.Clamp(bin, 0, Bins - 1)]++;
        }

        int total = disparity.Length;
        float frameLow = Percentile(minimum, span, total, LowPercentile);
        float frameHigh = MathF.Max(Percentile(minimum, span, total, HighPercentile), frameLow + 0.001f);

        if (!_seeded)
        {
            Low = frameLow;
            High = frameHigh;
            _seeded = true;
            return;
        }

        Low += (frameLow - Low) * RangeBlend;
        High += (frameHigh - High) * RangeBlend;
    }

    private float Percentile(float minimum, float span, int total, float fraction)
    {
        int target = (int)(total * fraction);
        int running = 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            running += _histogram[bin];
            if (running >= target)
            {
                return minimum + (bin / (float)(Bins - 1) * span);
            }
        }

        return minimum + span;
    }
}
