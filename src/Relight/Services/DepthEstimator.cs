using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Relight.Services;

/// <summary>Runs monocular depth estimation (Depth Anything V2) on the GPU via DirectML.</summary>
public sealed class DepthEstimator : IDisposable
{
    /// <summary>ViT patch size; both input dimensions must be a multiple of this.</summary>
    public const int PatchSize = 14;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly float[] _inputData;
    private readonly DenseTensor<float> _input;
    private readonly List<NamedOnnxValue> _feed;
    private bool _disposed;

    public DepthEstimator(string modelPath, int width, int height)
    {
        Width = width;
        Height = height;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
        };

        try
        {
            options.AppendExecutionProvider_DML(0);
            UsesGpu = true;
        }
        catch (Exception)
        {
            // DirectML is unavailable; the CPU provider still produces correct output.
            UsesGpu = false;
        }

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        _inputData = new float[3 * width * height];
        _input = new DenseTensor<float>(_inputData, [1, 3, height, width]);
        _feed = [NamedOnnxValue.CreateFromTensor(_inputName, _input)];
    }

    public int Width { get; }

    public int Height { get; }

    public int PixelCount => Width * Height;

    public bool UsesGpu { get; }

    /// <summary>The writable NCHW input buffer; fill it before calling <see cref="Run"/>.</summary>
    public float[] InputBuffer => _inputData;

    /// <summary>
    /// Chooses model dimensions for a frame aspect ratio, keeping the pixel budget close to
    /// <paramref name="quality"/> squared so latency stays comparable across aspect ratios.
    /// Both dimensions are rounded to a multiple of the ViT patch size.
    /// </summary>
    public static (int Width, int Height) ChooseSize(int quality, double aspect)
    {
        if (aspect <= 0)
        {
            aspect = 1;
        }

        // Derive the width from the already-snapped height so the ratio stays close to the frame.
        int height = Snap(quality / Math.Sqrt(aspect));
        int width = Snap(height * aspect);
        return (width, height);
    }

    private static int Snap(double value) =>
        Math.Max(PatchSize * 4, (int)Math.Round(value / PatchSize) * PatchSize);

    /// <summary>Runs the model and copies the disparity map into <paramref name="destination"/>.</summary>
    public void Run(Span<float> destination)
    {
        using var results = _session.Run(_feed);
        var output = results.First(result => result.Name == _outputName).AsTensor<float>();
        if (output is DenseTensor<float> dense)
        {
            dense.Buffer.Span[..destination.Length].CopyTo(destination);
            return;
        }

        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = output.GetValue(index);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
