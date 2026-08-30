using Relight.Models;

namespace Relight.ViewModels;

/// <summary>
/// Pairs a light with its position in the rig so the custom-mode list can number it without
/// pushing a display concern down into the model.
/// </summary>
public sealed class LightSlot
{
    public LightSlot(int number, LightSource light)
    {
        Number = number;
        Light = light;
    }

    public int Number { get; }

    public LightSource Light { get; }

    public string Label => $"Light {Number}";
}
