using System;
using System.Threading;

namespace Relight.Graphics;

/// <summary>
/// Drives rendering from a dedicated thread. WinUI's <c>CompositionTarget.Rendering</c> only
/// ticks while the XAML tree is animating, so a swap chain panel needs its own loop, and the
/// Direct3D immediate context must be touched by this thread alone.
/// </summary>
public sealed class RenderLoop : IDisposable
{
    private readonly Action _onFrame;
    private readonly Action<Exception> _onError;
    private readonly Thread _thread;
    private volatile bool _running;
    private bool _disposed;

    public RenderLoop(Action onFrame, Action<Exception> onError)
    {
        _onFrame = onFrame;
        _onError = onError;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Relight render",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread.Start();
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                _onFrame();
            }
            catch (Exception ex)
            {
                _running = false;
                _onError(ex);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
