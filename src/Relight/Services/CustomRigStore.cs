using System;
using System.IO;
using System.Text.Json;
using Relight.Models;

namespace Relight.Services;

/// <summary>
/// Saves and restores the hand-built light rig, so custom mode survives a restart. Failures are
/// never fatal: a missing or corrupt file simply means the rig starts from the current preset.
/// </summary>
public sealed class CustomRigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    public CustomRigStore()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Relight");
        _path = Path.Combine(folder, "custom-rig.json");
    }

    public bool Exists => File.Exists(_path);

    public void Save(RelightSettings settings)
    {
        try
        {
            int count = Math.Clamp(settings.LightCount, 1, RelightSettings.MaxLights);
            var lights = new LightState[count];
            for (int index = 0; index < count; index++)
            {
                var light = settings.Lights[index];
                lights[index] = new LightState(
                    light.X,
                    light.Y,
                    light.Z,
                    light.ColorR,
                    light.ColorG,
                    light.ColorB,
                    light.Intensity,
                    light.CastsShadow);
            }

            var rig = new CustomRig(
                count,
                lights,
                settings.Exposure,
                settings.Relief,
                settings.Specular,
                settings.Shadow,
                settings.Occlusion,
                settings.Strength);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(rig, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Write($"custom rig save failed: {ex.Message}");
        }
    }

    /// <summary>Applies the stored rig onto <paramref name="settings"/>, returning false if there is none.</summary>
    public bool TryLoad(RelightSettings settings)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            var rig = JsonSerializer.Deserialize<CustomRig>(File.ReadAllText(_path));
            if (rig?.Lights is null || rig.Lights.Length == 0)
            {
                return false;
            }

            int count = Math.Clamp(Math.Min(rig.LightCount, rig.Lights.Length), 1, RelightSettings.MaxLights);
            for (int index = 0; index < count; index++)
            {
                var state = rig.Lights[index];
                settings.Lights[index].Set(
                    state.X,
                    state.Y,
                    state.Z,
                    state.ColorR,
                    state.ColorG,
                    state.ColorB,
                    state.Intensity,
                    state.CastsShadow);
            }

            settings.LightCount = count;
            settings.Exposure = rig.Exposure;
            settings.Relief = rig.Relief;
            settings.Specular = rig.Specular;
            settings.Shadow = rig.Shadow;
            settings.Occlusion = rig.Occlusion;
            settings.Strength = Math.Clamp(rig.Strength, 0f, 1f);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            DiagnosticLog.Write($"custom rig load failed: {ex.Message}");
            return false;
        }
    }

    private sealed record LightState(
        float X,
        float Y,
        float Z,
        float ColorR,
        float ColorG,
        float ColorB,
        float Intensity,
        bool CastsShadow);

    private sealed record CustomRig(
        int LightCount,
        LightState[] Lights,
        float Exposure,
        float Relief,
        float Specular,
        float Shadow,
        float Occlusion,
        float Strength);
}
