namespace Relight.Models;

/// <summary>Which image the relighting pipeline presents.</summary>
public enum RelightMode
{
    Relit = 0,
    Camera = 1,
    Depth = 2,
    Normals = 3,
}

/// <summary>Relighting state shared between the UI and the GPU pipeline.</summary>
public sealed class RelightSettings
{
    /// <summary>Furthest surface plane the relit scene can hold, matching SURFACE_FAR_Z in the shader.</summary>
    public const float SurfaceFarZ = -0.7f;

    /// <summary>Must match MAX_LIGHTS in the shader.</summary>
    public const int MaxLights = 4;

    private const float LightZClearance = 0.04f;

    public const float LightZMin = SurfaceFarZ + LightZClearance;
    public const float LightZMax = 1.65f;

    /// <summary>Fixed-size pool; <see cref="LightCount"/> decides how many are active.</summary>
    public LightSource[] Lights { get; } =
        [new LightSource(), new LightSource(), new LightSource(), new LightSource()];

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
}
