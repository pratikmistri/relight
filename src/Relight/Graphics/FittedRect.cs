namespace Relight.Graphics;

/// <summary>
/// Fits an image of a given aspect ratio inside a container, centred, without cropping.
/// Both the Direct3D viewport and pointer hit-testing derive from this so they cannot disagree.
/// </summary>
public readonly record struct FittedRect(double X, double Y, double Width, double Height)
{
    public static FittedRect Fit(double containerWidth, double containerHeight, double imageAspect)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || imageAspect <= 0)
        {
            return new FittedRect(0, 0, 0, 0);
        }

        double width = containerWidth;
        double height = containerWidth / imageAspect;
        if (height > containerHeight)
        {
            height = containerHeight;
            width = containerHeight * imageAspect;
        }

        return new FittedRect((containerWidth - width) * 0.5, (containerHeight - height) * 0.5, width, height);
    }

    /// <summary>Converts a container-space point into [0,1] image coordinates.</summary>
    public bool TryToImage(double pointX, double pointY, out float u, out float v)
    {
        u = 0;
        v = 0;
        if (Width <= 0 || Height <= 0)
        {
            return false;
        }

        u = (float)((pointX - X) / Width);
        v = (float)((pointY - Y) / Height);
        return true;
    }
}
