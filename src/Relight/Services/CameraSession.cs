using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relight.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace Relight.Services;

/// <summary>Streams BGRA8 camera frames into a <see cref="LatestFrameSlot"/>.</summary>
public sealed class CameraSession : IDisposable
{
    private readonly LatestFrameSlot _slot;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private int _deliveredFrames;
    private int _emptyFrames;
    private int _failedFrames;
    private bool _disposed;

    public CameraSession(LatestFrameSlot slot)
    {
        _slot = slot;
    }

    public bool IsRunning => _reader is not null;

    public async Task StartAsync()
    {
        if (_reader is not null)
        {
            return;
        }

        var groups = await MediaFrameSourceGroup.FindAllAsync();
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("No camera was found on this device.");
        }

        foreach (var group in groups)
        {
            foreach (var candidate in group.SourceInfos)
            {
                DiagnosticLog.Write(
                    $"camera candidate: group='{group.DisplayName}' kind={candidate.SourceKind} " +
                    $"stream={candidate.MediaStreamType} id={candidate.Id}");
            }
        }

        // Only colour preview/record streams are usable; Surface devices also expose IR and depth.
        var selection = groups
            .SelectMany(group => group.SourceInfos, (group, info) => new { group, info })
            .FirstOrDefault(candidate =>
                candidate.info.SourceKind == MediaFrameSourceKind.Color &&
                (candidate.info.MediaStreamType == MediaStreamType.VideoPreview ||
                 candidate.info.MediaStreamType == MediaStreamType.VideoRecord))
            ?? throw new InvalidOperationException("No colour camera stream was found.");

        DiagnosticLog.Write($"camera selected: group='{selection.group.DisplayName}' stream={selection.info.MediaStreamType}");

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = selection.group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        _capture = capture;
        var source = capture.FrameSources[selection.info.Id];
        await SelectPreferredFormatAsync(source);

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        reader.FrameArrived += OnFrameArrived;

        var status = await reader.StartAsync();
        DiagnosticLog.Write($"camera reader start: {status}");
        if (status != MediaFrameReaderStartStatus.Success)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
            throw new InvalidOperationException($"Could not start the camera ({status}).");
        }

        _reader = reader;
    }

    /// <summary>Prefers a modest square-friendly resolution so CPU preprocessing stays cheap.</summary>
    private static async Task SelectPreferredFormatAsync(MediaFrameSource source)
    {
        var format = source.SupportedFormats
            .Where(candidate => candidate.VideoFormat.Width >= 640 && candidate.VideoFormat.Height >= 480)
            .OrderBy(candidate => candidate.VideoFormat.Width * candidate.VideoFormat.Height)
            .ThenByDescending(candidate => candidate.FrameRate.Numerator / Math.Max(candidate.FrameRate.Denominator, 1u))
            .FirstOrDefault();

        if (format is null)
        {
            DiagnosticLog.Write("camera format: keeping the default");
            return;
        }

        DiagnosticLog.Write(
            $"camera format: {format.VideoFormat.Width}x{format.VideoFormat.Height} " +
            $"{format.Subtype} @{format.FrameRate.Numerator}/{format.FrameRate.Denominator}");
        await source.SetFormatAsync(format);
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            CopyLatestFrame(sender);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _failedFrames) <= 3)
            {
                DiagnosticLog.Write($"camera frame failed: {ex}");
            }
        }
    }

    private void CopyLatestFrame(MediaFrameReader sender)
    {
        using var reference = sender.TryAcquireLatestFrame();
        var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            if (Interlocked.Increment(ref _emptyFrames) <= 3)
            {
                DiagnosticLog.Write("camera frame: no software bitmap");
            }

            return;
        }

        using (bitmap)
        {
            if (Interlocked.Increment(ref _deliveredFrames) <= 3)
            {
                DiagnosticLog.Write(
                    $"camera frame #{_deliveredFrames}: {bitmap.PixelWidth}x{bitmap.PixelHeight} {bitmap.BitmapPixelFormat}");
            }

            using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            using var memoryReference = buffer.CreateReference();
            unsafe
            {
                // CsWinRT projections need an explicit QueryInterface for classic COM interfaces.
                var access = memoryReference.As<IMemoryBufferByteAccess>();
                access.GetBuffer(out byte* data, out uint capacity);
                var plane = buffer.GetPlaneDescription(0);
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;

                if (_deliveredFrames <= 1)
                {
                    DiagnosticLog.Write(
                        $"camera buffer: capacity={capacity} start={plane.StartIndex} stride={plane.Stride} " +
                        $"expected={width * 4}");
                }

                if (plane.Stride == width * 4)
                {
                    _slot.Write(
                        new ReadOnlySpan<byte>(data + plane.StartIndex, (int)capacity - plane.StartIndex),
                        width,
                        height);
                    return;
                }

                // Strided source: compact row by row before publishing.
                var packed = new byte[width * height * 4];
                fixed (byte* destination = packed)
                {
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(
                            data + plane.StartIndex + (row * plane.Stride),
                            destination + (row * width * 4),
                            width * 4,
                            width * 4);
                    }
                }

                _slot.Write(packed, width, height);
            }
        }
    }

    public void Stop()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            _ = _reader.StopAsync().AsTask();
            _reader.Dispose();
            _reader = null;
        }

        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
