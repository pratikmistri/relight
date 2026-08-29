namespace Relight.Models;

/// <summary>
/// A depth field together with the exact camera frame it was inferred from. Presenting the two
/// together keeps the relighting coherent, at the cost of showing the frame one inference late.
/// </summary>
public sealed class DepthSnapshot
{
    public float[] Disparity { get; set; } = [];

    public byte[] Frame { get; set; } = [];

    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    public float RangeLow { get; set; }

    public float RangeSpan { get; set; } = 1f;

    /// <summary>Publication counter used to detect a newer snapshot.</summary>
    public long Version { get; set; }
}
