using CommunityToolkit.Mvvm.ComponentModel;

namespace Relight.Models;

/// <summary>
/// A single virtual light. Positions are in image UV space. Observable so the custom-mode pane
/// and the drag handles stay in step with whatever moved the light.
/// </summary>
public sealed class LightSource : ObservableObject
{
    private float _x = 0.34f;
    private float _y = 0.34f;
    private float _z = 0.42f;
    private float _colorR = 1f;
    private float _colorG = 0.72f;
    private float _colorB = 0.46f;
    private float _intensity = 3f;
    private bool _castsShadow = true;

    public float X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public float Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    /// <summary>Depth of the light, between <see cref="RelightSettings.LightZMin"/> and <see cref="RelightSettings.LightZMax"/>.</summary>
    public float Z
    {
        get => _z;
        set => SetProperty(ref _z, value);
    }

    public float ColorR
    {
        get => _colorR;
        set => SetProperty(ref _colorR, value);
    }

    public float ColorG
    {
        get => _colorG;
        set => SetProperty(ref _colorG, value);
    }

    public float ColorB
    {
        get => _colorB;
        set => SetProperty(ref _colorB, value);
    }

    public float Intensity
    {
        get => _intensity;
        set => SetProperty(ref _intensity, value);
    }

    /// <summary>Ray-marched shadows are the most expensive term, so only key lights cast them.</summary>
    public bool CastsShadow
    {
        get => _castsShadow;
        set => SetProperty(ref _castsShadow, value);
    }

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
