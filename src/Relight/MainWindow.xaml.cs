using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Relight.Graphics;
using Relight.Models;
using Relight.Services;
using Relight.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace Relight;

/// <summary>
/// Hosts the relighting pipeline: camera capture feeds both the Direct3D renderer and a
/// background depth-inference loop, and the renderer presents every composition frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string ModelFileName = "depth-anything-v2-small-fp16.onnx";

    private readonly LatestFrameSlot _frames = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LightController _light;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _overlayTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _toastTimer;

    private RelightRenderer? _renderer;
    private RenderLoop? _loop;
    private CameraSession? _camera;
    private DepthInferenceLoop? _depth;

    private byte[] _cameraBuffer = [];
    private long _seenCameraVersion;
    private readonly DepthSnapshot _snapshot = new();

    private int _panelWidth;
    private int _panelHeight;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _framesRendered;
    private int _traceFrames;
    private double _frameAspect = 4.0 / 3.0;
    private double _lastDiagnosticsUpdate;
    private double _lastDepthUpload;
    private bool _loggedFirstFrames;
    private bool _closing;

    public MainWindow()
    {
        // The view model must exist before InitializeComponent, because applying the
        // ComboBox SelectedIndex values raises SelectionChanged during XAML load.
        ViewModel = new MainViewModel();
        _light = new LightController(ViewModel.Settings);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);

        ViewModel.DepthResolutionChanged += OnDepthResolutionChanged;

        RenderPanel.SizeChanged += OnPanelSizeChanged;
        RenderPanel.PointerMoved += OnPointerMoved;
        RenderPanel.PointerPressed += OnPointerPressed;
        RenderPanel.PointerEntered += OnPointerEntered;
        RenderPanel.PointerExited += OnPointerExited;
        RenderPanel.PointerWheelChanged += OnPointerWheelChanged;
        RenderPanel.Loaded += OnPanelLoaded;

        if (Content is FrameworkElement rootElement)
        {
            rootElement.KeyDown += OnKeyDown;
        }

        _overlayTimer = DispatcherQueue.CreateTimer();
        _overlayTimer.Interval = TimeSpan.FromSeconds(2.5);
        _overlayTimer.IsRepeating = false;
        _overlayTimer.Tick += (_, _) => SetOverlayVisible(false);

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(1.8);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => FadePresetToast(0);

        Closed += OnClosed;
    }

    public MainViewModel ViewModel { get; }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        RenderPanel.Loaded -= OnPanelLoaded;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            ViewModel.Status = "Starting Direct3D…";
            _renderer = new RelightRenderer(RenderPanel);
            UpdatePanelSize();

            ViewModel.Status = "Waiting for the camera…";
            _camera = new CameraSession(_frames);
            await _camera.StartAsync();

            // The model and height field are sized from the real frame, so wait for one.
            var (frameWidth, frameHeight) = await WaitForFrameSizeAsync();
            _frameAspect = (double)frameWidth / frameHeight;
            DiagnosticLog.Write($"camera frame size {frameWidth}x{frameHeight}, aspect {_frameAspect:F3}");

            ViewModel.Status = "Loading the depth model…";
            string modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", ModelFileName);
            if (!File.Exists(modelPath))
            {
                ViewModel.Status = $"The depth model is missing:\n{modelPath}";
                return;
            }

            await StartDepthAsync(modelPath, ViewModel.DepthResolutions[ViewModel.DepthResolutionIndex]);

            ViewModel.Status = string.Empty;
            ViewModel.IsBusy = false;
            StatusBanner.Visibility = Visibility.Collapsed;

            // Show the mood briefly, then let the overlay fade out of the way.
            ShowPresetToast();
            SetOverlayVisible(true);

            DiagnosticLog.Write($"startup complete; panel {_panelWidth}x{_panelHeight}");

            _loop = new RenderLoop(RenderFrame, OnRenderFailed);
            _loop.Start();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"startup failed: {ex}");
            ViewModel.Status = $"Could not start: {ex.Message}";
            StatusBanner.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Waits until the camera has delivered a frame so its dimensions are known.</summary>
    private async Task<(int Width, int Height)> WaitForFrameSizeAsync()
    {
        byte[] probe = [];
        long version = 0;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (_frames.TryRead(ref probe, ref version, out int width, out int height) && width > 0)
            {
                return (width, height);
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("The camera did not deliver a frame.");
    }

    private async Task StartDepthAsync(string modelPath, int quality)
    {
        var (modelWidth, modelHeight) = DepthEstimator.ChooseSize(quality, _frameAspect);
        DiagnosticLog.Write($"depth model input {modelWidth}x{modelHeight} for quality {quality}");

        var estimator = await Task.Run(() => new DepthEstimator(modelPath, modelWidth, modelHeight));
        _snapshot.Version = 0;
        _depth = new DepthInferenceLoop(_frames, estimator) { Mirror = ViewModel.Settings.Mirror };
        _depth.Start();
        _renderer?.ResetHistory();
    }

    /// <summary>Runs on the render thread; owns every Direct3D call after construction.</summary>
    private void RenderFrame()
    {
        var renderer = _renderer;
        if (renderer is null || _closing)
        {
            Thread.Sleep(16);
            return;
        }

        bool trace = _traceFrames < 3;

        int width = Volatile.Read(ref _pendingWidth);        int height = Volatile.Read(ref _pendingHeight);
        if (width > 0 && height > 0)
        {
            renderer.EnsureSize(width, height);
        }

        _light.Tick(_clock.Elapsed.TotalMilliseconds);

        bool synced = ViewModel.SyncFrames;
        var depth = _depth;

        if (depth is not null && depth.TryTakeDepth(_snapshot))
        {
            // Elapsed time between depth updates drives the temporal filter's blend weight.
            double now = _clock.Elapsed.TotalSeconds;
            float delta = _lastDepthUpload > 0 ? (float)(now - _lastDepthUpload) : 0.016f;
            _lastDepthUpload = now;

            if (synced)
            {
                // Show the frame this depth was inferred from, so shading and image agree.
                renderer.UploadCamera(_snapshot.Frame, _snapshot.FrameWidth, _snapshot.FrameHeight);
                _seenCameraVersion = 0;
            }

            renderer.UpdateDepth(
                _snapshot.Disparity,
                depth.ModelWidth,
                depth.ModelHeight,
                _snapshot.RangeLow,
                _snapshot.RangeSpan,
                delta);

            if (trace)
            {
                _traceFrames++;
                DiagnosticLog.Write(
                    $"depth uploaded to GPU: {depth.ModelWidth}x{depth.ModelHeight} range=" +
                    $"[{_snapshot.RangeLow:F3},{_snapshot.RangeLow + _snapshot.RangeSpan:F3}] dt={delta * 1000:F0}ms synced={synced}");
            }
        }

        if (!synced && _frames.TryRead(ref _cameraBuffer, ref _seenCameraVersion, out int frameWidth, out int frameHeight))
        {
            renderer.UploadCamera(_cameraBuffer, frameWidth, frameHeight);
        }

        renderer.Render(ViewModel.Settings);
        UpdateDiagnostics();
    }

    private void OnRenderFailed(Exception error)
    {
        DiagnosticLog.Write($"render loop stopped: {error}");
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.Status = $"Rendering stopped: {error.Message}";
            StatusBanner.Visibility = Visibility.Visible;
        });
    }

    private void UpdateDiagnostics()
    {
        _framesRendered++;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastDiagnosticsUpdate < 500)
        {
            return;
        }

        double fps = _framesRendered * 1000.0 / (now - _lastDiagnosticsUpdate);
        double inference = _depth?.LastInferenceMilliseconds ?? 0;
        double depthRate = inference > 0 ? 1000.0 / inference : 0;
        _framesRendered = 0;
        _lastDiagnosticsUpdate = now;

        if (!_loggedFirstFrames)
        {
            _loggedFirstFrames = true;
            DiagnosticLog.Write($"render loop alive: {fps:F0} fps, depth {inference:F0} ms");
        }

        DispatcherQueue.TryEnqueue(() =>
            DiagnosticsText.Text = $"{fps:F0} fps · depth {inference:F0} ms ({depthRate:F0}/s)");
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanelSize();

    private void OnViewModeSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.ViewModeIndex = ViewModeBox.SelectedIndex;

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedIndex == ViewModel.PresetIndex)
        {
            return;
        }

        ViewModel.PresetIndex = PresetBox.SelectedIndex;
        ShowPresetToast();
    }

    private void OnDepthResolutionSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.DepthResolutionIndex = DepthResolutionBox.SelectedIndex;

    private void UpdatePanelSize()
    {
        double scale = RenderPanel.CompositionScaleX <= 0 ? 1.0 : RenderPanel.CompositionScaleX;
        _panelWidth = Math.Max(1, (int)Math.Round(RenderPanel.ActualWidth * scale));
        _panelHeight = Math.Max(1, (int)Math.Round(RenderPanel.ActualHeight * scale));

        // The render thread owns Direct3D, so hand the size over instead of resizing here.
        Volatile.Write(ref _pendingWidth, _panelWidth);
        Volatile.Write(ref _pendingHeight, _panelHeight);
    }

    /// <summary>
    /// Converts a pointer position into image coordinates using the same letterbox fit the
    /// renderer uses for its viewport, so the light lands exactly under the cursor.
    /// Pointer positions arrive in physical pixels, so the panel size is scaled to match.
    /// </summary>
    private bool TryGetImagePoint(PointerRoutedEventArgs e, out float x, out float y)
    {
        Point position = e.GetCurrentPoint(RenderPanel).Position;
        double scaleX = RenderPanel.CompositionScaleX <= 0 ? 1.0 : RenderPanel.CompositionScaleX;
        double scaleY = RenderPanel.CompositionScaleY <= 0 ? 1.0 : RenderPanel.CompositionScaleY;
        var view = FittedRect.Fit(
            RenderPanel.ActualWidth * scaleX,
            RenderPanel.ActualHeight * scaleY,
            _renderer?.ImageAspect ?? _frameAspect);
        return view.TryToImage(position.X, position.Y, out x, out y);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TryGetImagePoint(e, out float x, out float y))
        {
            _light.PointerMoved(x, y);
        }

        // Reveal the overlay only when the pointer approaches the bottom edge.
        double y2 = e.GetCurrentPoint(RenderPanel).Position.Y;
        double scaleY = RenderPanel.CompositionScaleY <= 0 ? 1.0 : RenderPanel.CompositionScaleY;
        double height = RenderPanel.ActualHeight * scaleY;
        if (height > 0 && y2 > height * 0.78)
        {
            SetOverlayVisible(true);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                ViewModel.PreviousPreset();
                ShowPresetToast();
                break;
            case VirtualKey.Right:
            case VirtualKey.Space:
                ViewModel.NextPreset();
                ShowPresetToast();
                break;
            case VirtualKey.V:
                ViewModel.NextViewMode();
                ViewModeBox.SelectedIndex = ViewModel.ViewModeIndex;
                break;
            case VirtualKey.H:
                SetOverlayVisible(!ViewModel.ShowOverlay);
                return;
            default:
                if (e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
                {
                    int index = e.Key - VirtualKey.Number1;
                    if (index < ViewModel.Presets.Count)
                    {
                        ViewModel.PresetIndex = index;
                        PresetBox.SelectedIndex = index;
                        ShowPresetToast();
                    }
                }

                return;
        }

        PresetBox.SelectedIndex = ViewModel.PresetIndex;
        e.Handled = true;
    }

    private void OnPreviousPreset(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviousPreset();
        PresetBox.SelectedIndex = ViewModel.PresetIndex;
        ShowPresetToast();
    }

    private void OnNextPreset(object sender, RoutedEventArgs e)
    {
        ViewModel.NextPreset();
        PresetBox.SelectedIndex = ViewModel.PresetIndex;
        ShowPresetToast();
    }

    private void SetOverlayVisible(bool visible)
    {
        ViewModel.ShowOverlay = visible;
        Overlay.Opacity = visible ? 1 : 0;
        Overlay.IsHitTestVisible = visible;
        _overlayTimer.Stop();
        if (visible)
        {
            _overlayTimer.Start();
        }
    }

    private void ShowPresetToast()
    {
        FadePresetToast(1);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void FadePresetToast(double opacity) => PresetToast.Opacity = opacity;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryGetImagePoint(e, out float x, out float y))
        {
            _light.PointerPressed(x, y);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _light.PointerEntered();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => _light.PointerExited();

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        _light.WheelChanged(e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta);

    private async void OnDepthResolutionChanged(int side)
    {
        if (_closing)
        {
            return;
        }

        var previous = _depth;
        _depth = null;
        previous?.Dispose();

        try
        {
            ViewModel.Status = "Reloading the depth model…";
            StatusBanner.Visibility = Visibility.Visible;

            string modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", ModelFileName);
            await StartDepthAsync(modelPath, side);

            ViewModel.Status = string.Empty;
            StatusBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Could not switch resolution: {ex.Message}";
            StatusBanner.Visibility = Visibility.Visible;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _loop?.Dispose();
        _camera?.Dispose();
        _depth?.Dispose();
        _renderer?.Dispose();
    }
}
