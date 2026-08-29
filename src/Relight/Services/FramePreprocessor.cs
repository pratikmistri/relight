using System;
using System.Threading.Tasks;

namespace Relight.Services;

/// <summary>
/// Turns a BGRA camera frame into the normalised NCHW tensor the depth model expects:
/// the full frame (no crop), optionally mirrored, bilinearly resized, then ImageNet normalised.
/// </summary>
public static class FramePreprocessor
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];

    public static void Fill(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        bool mirror,
        int targetWidth,
        int targetHeight,
        float[] destination)
    {
        int plane = targetWidth * targetHeight;
        float scaleX = (float)sourceWidth / targetWidth;
        float scaleY = (float)sourceHeight / targetHeight;

        // Rows are independent, and this sits directly in the depth loop's latency budget.
        Parallel.For(0, targetHeight, y => FillRow(
            bgra, sourceWidth, sourceHeight, mirror, targetWidth, destination, plane, scaleX, scaleY, y));
    }

    private static void FillRow(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        bool mirror,
        int targetWidth,
        float[] destination,
        int plane,
        float scaleX,
        float scaleY,
        int y)
    {
        float sourceY = ((y + 0.5f) * scaleY) - 0.5f;
        int y0 = (int)MathF.Floor(sourceY);
        float fractionY = sourceY - y0;
        int row0 = Math.Clamp(y0, 0, sourceHeight - 1) * sourceWidth;
        int row1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1) * sourceWidth;

        for (int x = 0; x < targetWidth; x++)
        {
            int outputX = mirror ? targetWidth - 1 - x : x;
            float sourceX = ((outputX + 0.5f) * scaleX) - 0.5f;
            int x0 = (int)MathF.Floor(sourceX);
            float fractionX = sourceX - x0;
            int column0 = Math.Clamp(x0, 0, sourceWidth - 1);
            int column1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);

            int index00 = (row0 + column0) * 4;
            int index01 = (row0 + column1) * 4;
            int index10 = (row1 + column0) * 4;
            int index11 = (row1 + column1) * 4;

            int target = (y * targetWidth) + x;
            for (int channel = 0; channel < 3; channel++)
            {
                // BGRA in memory, but the model wants RGB.
                int offset = 2 - channel;
                float top = Lerp(bgra[index00 + offset], bgra[index01 + offset], fractionX);
                float bottom = Lerp(bgra[index10 + offset], bgra[index11 + offset], fractionX);
                float value = Lerp(top, bottom, fractionY) / 255f;
                destination[(channel * plane) + target] = (value - Mean[channel]) / Deviation[channel];
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
