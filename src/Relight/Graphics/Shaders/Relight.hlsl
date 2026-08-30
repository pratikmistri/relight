// Port of the TypeGPU "Monocular Light Injection" WGSL kernels to HLSL.
// Reference: TypeGPU examples/image-processing/monocular-light-injection/shaders.ts

// ---------------------------------------------------------------------------
// Tunables (identical to the WGSL original, which was tuned for a 448px field)
// ---------------------------------------------------------------------------

#define MOTION_LOW     0.02
#define MOTION_HIGH    0.09

// The reference example blended with fixed per-frame alphas (0.32 steady, 0.8 in motion)
// assuming a 60 fps depth update. Depth here updates far slower, so those alphas are expressed
// as time constants and resolved against the real elapsed time; otherwise old frames smear.
#define TEMPORAL_TAU 0.0432
#define MOTION_TAU   0.0104

#define GRADIENT_RADIUS       7
#define GRADIENT_BACK         (-GRADIENT_RADIUS)
#define GRADIENT_LIMIT        0.009
#define GRADIENT_NOISE        0.0003
#define GRADIENT_NOISE_ENERGY (GRADIENT_NOISE * GRADIENT_NOISE)
#define OCCLUSION_TAPS        16
#define OCCLUSION_SCALE       0.07
#define OCCLUSION_RANGE       0.25
#define OCCLUSION_FLOOR       0.012

#define NEAR_Z             0.0
#define SURFACE_FAR_Z      (-0.7)
#define LIGHT_RADIUS       0.85
#define LIGHT_WRAP         0.34
#define RELIEF_SCALE       200.0
#define SLOPE_COMPRESSION  0.55
#define SPECULAR_POWER     36.0
#define SPECULAR_F0        0.06
#define GAMMA              2.2
#define WHITE_POINT        2.6
#define HIGHLIGHT_BLEACH   2.0
#define DITHER_STEP        (1.0 / 255.0)

#define BULB_WORLD_RADIUS       0.05
#define BULB_CAMERA_Z           2.0
#define BULB_REFERENCE_Z        0.42
#define BULB_CORE               8.0
#define BULB_LIMB               0.28
#define BULB_EDGE               0.75
#define BULB_EDGE_FLOOR         0.004
#define BULB_EDGE_LIMIT         0.3
#define BULB_HALO               1.6
#define BULB_HALO_SPAN          1.2
#define BULB_VEIL               0.12
#define BULB_VEIL_SPAN          4.0
#define BULB_ONSET              0.6
#define BULB_OCCLUSION_SOFTNESS 0.02
#define BULB_SOURCE_SOFTNESS    0.08
#define BULB_SAMPLE_SPREAD      0.6
#define BULB_SAMPLES            9.0

#define SHADOW_FAR_Z            (-1.25)
#define SHADOW_STEPS            32
#define SHADOW_SPAN             0.3
#define SHADOW_BASELINE         0.005
#define SHADOW_BIAS             0.014
#define SHADOW_SLOPE_BIAS       0.02
#define SHADOW_THICKNESS        0.7
#define SHADOW_THICKNESS_GROWTH 2.6
#define SHADOW_SOFTNESS         0.16
#define SHADOW_GAIN             2.5
#define SHADOW_FRONT_FADE       0.2

/// How much the shadow ramp widens with distance travelled along the ray. A real source has an
/// area, so its penumbra widens the further the occluder sits from the receiver; without this a
/// head throws a razor-edged wedge across a background metres behind it.
#define SHADOW_PENUMBRA_GROWTH  4.0

/// Depth edges are the least trustworthy part of a monocular height field, and a full-strength
/// Fresnel rim there paints a hard line around the silhouette. Damp it where the slope saturates.
#define RIM_EDGE_DAMP           0.3

#define MODE_RELIT   0
#define MODE_CAMERA  1
#define MODE_DEPTH   2
#define MODE_NORMALS 3

#define BULB_FULL   0
#define BULB_GLOW   1
#define BULB_HIDDEN 2

/// Maximum simultaneous light sources.
#define MAX_LIGHTS 6

static const float3 LUMINANCE_WEIGHTS = float3(0.2126, 0.7152, 0.0722);
static const float3 AMBIENT_FILL = float3(0.78, 0.86, 1.0);

