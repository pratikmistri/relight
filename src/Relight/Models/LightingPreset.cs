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

    /// <summary>
    /// Ordered for a webcam: the natural, flattering looks come first so the default preset is a
    /// soft studio key. Coloured lights are kept low and non-shadowing so they read as a glow on
    /// the face rather than a hard cast with sharp silhouette lines.
    /// </summary>
    public static IReadOnlyList<LightingPreset> All { get; } =
    [
        new LightingPreset
        {
            Name = "Studio Soft",
            Description = "Broad neutral key with an even fill",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.30f, relief: 0.85f, specular: 0.20f, shadow: 0.45f, occlusion: 0.45f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.36f, 0.28f, 0.70f, 1.00f, 0.98f, 0.95f, 2.6f, true);
                settings.Lights[1].Set(0.70f, 0.58f, 0.62f, 0.96f, 0.98f, 1.00f, 0.7f, false);
            },
        },
        new LightingPreset
        {
            Name = "Ring Light",
            Description = "Even frontal light, almost no shadow",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.34f, relief: 0.70f, specular: 0.22f, shadow: 0.25f, occlusion: 0.35f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.50f, 0.44f, 0.95f, 1.00f, 0.99f, 0.97f, 2.6f, false);
            },
        },
        new LightingPreset
        {
            Name = "Editorial",
            Description = "Soft key, cool fill and a gentle rim",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.24f, relief: 0.95f, specular: 0.22f, shadow: 0.60f, occlusion: 0.52f);
                settings.LightCount = 3;
                settings.Lights[0].Set(0.34f, 0.28f, 0.65f, 1.00f, 0.97f, 0.93f, 2.7f, true);
                settings.Lights[1].Set(0.72f, 0.56f, 0.58f, 0.88f, 0.93f, 1.00f, 0.65f, false);
                settings.Lights[2].Set(0.86f, 0.22f, 0.32f, 1.00f, 0.98f, 0.95f, 1.0f, false);
            },
        },
        new LightingPreset
        {
            Name = "Rembrandt",
            Description = "Warm key high on one side, with the shadow lifted",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.20f, relief: 1.00f, specular: 0.20f, shadow: 0.70f, occlusion: 0.58f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.30f, 0.24f, 0.58f, 1.00f, 0.96f, 0.90f, 2.9f, true);
                settings.Lights[1].Set(0.74f, 0.62f, 0.60f, 0.95f, 0.97f, 1.00f, 0.45f, false);
            },
        },
        new LightingPreset
        {
            Name = "Butterfly",
            Description = "Key above centre with a soft under-fill",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.28f, relief: 0.85f, specular: 0.22f, shadow: 0.50f, occlusion: 0.45f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.50f, 0.18f, 0.68f, 1.00f, 0.98f, 0.96f, 2.7f, true);
                settings.Lights[1].Set(0.50f, 0.88f, 0.55f, 0.94f, 0.96f, 1.00f, 0.60f, false);
            },
        },
        new LightingPreset
        {
            Name = "Clamshell",
            Description = "Even beauty lighting from above and below",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.32f, relief: 0.75f, specular: 0.24f, shadow: 0.35f, occlusion: 0.38f);
                settings.LightCount = 2;
                settings.Lights[0].Set(0.50f, 0.22f, 0.70f, 1.00f, 0.99f, 0.97f, 2.5f, true);
                settings.Lights[1].Set(0.50f, 0.84f, 0.64f, 1.00f, 0.98f, 0.96f, 1.0f, false);
            },
        },
        new LightingPreset
        {
            Name = "Three-Point",
            Description = "Classic key, cool fill and a soft rim",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.26f, relief: 0.92f, specular: 0.22f, shadow: 0.55f, occlusion: 0.50f);
                settings.LightCount = 3;
                settings.Lights[0].Set(0.32f, 0.28f, 0.62f, 1.00f, 0.97f, 0.92f, 2.7f, true);
                settings.Lights[1].Set(0.72f, 0.58f, 0.55f, 0.92f, 0.95f, 1.00f, 0.60f, false);
                settings.Lights[2].Set(0.88f, 0.20f, 0.28f, 1.00f, 0.99f, 0.97f, 0.95f, false);
            },
        },
        new LightingPreset
        {
            Name = "Golden Hour",
            Description = "Low warm sun with a soft bounce",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.30f, relief: 0.80f, specular: 0.22f, shadow: 0.70f, occlusion: 0.50f);
                settings.LightCount = 2;

                // The deep amber key is the whole character of this one; a near-white key just
                // reads as a slightly warm room.
                settings.Lights[0].Set(0.12f, 0.62f, 0.38f, 1.00f, 0.72f, 0.36f, 3.1f, true);
                settings.Lights[1].Set(0.75f, 0.30f, 0.28f, 1.00f, 0.85f, 0.60f, 1.1f, false);
            },
        },
        new LightingPreset
        {
            Name = "Twilight Glow",
            Description = "Neutral key with soft magenta and cyan glow",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.24f, relief: 0.88f, specular: 0.24f, shadow: 0.40f, occlusion: 0.45f);
                settings.LightCount = 3;

                // The key stays near-neutral so skin keeps its colour; the tints are unshadowed
                // fills, which is what keeps them a glow instead of a hard coloured edge.
                settings.Lights[0].Set(0.46f, 0.38f, 0.82f, 1.00f, 0.97f, 0.95f, 2.4f, true);
                settings.Lights[1].Set(0.14f, 0.42f, 0.45f, 1.00f, 0.62f, 0.86f, 0.95f, false);
                settings.Lights[2].Set(0.86f, 0.50f, 0.45f, 0.62f, 0.88f, 1.00f, 0.95f, false);
            },
        },
        new LightingPreset
        {
            Name = "Candlelit",
            Description = "Close, warm and dim with strong relief",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.10f, relief: 1.20f, specular: 0.18f, shadow: 0.90f, occlusion: 0.75f);
                settings.LightCount = 1;
                settings.Lights[0].Set(0.38f, 0.72f, 0.80f, 1.00f, 0.62f, 0.24f, 2.6f, true);
            },
        },
        new LightingPreset
        {
            Name = "Neon Nights",
            Description = "Opposing magenta and cyan glow, no cast shadows",
            Apply = settings =>
            {
                Globals(settings, exposure: 0.16f, relief: 1.05f, specular: 0.30f, shadow: 0.45f, occlusion: 0.55f);
                settings.LightCount = 2;

                // Neither source casts. Two near point lights on opposite sides each throw a
                // diverging shadow wedge from the head, and the two wedges cross into a hard
                // cone. Unshadowed, the same tints wrap the face and diffuse into each other,
                // which is the look this preset is actually after. Form still comes from the
                // lambert wrap and the height-field occlusion.
                settings.Lights[0].Set(0.16f, 0.34f, 0.42f, 1.00f, 0.22f, 0.75f, 3.0f, false);
                settings.Lights[1].Set(0.86f, 0.55f, 0.42f, 0.20f, 0.85f, 1.00f, 2.6f, false);
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
