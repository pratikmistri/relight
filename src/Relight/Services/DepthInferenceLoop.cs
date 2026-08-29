using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Relight.Models;

namespace Relight.Services;

/// <summary>
/// Runs depth estimation on a background thread, always on the newest camera frame,
/// and publishes the resulting disparity field to the renderer.
/// </summary>
public sealed class DepthInferenceLoop : IDisposable
{
    private readonly LatestFrameSlot _frames;
    private readonly DepthEstimator _estimator;
    private readonly DisparityRange _range = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _publishGate = new();

    private readonly float[] _workDisparity;
    private float[] _publishedDisparity;
    private byte[] _publishedFrame = [];
    private int _publishedFrameWidth;
    private int _publishedFrameHeight;
    private long _publishedVersion;
    private float _publishedLow;
    private float _publishedSpan = 1f;

    private byte[] _frameBuffer = [];
    private long _seenFrameVersion;
    private int _loggedRuns;
    private Task? _worker;
    private bool _disposed;

    public DepthInferenceLoop(LatestFrameSlot frames, DepthEstimator estimator)
    {
        _frames = frames;
        _estimator = estimator;

        int count = estimator.PixelCount;
        _workDisparity = new float[count];
        _publishedDisparity = new float[count];
    }

    public int ModelWidth => _estimator.Width;

    public int ModelHeight => _estimator.Height;

    public bool Mirror { get; set; } = true;

    public double LastInferenceMilliseconds { get; private set; }

    /// <summary>CPU cost of resizing and normalising the frame, in milliseconds.</summary>
    public double LastPreprocessMilliseconds { get; private set; }

    /// <summary>Cost of the model run itself, in milliseconds.</summary>
    public double LastModelMilliseconds { get; private set; }

    public void Start()
    {
        _worker ??= Task.Factory.StartNew(
            Loop,
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Discards the stabilised range so the next frame reseeds it.</summary>
    public void Reset() => _range.Reset();

    /// <summary>
    /// Copies the newest depth field, and the camera frame it was inferred from, into
    /// <paramref name="snapshot"/> when it is newer than the snapshot's version.
    /// </summary>
    public bool TryTakeDepth(DepthSnapshot snapshot)
    {
        lock (_publishGate)
        {
            if (_publishedVersion == snapshot.Version || _publishedVersion == 0)
            {
                return false;
            }

            if (snapshot.Disparity.Length != _publishedDisparity.Length)
            {
                snapshot.Disparity = new float[_publishedDisparity.Length];
            }

            if (snapshot.Frame.Length != _publishedFrame.Length)
            {
                snapshot.Frame = new byte[_publishedFrame.Length];
            }

            _publishedDisparity.CopyTo(snapshot.Disparity, 0);
            _publishedFrame.CopyTo(snapshot.Frame, 0);
            snapshot.FrameWidth = _publishedFrameWidth;
            snapshot.FrameHeight = _publishedFrameHeight;
            snapshot.RangeLow = _publishedLow;
            snapshot.RangeSpan = _publishedSpan;
            snapshot.Version = _publishedVersion;
            return true;
        }
    }

    private void Loop()
    {
        var stopwatch = new Stopwatch();
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            if (!_frames.TryRead(ref _frameBuffer, ref _seenFrameVersion, out int width, out int height))
            {
                Thread.Sleep(4);
                continue;
            }

            try
            {
                stopwatch.Restart();
                FramePreprocessor.Fill(
                    _frameBuffer,
                    width,
                    height,
                    Mirror,
                    _estimator.Width,
                    _estimator.Height,
                    _estimator.InputBuffer);
                double preprocessMs = stopwatch.Elapsed.TotalMilliseconds;

                _estimator.Run(_workDisparity);
                double inferenceMs = stopwatch.Elapsed.TotalMilliseconds - preprocessMs;

                _range.Accumulate(_workDisparity);
                stopwatch.Stop();
                LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                LastPreprocessMilliseconds = preprocessMs;
                LastModelMilliseconds = inferenceMs;

                if (_loggedRuns < 6)
                {
                    _loggedRuns++;
                    LogDisparityStats();
                }

                lock (_publishGate)
                {
                    _workDisparity.CopyTo(_publishedDisparity, 0);

                    // Keep the source frame so the renderer can show it alongside its own depth.
                    if (_publishedFrame.Length != _frameBuffer.Length)
                    {
                        _publishedFrame = new byte[_frameBuffer.Length];
                    }

                    _frameBuffer.CopyTo(_publishedFrame, 0);
                    _publishedFrameWidth = width;
                    _publishedFrameHeight = height;
                    _publishedLow = _range.Low;
                    _publishedSpan = _range.Span;
                    _publishedVersion++;
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // A single failed inference should not tear down the stream.
                Thread.Sleep(50);
            }
        }
    }

    private void LogDisparityStats()
    {
        float minimum = float.MaxValue;
        float maximum = float.MinValue;
        double sum = 0;
        foreach (float value in _workDisparity)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sum += value;
        }

        DiagnosticLog.Write(
            $"depth run {_loggedRuns}: {_estimator.Width}x{_estimator.Height} min={minimum:F3} max={maximum:F3} " +
            $"mean={sum / _workDisparity.Length:F3} range=[{_range.Low:F3},{_range.Low + _range.Span:F3}] " +
            $"total={LastInferenceMilliseconds:F0}ms (preprocess={LastPreprocessMilliseconds:F1}ms " +
            $"model={LastModelMilliseconds:F0}ms) gpu={_estimator.UsesGpu}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation during shutdown is expected.
        }

        _cancellation.Dispose();
        _estimator.Dispose();
    }
}