// ---------------------------------------------------------------------------
// Shared bindings
// ---------------------------------------------------------------------------

cbuffer DepthParams : register(b0)
{
    uint2 FieldSize;
    uint  Reset;
    float RangeLow;
    float RangeSpan;
    float DeltaSeconds;
    float2 DepthPadding;
};

cbuffer RelightParams : register(b1)
{
    /// rgb = tint, a = intensity.
    float4 LightColors[MAX_LIGHTS];
    /// xy = position in UV space, z = depth, w = casts shadows when non-zero.
    float4 LightPositions[MAX_LIGHTS];
    float  Exposure;
    float  Relief;
    float  Specular;
    float  Shadow;
    float  Occlusion;
    /// World units covered by one output pixel; replaces fwidth for the bulb edge.
    float  PixelWorldSize;
    uint   Mirror;
    uint   Mode;
    uint   LightCount;
    /// Converts UV space into an isotropic world space: (width / height, 1).
    float2 WorldScale;
    /// One of the BULB_* constants; decides how much of the light source is drawn.
    uint   BulbMode;
    /// Blends the relit result back toward the untouched camera image; 1 is the full effect.
    float  Strength;
    float3 RelightPadding;
};

SamplerState LinearClamp : register(s0);

// ---------------------------------------------------------------------------
// Pass 1 - depth prepare: normalise the model disparity and temporally filter
// it into a fixed-size height field.
// ---------------------------------------------------------------------------

Texture2D<float> DisparityTexture : register(t0);
RWTexture2D<float> HistoryOut : register(u0);

[numthreads(8, 8, 1)]
void DepthPrepareCS(uint3 gid : SV_DispatchThreadID)
{
    if (gid.x >= FieldSize.x || gid.y >= FieldSize.y)
    {
        return;
    }

    float2 uv = (float2(gid.xy) + 0.5) / float2(FieldSize);
    float disparity = DisparityTexture.SampleLevel(LinearClamp, uv, 0);

    float normalized = 0.0;
    if (!isnan(disparity))
    {
        normalized = saturate((disparity - RangeLow) / max(RangeSpan, 0.001));
    }

    float filtered = normalized;
    if (Reset == 0)
    {
        float previous = HistoryOut[gid.xy];
        float motion = smoothstep(MOTION_LOW, MOTION_HIGH, abs(normalized - previous));
        float delta = max(DeltaSeconds, 0.0001);
        float steady = saturate(1.0 - exp(-delta / TEMPORAL_TAU));
        float moving = saturate(1.0 - exp(-delta / MOTION_TAU));
        filtered = lerp(previous, normalized, lerp(steady, moving, motion));
    }

    HistoryOut[gid.xy] = filtered;
}

// ---------------------------------------------------------------------------
// Pass 2 - surface: derive the surface slope and a height-field occlusion term.
// ---------------------------------------------------------------------------

Texture2D<float> DepthField : register(t1);
RWTexture2D<float4> SurfaceOut : register(u1);

float DepthTexelAt(int2 coord, int2 size)
{
    return DepthField.Load(int3(clamp(coord, int2(0, 0), size - 1), 0));
}

float GentlerDelta(float backward, float forward)
{
    float back = abs(backward);
    float front = abs(forward);
    return (backward * front + forward * back) / max(back + front, 1e-9);
}

float2 SurfaceSlope(float2 gradient)
{
    float steepness = max(length(gradient), 1e-9);
    float shrunk = sqrt(max(steepness * steepness - GRADIENT_NOISE_ENERGY, 0.0));
    float ceiling = GRADIENT_LIMIT * tanh(shrunk / GRADIENT_LIMIT);
    return gradient * (ceiling / steepness);
}

