using System;

namespace Relight.Services;

/// <summary>
/// Meters how bright the room already is and turns it into a smoothed gain, so a single preset
/// reads the same in a dim room and a well-lit one instead of blowing out or staying murky.
/// </summary>
public sealed class SceneExposure
{
    /// <summary>
    /// Mean frame luminance the meter aims for. Deliberately below mid-grey: the relit look wants
    /// the ambient fill to sit low so the key light does the shaping.
    /// </summary>
    private const float TargetLuma = 0.26f;

    private const float MinimumGain = 0.55f;
    private const float MaximumGain = 2.2f;

    /// <summary>Slow on purpose: a fast meter pumps whenever the subject moves.</summary>
    private const float SmoothingSeconds = 1.2f;

    /// <summary>Sample every Nth pixel on each axis; the mean does not need every texel.</summary>
    private const int SampleStride = 8;

    /// <summary>Guards against dividing by an essentially black frame.</summary>
    private const float MinimumLuma = 0.02f;

    private float _gain = 1f;
    private bool _seeded;

    /// <summary>Multiplier for the ambient term and every light intensity.</summary>
    public float Gain => _gain;

    /// <summary>Drops the smoothing history so the next frame seeds the gain outright.</summary>
    public void Reset()
    {
        _gain = 1f;
        _seeded = false;
    }

    public void Update(ReadOnlySpan<byte> bgra, int width, int height, float deltaSeconds)
    {
        float luma = MeasureLuma(bgra, width, height);
        if (luma <= 0f)
        {
            return;
        }

        float target = Math.Clamp(TargetLuma / MathF.Max(luma, MinimumLuma), MinimumGain, MaximumGain);
        if (!_seeded)
        {
            _seeded = true;
            _gain = target;
            return;
        }

        float blend = 1f - MathF.Exp(-MathF.Max(deltaSeconds, 0.0001f) / SmoothingSeconds);
        _gain += (target - _gain) * blend;
    }

    private static float MeasureLuma(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            return 0f;
        }

        double sum = 0;
        int count = 0;
        for (int y = 0; y < height; y += SampleStride)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x += SampleStride)
            {
                int index = row + (x * 4);
                sum += (0.0722 * bgra[index]) + (0.7152 * bgra[index + 1]) + (0.2126 * bgra[index + 2]);
                count++;
            }
        }

        return count > 0 ? (float)(sum / count / 255.0) : 0f;
    }
}
