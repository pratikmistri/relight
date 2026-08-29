using System;
using System.Threading;

namespace Relight.Models;

/// <summary>
/// A single-slot, lock-guarded handoff of the most recent camera frame from the
/// capture thread to the render and inference consumers. Newest frame always wins.
/// </summary>
public sealed class LatestFrameSlot
{
    private readonly Lock _gate = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _version;

    public void Write(ReadOnlySpan<byte> source, int width, int height)
    {
        lock (_gate)
        {
            int required = width * height * 4;
            if (_pixels.Length != required)
            {
                _pixels = new byte[required];
            }

            source[..required].CopyTo(_pixels);
            _width = width;
            _height = height;
            _version++;
        }
    }

    /// <summary>
    /// Copies the newest frame into <paramref name="destination"/> when it is newer than
    /// <paramref name="seenVersion"/>, growing the buffer as needed.
    /// </summary>
    public bool TryRead(ref byte[] destination, ref long seenVersion, out int width, out int height)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            if (_version == seenVersion || _version == 0)
            {
                return false;
            }

            if (destination.Length != _pixels.Length)
            {
                destination = new byte[_pixels.Length];
            }

            _pixels.CopyTo(destination, 0);
            seenVersion = _version;
            return true;
        }
    }
}