[numthreads(8, 8, 1)]
void SurfaceCS(uint3 gid : SV_DispatchThreadID)
{
    int2 size = int2(FieldSize);
    int2 coord = int2(gid.xy);
    if (coord.x >= size.x || coord.y >= size.y)
    {
        return;
    }

    float center = DepthTexelAt(coord, size);
    float left = DepthTexelAt(coord + int2(GRADIENT_BACK, 0), size);
    float right = DepthTexelAt(coord + int2(GRADIENT_RADIUS, 0), size);
    float up = DepthTexelAt(coord + int2(0, GRADIENT_BACK), size);
    float down = DepthTexelAt(coord + int2(0, GRADIENT_RADIUS), size);

    float2 rawGradient = float2(
        GentlerDelta(center - left, right - center),
        GentlerDelta(center - up, down - center)) / float(GRADIENT_RADIUS);
    float2 gradient = SurfaceSlope(rawGradient);

    float occlusion = 0.0;
    [unroll]
    for (int radiusIndex = 0; radiusIndex < 2; radiusIndex++)
    {
        int radius = radiusIndex == 0 ? 3 : 9;
        [unroll]
        for (int stepY = -1; stepY <= 1; stepY++)
        {
            [unroll]
            for (int stepX = -1; stepX <= 1; stepX++)
            {
                if (stepX != 0 || stepY != 0)
                {
                    float neighbor = DepthTexelAt(coord + int2(stepX * radius, stepY * radius), size);
                    float difference = neighbor - center;
                    float contact = 1.0 - saturate(abs(difference) / OCCLUSION_RANGE);
                    float cleared = max(difference - OCCLUSION_FLOOR, 0.0);
                    occlusion += saturate(cleared / OCCLUSION_SCALE) * contact;
                }
            }
        }
    }

    SurfaceOut[uint2(gid.xy)] = float4(
        gradient,
        1.0 - saturate(occlusion / float(OCCLUSION_TAPS)),
        center);
}

// ---------------------------------------------------------------------------
// Pass 3 - relight: the full-screen lighting shader.
// ---------------------------------------------------------------------------

Texture2D<float4> SurfaceTexture : register(t2);
Texture2D<float4> CameraTexture : register(t3);

struct VertexOutput
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VertexOutput FullScreenVS(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.uv = uv;
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return output;
}

float SurfaceZ(float depth)
{
    return lerp(SURFACE_FAR_Z, NEAR_Z, depth);
}

float ShadowZ(float depth)
{
    return lerp(SHADOW_FAR_Z, NEAR_Z, depth);
}

float DepthAt(float2 uv)
{
    return SurfaceTexture.SampleLevel(LinearClamp, uv, 0).w;
}

/// Samples the height field at a point expressed in world space.
float DepthAtWorld(float2 world)
{
    return DepthAt((world / WorldScale) + 0.5);
}

/// The relit image covers the whole camera frame, so only the mirror flip is applied.
float2 CameraUvAt(float2 uv)
{
    return Mirror != 0 ? float2(1.0 - uv.x, uv.y) : uv;
}

float Dither(float2 uv)
{
    float2 scaled = uv * 1024.0;
    return frac(52.9829189 * frac(0.06711056 * scaled.x + 0.00583715 * scaled.y));
}

float ShadowFactor(float3 origin, float3 lightDirection, float reach, float jitter, float lightZ)
{
    float stride = reach / float(SHADOW_STEPS);
    float baselineTravel = reach * (SHADOW_BASELINE / SHADOW_SPAN);
    float3 trailProbe = origin - lightDirection * baselineTravel;
    float receiverRise = max(
        origin.z - ShadowZ(DepthAtWorld(trailProbe.xy)) - baselineTravel * lightDirection.z,
        0.0);
    float risePerTravel = receiverRise / max(baselineTravel, 1e-9);

    float occlusion = 0.0;
    for (int step = 0; step < SHADOW_STEPS; step++)
    {
        float travel = (float(step) + jitter) * stride;
        float3 probe = origin + lightDirection * travel;
        float sampleZ = ShadowZ(DepthAtWorld(probe.xy));
        float difference = sampleZ - probe.z;
        float bias = SHADOW_BIAS + travel * (SHADOW_SLOPE_BIAS + risePerTravel);
        float thickness = SHADOW_THICKNESS * (1.0 + (travel / SHADOW_SPAN) * SHADOW_THICKNESS_GROWTH);
        if (difference > bias && difference < thickness)
        {
            float behindLight = 1.0 - saturate((sampleZ - lightZ) / SHADOW_FRONT_FADE);
            float softness = SHADOW_SOFTNESS * (1.0 + (travel / SHADOW_SPAN) * SHADOW_PENUMBRA_GROWTH);
            occlusion += saturate((difference - bias) / softness) * behindLight;
        }
    }

    return 1.0 - saturate((occlusion / float(SHADOW_STEPS)) * SHADOW_GAIN);
}

