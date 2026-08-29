using System;
using System.Collections.Generic;

namespace Relight.Models;

/// <summary>
/// A named lighting mood: global response plus the light rig that produces it.
/// Positions follow photographic conventions, with the key light first so the pointer steers it.
/// </summary>
public sealed class LightingPreset
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required Action<RelightSettings> Apply { get; init; }

    public static IReadOnlyList<LightingPreset> All { get; } =
    [
        new LightingPreset
        {
            Name = "Rembrandt",
            Description = "Single warm key high on one side, deep falloff",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.22f, relief: 0.9f, specular: 0.25f, shadow: 0.85f, occlusion: 0.65f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.30f, 0.22f, 0.55f, 1.00f, 0.93f, 0.84f, 3.2f, true);
            },
        },
        new LightingPreset
        {
            Name = "Butterfly",
            Description = "Key above centre with a soft under-fill",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.35f, relief: 0.7f, specular: 0.30f, shadow: 0.55f, occlusion: 0.45f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.50f, 0.16f, 0.62f, 1.00f, 0.96f, 0.92f, 3.0f, true);
                settings.Lights[1].Set(0.50f, 0.92f, 0.50f, 0.85f, 0.90f, 1.00f, 0.9f, false);
            },
        },
        new LightingPreset
        {
            Name = "Split",
            Description = "Hard edge light from the side, half in shadow",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.12f, relief: 1.0f, specular: 0.30f, shadow: 1.0f, occlusion: 0.70f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.05f, 0.42f, 0.30f, 1.00f, 0.95f, 0.88f, 3.4f, true);
            },
        },
        new LightingPreset
        {
            Name = "Three-Point",
            Description = "Classic key, cool fill and a bright rim",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.30f, relief: 0.85f, specular: 0.28f, shadow: 0.60f, occlusion: 0.50f);
                settings.LightCount = 3;
                settings.Lights[0].Set(0.32f, 0.28f, 0.55f, 1.00f, 0.94f, 0.86f, 2.8f, true);
                settings.Lights[1].Set(0.72f, 0.55f, 0.45f, 0.80f, 0.86f, 1.00f, 1.0f, false);
                settings.Lights[2].Set(0.88f, 0.16f, 0.10f, 1.00f, 1.00f, 1.00f, 1.6f, false);
            },
        },
        new LightingPreset
        {
            Name = "Neon Nights",
            Description = "Opposing magenta and cyan sources",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.14f, relief: 1.1f, specular: 0.45f, shadow: 0.75f, occlusion: 0.60f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.16f, 0.34f, 0.42f, 1.00f, 0.22f, 0.75f, 3.0f, true);
                settings.Lights[1].Set(0.86f, 0.55f, 0.42f, 0.20f, 0.85f, 1.00f, 2.6f, true);
            },
        },
        new LightingPreset
        {
            Name = "Golden Hour",
            Description = "Low warm sun with a soft bounce",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.32f, relief: 0.75f, specular: 0.22f, shadow: 0.70f, occlusion: 0.50f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.12f, 0.62f, 0.35f, 1.00f, 0.72f, 0.36f, 3.2f, true);
                settings.Lights[1].Set(0.75f, 0.30f, 0.25f, 1.00f, 0.85f, 0.60f, 1.1f, false);
            },
        },
        new LightingPreset
        {
            Name = "Candlelit",
            Description = "Close, warm and dim with strong relief",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.10f, relief: 1.2f, specular: 0.18f, shadow: 0.90f, occlusion: 0.75f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.38f, 0.72f, 0.80f, 1.00f, 0.62f, 0.24f, 2.6f, true);
            },
        },
        new LightingPreset
        {
            Name = "Clamshell",
            Description = "Even beauty lighting from above and below",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.45f, relief: 0.55f, specular: 0.35f, shadow: 0.35f, occlusion: 0.35f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.50f, 0.20f, 0.60f, 1.00f, 0.97f, 0.94f, 2.4f, true);
                settings.Lights[1].Set(0.50f, 0.85f, 0.60f, 1.00f, 0.95f, 0.90f, 1.5f, false);
            },
        },
        new LightingPreset
        {
            Name = "Moonlight",
            Description = "Cool single source, high and distant",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.16f, relief: 1.0f, specular: 0.32f, shadow: 0.85f, occlusion: 0.65f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.62f, 0.18f, 0.45f, 0.62f, 0.76f, 1.00f, 3.0f, true);
            },
        },
    ];

    private static void Globals(
        RelightSettings settings,
        float exposure,
        float relief,
        float specular,
        float shadow,
        float occlusion)
    {
        settings.Exposure = exposure;
        settings.Relief = relief;
        settings.Specular = specular;
        settings.Shadow = shadow;
        settings.Occlusion = occlusion;
    }
}
