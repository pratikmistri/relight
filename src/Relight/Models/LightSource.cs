namespace Relight.Models;

/// <summary>A single virtual light. Positions are in image UV space.</summary>
public sealed class LightSource
{
    public float X { get; set; } = 0.34f;

    public float Y { get; set; } = 0.34f;

    /// <summary>Depth of the light, between <see cref="RelightSettings.LightZMin"/> and <see cref="RelightSettings.LightZMax"/>.</summary>
    public float Z { get; set; } = 0.42f;

    public float ColorR { get; set; } = 1f;

    public float ColorG { get; set; } = 0.72f;

    public float ColorB { get; set; } = 0.46f;

    public float Intensity { get; set; } = 3f;

    /// <summary>Ray-marched shadows are the most expensive term, so only key lights cast them.</summary>
    public bool CastsShadow { get; set; } = true;

    public void Set(float x, float y, float z, float r, float g, float b, float intensity, bool castsShadow)
    {
        X = x;
        Y = y;
        Z = z;
        ColorR = r;
        ColorG = g;
        ColorB = b;
        Intensity = intensity;
        CastsShadow = castsShadow;
    }
}