float3 DepthRamp(float value)
{
    float3 cold = float3(0.03, 0.02, 0.12);
    float3 middle = float3(0.11, 0.45, 0.94);
    float3 warm = float3(0.85, 0.36, 0.96);
    float3 hot = float3(0.97, 0.97, 0.87);
    if (value < 0.4)
    {
        return lerp(cold, middle, value / 0.4);
    }

    if (value < 0.75)
    {
        return lerp(middle, warm, (value - 0.4) / 0.35);
    }

    return lerp(warm, hot, (value - 0.75) / 0.25);
}

float BulbRadius(float lightZ)
{
    return BULB_WORLD_RADIUS * ((BULB_CAMERA_Z - BULB_REFERENCE_Z) / (BULB_CAMERA_Z - lightZ));
}

float BulbExposure(float radius, float2 lightUv, float lightZ)
{
    float open = 0.0;
    [unroll]
    for (int stepY = -1; stepY <= 1; stepY++)
    {
        [unroll]
        for (int stepX = -1; stepX <= 1; stepX++)
        {
            float2 offset = float2(stepX, stepY) * (radius * BULB_SAMPLE_SPREAD);
            float2 probe = lightUv + (offset / WorldScale);
            open += smoothstep(0.0, BULB_SOURCE_SOFTNESS, lightZ - SurfaceZ(DepthAt(probe)));
        }
    }

    return open / BULB_SAMPLES;
}

float4 BulbSurface(float2 uv, float3 tint, float depth, float2 lightUv, float lightZ)
{
    float radius = BulbRadius(lightZ);
    float spread = length((uv - lightUv) * WorldScale) / radius;
    float limb = saturate(spread);
    float dome = sqrt(max(1.0 - limb * limb, 0.0));
    float facing = dome * dome;
    float front = lightZ + BULB_WORLD_RADIUS * dome;
    float solid = smoothstep(0.0, BULB_OCCLUSION_SOFTNESS, front - SurfaceZ(depth));
    // Analytic screen-space derivative of `spread`, so this is safe inside a loop.
    float edge = clamp((PixelWorldSize / radius) * BULB_EDGE, BULB_EDGE_FLOOR, BULB_EDGE_LIMIT);
    float coverage = (1.0 - smoothstep(1.0 - edge, 1.0 + edge, spread)) * solid;
    float3 hue = lerp(tint, float3(1.0, 1.0, 1.0), facing * facing);
    return float4(hue * (BULB_CORE * lerp(BULB_LIMB, 1.0, facing)), coverage);
}

float3 BulbGlow(float2 uv, float3 tint, float2 lightUv, float lightZ)
{
    float radius = BulbRadius(lightZ);
    float radii = length((uv - lightUv) * WorldScale) / radius;
    float halo = exp(-radii / BULB_HALO_SPAN);
    float veil = exp(-radii / BULB_VEIL_SPAN);
    return tint * ((halo * BULB_HALO + veil * BULB_VEIL) * BulbExposure(radius, lightUv, lightZ));
}

float Compress(float value)
{
    return (value * (value / (WHITE_POINT * WHITE_POINT) + 1.0)) / (value + 1.0);
}

float3 Tonemap(float3 color)
{
    float luminance = max(dot(color, LUMINANCE_WEIGHTS), 0.0001);
    float mapped = Compress(luminance);
    float3 shoulder = color / (WHITE_POINT * WHITE_POINT) + 1.0;
    float3 perChannel = (color * shoulder) / (color + 1.0);
    float bleach = pow(saturate(mapped), HIGHLIGHT_BLEACH);
    return saturate(lerp(color * (mapped / luminance), perChannel, bleach));
}

