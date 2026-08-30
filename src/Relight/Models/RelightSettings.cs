namespace Relight.Models;

/// <summary>Which image the relighting pipeline presents.</summary>
public enum RelightMode
{
    Relit = 0,
    Camera = 1,
    Depth = 2,
    Normals = 3,
}

/// <summary>How much of the virtual light source itself is drawn over the relit image.</summary>
public enum BulbVisibility
{
    /// <summary>The solid bulb and its glow.</summary>
    Full = 0,

    /// <summary>Only the halo, without the solid disc.</summary>
    Glow = 1,

    /// <summary>Nothing; the light shapes the scene but is never seen.</summary>
    Hidden = 2,
}

/// <summary>Relighting state shared between the UI and the GPU pipeline.</summary>
public sealed class RelightSettings
{
    /// <summary>Furthest surface plane the relit scene can hold, matching SURFACE_FAR_Z in the shader.</summary>
    public const float SurfaceFarZ = -0.7f;

    /// <summary>Must match MAX_LIGHTS in the shader.</summary>
    public const int MaxLights = 6;

    private const float LightZClearance = 0.04f;

    public const float LightZMin = SurfaceFarZ + LightZClearance;
    public const float LightZMax = 1.65f;

    /// <summary>Fixed-size pool; <see cref="LightCount"/> decides how many are active.</summary>
    public LightSource[] Lights { get; } = CreateLights();

    public int LightCount { get; set; } = 1;

    /// <summary>The light steered by the pointer.</summary>
    public LightSource KeyLight => Lights[0];

    public bool Mirror { get; set; } = true;

    public float Exposure { get; set; } = 0.5f;

    public float Relief { get; set; } = 0.85f;

    public float Specular { get; set; } = 0.22f;

    public float Shadow { get; set; } = 0.7f;

    public float Occlusion { get; set; } = 0.55f;

    public RelightMode Mode { get; set; } = RelightMode.Relit;

    /// <summary>
    /// How the light sources themselves are drawn. Presets deliberately leave this alone so the
    /// choice survives switching moods.
    /// </summary>
    public BulbVisibility Bulb { get; set; } = BulbVisibility.Hidden;

    /// <summary>
    /// Blends the relit result back toward the untouched camera image. 1 is the full effect,
    /// 0 is the plain camera. Presets leave it alone so it stays a user preference.
    /// </summary>
    public float Strength { get; set; } = 0.85f;

    /// <summary>
    /// Gain from the auto-exposure meter. Presets own <see cref="Exposure"/> as the intent;
    /// this scales the whole relight response so one preset reads the same in any room.
    /// </summary>
    public float ExposureGain { get; set; } = 1f;

    private static LightSource[] CreateLights()
    {
        var lights = new LightSource[MaxLights];
        for (int index = 0; index < lights.Length; index++)
        {
            lights[index] = new LightSource();
        }

        return lights;
    }
}
