using System;

namespace Relight.Services;

/// <summary>
/// Produces the irregular brightness wobble of an open flame. A steady lamp is what makes a
/// candle preset read as "orange light" rather than a candle, so the key light is modulated
/// rather than the whole frame.
/// </summary>
public static class CandleFlicker
{
    /// <summary>
    /// Multiplier for the key light's intensity at <paramref name="seconds"/>.
    /// Returns 1 when <paramref name="amplitude"/> is zero, so steady presets pay nothing.
    /// </summary>
    public static float Evaluate(double seconds, float amplitude)
    {
        if (amplitude <= 0f)
        {
            return 1f;
        }

        // Layered sines at deliberately incommensurate rates never repeat on a human timescale,
        // which reads as random without keeping any state or allocating.
        double wobble =
            (Math.Sin(seconds * 11.3) * 0.50) +
            (Math.Sin(seconds * 6.7) * 0.30) +
            (Math.Sin(seconds * 19.1) * 0.14) +
            (Math.Sin(seconds * 29.7) * 0.06);

        // A flame dips further than it spikes, so bias the swing downward.
        double signed = wobble < 0 ? wobble * 1.35 : wobble;
        return (float)Math.Max(0.25, 1.0 + (signed * amplitude));
    }
}