float4 RelightPS(VertexOutput input) : SV_TARGET
{
    float2 uv = input.uv;
    float3 cameraColor = saturate(CameraTexture.Sample(LinearClamp, CameraUvAt(uv)).rgb);
    if (Mode == MODE_CAMERA)
    {
        return float4(cameraColor, 1.0);
    }

    float4 surface = SurfaceTexture.SampleLevel(LinearClamp, uv, 0);
    if (Mode == MODE_DEPTH)
    {
        return float4(DepthRamp(saturate(surface.w)), 1.0);
    }

    float2 slope = surface.xy * (Relief * RELIEF_SCALE);
    float2 tilt = -slope / (1.0 + length(slope) * SLOPE_COMPRESSION);
    float3 normal = normalize(float3(tilt, 1.0));
    if (Mode == MODE_NORMALS)
    {
        return float4(normal * 0.5 + 0.5, 1.0);
    }

    float2 centered = (uv - 0.5) * WorldScale;
    float noise = Dither(uv);
    float3 position = float3(centered, SurfaceZ(surface.w));
    float occlusion = lerp(1.0, surface.z, Occlusion);
    float3 albedo = pow(cameraColor, GAMMA);
    float3 lit = albedo * AMBIENT_FILL * (Exposure * occlusion);

    // SurfaceSlope caps the gradient at GRADIENT_LIMIT, so this reads 1 exactly where the height
    // field breaks over a silhouette.
    float edge = saturate(length(surface.xy) / GRADIENT_LIMIT);
    float rimDamp = lerp(1.0, RIM_EDGE_DAMP, edge);

    for (uint index = 0; index < LightCount; index++)
    {
        float intensity = LightColors[index].a;
        if (intensity <= 0.0)
        {
            continue;
        }

        float3 tint = LightColors[index].rgb;
        float2 lightUv = LightPositions[index].xy;
        float lightZ = LightPositions[index].z;
        bool castsShadow = LightPositions[index].w > 0.5;

        float3 lightPosition = float3((lightUv - 0.5) * WorldScale, lightZ);
        float3 toLight = lightPosition - position;
        float distanceToLight = max(length(toLight), 0.0001);
        float3 lightDirection = toLight / distanceToLight;
        float spread = distanceToLight / LIGHT_RADIUS;
        float falloff = 1.0 / (1.0 + spread * spread);
        float wrapped = saturate((dot(normal, lightDirection) + LIGHT_WRAP) / (1.0 + LIGHT_WRAP));
        float lambert = wrapped * wrapped;

        float shadow = 1.0;
        if (Shadow > 0.0 && castsShadow)
        {
            float3 shadowOrigin = float3(centered, ShadowZ(surface.w));
            float3 shadowToLight = float3((lightUv - 0.5) * WorldScale, lightZ) - shadowOrigin;
            float shadowDistance = max(length(shadowToLight), 0.0001);
            float reach = shadowDistance * (SHADOW_SPAN / max(length(shadowToLight.xy), SHADOW_SPAN));
            float traced = ShadowFactor(shadowOrigin, shadowToLight / shadowDistance, reach, noise, lightZ);
            shadow = lerp(1.0, traced, Shadow);
        }

        float3 halfDirection = normalize(lightDirection + float3(0.0, 0.0, 1.0));
        float lobe = pow(saturate(dot(normal, halfDirection)), SPECULAR_POWER);
        float grazing = pow(1.0 - saturate(normal.z), 5.0) * rimDamp;
        float highlight = lobe * (SPECULAR_F0 + (1.0 - SPECULAR_F0) * grazing);

        lit += albedo * tint * (lambert * falloff * shadow * intensity);
        lit += tint * (highlight * falloff * shadow * occlusion * Specular * intensity);
    }

    // Bulbs and their glow composite over the accumulated lighting.
    if (BulbMode != BULB_HIDDEN)
    {
        for (uint bulbIndex = 0; bulbIndex < LightCount; bulbIndex++)
        {
            float intensity = LightColors[bulbIndex].a;
            if (intensity <= 0.0)
            {
                continue;
            }

            float3 tint = LightColors[bulbIndex].rgb;
            float2 lightUv = LightPositions[bulbIndex].xy;
            float lightZ = LightPositions[bulbIndex].z;
            float presence = saturate(intensity / BULB_ONSET);
            if (BulbMode == BULB_FULL)
            {
                float4 bulb = BulbSurface(uv, tint, surface.w, lightUv, lightZ);
                lit = lerp(lit, bulb.xyz * presence, bulb.w * presence);
            }

            lit += BulbGlow(uv, tint, lightUv, lightZ) * presence;
        }
    }

    float3 display = pow(Tonemap(lit), 1.0 / GAMMA);

    // Dial the whole look back toward the plain camera image.
    display = lerp(cameraColor, display, saturate(Strength));
    return float4(display + (noise - 0.5) * DITHER_STEP, 1.0);
}
