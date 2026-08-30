using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Relight.Models;
using Relight.Services;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WinUI;

namespace Relight.Graphics;

/// <summary>
/// Direct3D 11 implementation of the depth-driven relighting pipeline: a depth-prepare
/// pass, a surface pass, and a full-screen relight pass presented on a swap chain panel.
/// </summary>
public sealed class RelightRenderer : IDisposable
{
    /// <summary>
    /// Height of the height field in texels. The shader tunables (gradient radius, occlusion
    /// radii) are calibrated against a texel being 1/448 of the frame height, so this stays
    /// fixed while the width follows the camera's aspect ratio.
    /// </summary>
    public const int FieldHeight = 448;

    private const Format BackBufferFormat = Format.B8G8R8A8_UNorm;
    private const int BufferCount = 2;

    /// <summary>
    /// Present every second vblank. The relight pass ray-marches shadows per pixel, so running it
    /// at full refresh starves the DirectML depth inference sharing the same GPU; the camera only
    /// delivers 30 fps anyway.
    /// </summary>
    private const int PresentInterval = 2;

    private readonly SwapChainPanel _panel;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;

    private readonly ID3D11VertexShader _fullScreenVs;
    private readonly ID3D11PixelShader _relightPs;
    private readonly ID3D11ComputeShader _depthPrepareCs;
    private readonly ID3D11ComputeShader _surfaceCs;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Buffer _depthConstants;
    private readonly ID3D11Buffer _relightConstants;

    private ID3D11Texture2D? _historyTexture;
    private ID3D11ShaderResourceView? _historySrv;
    private ID3D11UnorderedAccessView? _historyUav;
    private ID3D11Texture2D? _surfaceTexture;
    private ID3D11ShaderResourceView? _surfaceSrv;
    private ID3D11UnorderedAccessView? _surfaceUav;
    private int _fieldWidth;

    private ID3D11Texture2D? _disparityTexture;
    private ID3D11ShaderResourceView? _disparitySrv;
    private int _disparityWidth;
    private int _disparityHeight;

    private ID3D11Texture2D? _cameraTexture;
    private ID3D11ShaderResourceView? _cameraSrv;
    private int _cameraWidth;
    private int _cameraHeight;

    private ID3D11RenderTargetView? _backBufferView;
    private int _swapChainWidth;
    private int _swapChainHeight;
    private float _compositionScaleX;
    private float _compositionScaleY;

    private bool _resetHistory = true;
    private bool _disposed;

    public RelightRenderer(SwapChainPanel panel)
    {
        _panel = panel;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context).CheckError();

        _device = device!;
        _context = context!;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = 1,
            Height = 1,
            Format = BackBufferFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };

        _swapChain = factory.CreateSwapChainForComposition(_device, description);

        using (var native = (Vortice.WinUI.ISwapChainPanelNative)_panel)
        {
            native.SetSwapChain(_swapChain);
        }

        string source = LoadShaderSource();
        _fullScreenVs = _device.CreateVertexShader(CompileShader(source, "FullScreenVS", "vs_5_0").Span);
        _relightPs = _device.CreatePixelShader(CompileShader(source, "RelightPS", "ps_5_0").Span);
        _depthPrepareCs = _device.CreateComputeShader(CompileShader(source, "DepthPrepareCS", "cs_5_0").Span);
        _surfaceCs = _device.CreateComputeShader(CompileShader(source, "SurfaceCS", "cs_5_0").Span);

        _linearClamp = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _depthConstants = CreateConstantBuffer<DepthConstants>();
        _relightConstants = CreateConstantBuffer<RelightConstants>();
    }

    /// <summary>Aspect ratio of the relit image, matching the camera frame.</summary>
    public double ImageAspect => _fieldWidth > 0 ? (double)_fieldWidth / FieldHeight : 1.0;

    /// <summary>Creates the height field and surface textures for a given frame aspect ratio.</summary>
    private void EnsureField(int width, int height)
    {
        if (_fieldWidth == width && _historyTexture is not null)
        {
            return;
        }

        _surfaceUav?.Dispose();
        _surfaceSrv?.Dispose();
        _surfaceTexture?.Dispose();
        _historyUav?.Dispose();
        _historySrv?.Dispose();
        _historyTexture?.Dispose();

        _historyTexture = CreateComputeTexture(Format.R32_Float, width, height);
        _historySrv = _device.CreateShaderResourceView(_historyTexture);
        _historyUav = _device.CreateUnorderedAccessView(_historyTexture);

        _surfaceTexture = CreateComputeTexture(Format.R16G16B16A16_Float, width, height);
        _surfaceSrv = _device.CreateShaderResourceView(_surfaceTexture);
        _surfaceUav = _device.CreateUnorderedAccessView(_surfaceTexture);

        _fieldWidth = width;
        _resetHistory = true;
        DiagnosticLog.Write($"height field created: {width}x{height}");
    }

    /// <summary>Forces the next depth update to seed rather than blend the temporal history.</summary>
    public void ResetHistory() => _resetHistory = true;

    private static string LoadShaderSource()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Graphics", "Shaders", "Relight.hlsl");
        return File.ReadAllText(path);
    }

    private static ReadOnlyMemory<byte> CompileShader(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(
            source,
            [],
            include: null!,
            entryPoint,
            "Relight.hlsl",
            profile,
            ShaderFlags.OptimizationLevel3,
            out Blob? blob,
            out Blob? errors);

        using (blob)
        using (errors)
        {
            if (result.Failure || blob is null)
            {
                string message = errors?.AsString() ?? result.Description;
                throw new InvalidOperationException($"Failed to compile {entryPoint}: {message}");
            }

            return blob.AsBytes();
        }
    }

    private ID3D11Buffer CreateConstantBuffer<T>()
        where T : unmanaged
    {
        int size = (Unsafe.SizeOf<T>() + 15) / 16 * 16;
        return _device.CreateBuffer(
            (uint)size,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);
    }

    private ID3D11Texture2D CreateComputeTexture(Format format, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        return _device.CreateTexture2D(description);
    }

    private ID3D11Texture2D CreateUploadTexture(Format format, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };

        return _device.CreateTexture2D(description);
    }

    /// <summary>
    /// Resizes the swap chain to match the panel's physical pixel size and maps it back onto the
    /// panel. A composition swap chain is measured in effective pixels, so a buffer sized in
    /// physical pixels renders oversized and clipped until the composition scale is undone.
    /// </summary>
    public void EnsureSize(int width, int height, float scaleX, float scaleY)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        scaleX = scaleX > 0f ? scaleX : 1f;
        scaleY = scaleY > 0f ? scaleY : 1f;

        bool sizeChanged = width != _swapChainWidth || height != _swapChainHeight;
        bool scaleChanged = scaleX != _compositionScaleX || scaleY != _compositionScaleY;
        if (!sizeChanged && !scaleChanged)
        {
            return;
        }

        if (sizeChanged)
        {
            _backBufferView?.Dispose();
            _backBufferView = null;

            _swapChain.ResizeBuffers((uint)BufferCount, (uint)width, (uint)height, BackBufferFormat, SwapChainFlags.None)
                .CheckError();

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _backBufferView = _device.CreateRenderTargetView(backBuffer);
            _swapChainWidth = width;
            _swapChainHeight = height;
        }

        using (var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            swapChain2.MatrixTransform = new Matrix3x2(1f / scaleX, 0f, 0f, 1f / scaleY, 0f, 0f);
        }

        _compositionScaleX = scaleX;
        _compositionScaleY = scaleY;
        DiagnosticLog.Write($"swapchain resized: {width}x{height} scale={scaleX:F2}x{scaleY:F2}");
    }

    /// <summary>Uploads the newest BGRA camera frame.</summary>
    public void UploadCamera(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_cameraTexture is null || _cameraWidth != width || _cameraHeight != height)
        {
            _cameraSrv?.Dispose();
            _cameraTexture?.Dispose();
            _cameraTexture = CreateUploadTexture(Format.B8G8R8A8_UNorm, width, height);
            _cameraSrv = _device.CreateShaderResourceView(_cameraTexture);
            _cameraWidth = width;
            _cameraHeight = height;
            DiagnosticLog.Write($"camera texture created: {width}x{height}");
        }

        WriteRows(_cameraTexture!, bgra, width * 4, height);
    }

    /// <summary>Uploads a freshly inferred disparity map and runs the depth and surface passes.</summary>
    public void UpdateDepth(
        ReadOnlySpan<float> disparity,
        int modelWidth,
        int modelHeight,
        float rangeLow,
        float rangeSpan,
        float deltaSeconds)
    {
        if (modelWidth <= 0 || modelHeight <= 0)
        {
            return;
        }

        // The height field mirrors the model's aspect ratio at a fixed vertical resolution.
        int fieldWidth = Math.Max(8, (int)Math.Round(FieldHeight * (double)modelWidth / modelHeight / 8.0) * 8);
        EnsureField(fieldWidth, FieldHeight);

        if (_disparityTexture is null || _disparityWidth != modelWidth || _disparityHeight != modelHeight)
        {
            _disparitySrv?.Dispose();
            _disparityTexture?.Dispose();
            _disparityTexture = CreateUploadTexture(Format.R32_Float, modelWidth, modelHeight);
            _disparitySrv = _device.CreateShaderResourceView(_disparityTexture);
            _disparityWidth = modelWidth;
            _disparityHeight = modelHeight;
        }

        WriteRows(_disparityTexture!, MemoryMarshal.AsBytes(disparity), modelWidth * sizeof(float), modelHeight);

        WriteConstants(_depthConstants, new DepthConstants
        {
            FieldWidth = (uint)fieldWidth,
            FieldHeight = FieldHeight,
            Reset = _resetHistory ? 1u : 0u,
            RangeLow = rangeLow,
            RangeSpan = rangeSpan,
            DeltaSeconds = deltaSeconds,
        });

        _resetHistory = false;

        uint groupsX = (uint)((fieldWidth + 7) / 8);
        uint groupsY = (FieldHeight + 7) / 8;

        _context.CSSetConstantBuffer(0, _depthConstants);
        _context.CSSetSampler(0, _linearClamp);
        _context.CSSetShader(_depthPrepareCs);
        _context.CSSetShaderResource(0, _disparitySrv);
        _context.CSSetUnorderedAccessView(0, _historyUav);
        _context.Dispatch(groupsX, groupsY, 1);
        _context.CSUnsetUnorderedAccessView(0);
        _context.CSUnsetShaderResource(0);

        _context.CSSetShader(_surfaceCs);
        _context.CSSetShaderResource(1, _historySrv);
        _context.CSSetUnorderedAccessView(1, _surfaceUav);
        _context.Dispatch(groupsX, groupsY, 1);
        _context.CSUnsetUnorderedAccessView(1);
        _context.CSUnsetShaderResource(1);
        _context.CSSetShader(null!);
    }

    private static RelightConstants BuildRelightConstants(RelightSettings settings, FittedRect view)
    {
        // The meter scales the whole relight response, not just the ambient term, so a preset
        // keeps its key-to-fill ratio while adapting to how bright the room already is.
        float gain = settings.ExposureGain > 0f ? settings.ExposureGain : 1f;

        var constants = new RelightConstants
        {
            Exposure = settings.Exposure * gain,
            Relief = settings.Relief,
            Specular = settings.Specular,
            Shadow = settings.Shadow,
            Occlusion = settings.Occlusion,
            PixelWorldSize = view.Height > 0 ? (float)(1.0 / view.Height) : 0.002f,
            Mirror = settings.Mirror ? 1u : 0u,
            Mode = (uint)settings.Mode,
            LightCount = (uint)Math.Clamp(settings.LightCount, 0, RelightSettings.MaxLights),
            WorldScale = new Vector2((float)(view.Height > 0 ? view.Width / view.Height : 1.0), 1f),
            BulbMode = (uint)settings.Bulb,
            Strength = Math.Clamp(settings.Strength, 0f, 1f),
        };

        for (int index = 0; index < RelightSettings.MaxLights; index++)
        {
            var light = settings.Lights[index];

            // Only the key flickers; a wobbling fill would just look like a loose connection.
            float intensity = light.Intensity * gain * (index == 0 ? settings.FlickerGain : 1f);
            constants.LightColors[index] = new Vector4(light.ColorR, light.ColorG, light.ColorB, intensity);
            constants.LightPositions[index] = new Vector4(light.X, light.Y, light.Z, light.CastsShadow ? 1f : 0f);
        }

        return constants;
    }

    /// <summary>Draws the relit frame and presents it.</summary>
    public void Render(RelightSettings settings)
    {
        if (_backBufferView is null)
        {
            return;
        }

        _context.OMSetRenderTargets(_backBufferView);
        _context.ClearRenderTargetView(_backBufferView, Colors.Black);

        // Letterbox the frame inside the panel, using the same fit the pointer mapping uses.
        var view = FittedRect.Fit(_swapChainWidth, _swapChainHeight, ImageAspect);

        if (_cameraSrv is null || _surfaceSrv is null)
        {
            // Nothing to draw yet: present the cleared target so the panel is not transparent.
            _swapChain.Present(PresentInterval, PresentFlags.None);
            return;
        }

        WriteConstants(_relightConstants, BuildRelightConstants(settings, view));

        _context.RSSetViewport((float)view.X, (float)view.Y, (float)view.Width, (float)view.Height);

        _context.IASetInputLayout(null!);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullScreenVs);
        _context.PSSetShader(_relightPs);
        _context.PSSetConstantBuffer(1, _relightConstants);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(2, _surfaceSrv);
        _context.PSSetShaderResource(3, _cameraSrv);
        _context.Draw(3, 0);

        _context.PSUnsetShaderResource(2);
        _context.PSUnsetShaderResource(3);
        _swapChain.Present(PresentInterval, PresentFlags.None);
    }

    private void WriteRows(ID3D11Texture2D texture, ReadOnlySpan<byte> source, int rowBytes, int rows)
    {
        var mapped = _context.Map(texture, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                byte* destination = (byte*)mapped.DataPointer;
                fixed (byte* origin = source)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        Buffer.MemoryCopy(
                            origin + (row * rowBytes),
                            destination + (row * (int)mapped.RowPitch),
                            rowBytes,
                            rowBytes);
                    }
                }
            }
        }
        finally
        {
            _context.Unmap(texture, 0);
        }
    }

    private void WriteConstants<T>(ID3D11Buffer buffer, T value)
        where T : unmanaged
    {
        var mapped = _context.Map(buffer, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                *(T*)mapped.DataPointer = value;
            }
        }
        finally
        {
            _context.Unmap(buffer, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _backBufferView?.Dispose();
        _cameraSrv?.Dispose();
        _cameraTexture?.Dispose();
        _disparitySrv?.Dispose();
        _disparityTexture?.Dispose();
        _surfaceUav?.Dispose();
        _surfaceSrv?.Dispose();
        _surfaceTexture?.Dispose();
        _historyUav?.Dispose();
        _historySrv?.Dispose();
        _historyTexture?.Dispose();
        _relightConstants.Dispose();
        _depthConstants.Dispose();
        _linearClamp.Dispose();
        _surfaceCs.Dispose();
        _depthPrepareCs.Dispose();
        _relightPs.Dispose();
        _fullScreenVs.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DepthConstants
    {
        public uint FieldWidth;
        public uint FieldHeight;
        public uint Reset;
        public float RangeLow;
        public float RangeSpan;
        public float DeltaSeconds;
        private readonly float _padding0;
        private readonly float _padding1;
    }

    [InlineArray(RelightSettings.MaxLights)]
    private struct LightVectorArray
    {
        private Vector4 _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RelightConstants
    {
        public LightVectorArray LightColors;
        public LightVectorArray LightPositions;
        public float Exposure;
        public float Relief;
        public float Specular;
        public float Shadow;
        public float Occlusion;
        public float PixelWorldSize;
        public uint Mirror;
        public uint Mode;
        public uint LightCount;
        public Vector2 WorldScale;
        public uint BulbMode;
        public float Strength;
        private readonly float _padding0;
        private readonly float _padding1;
        private readonly float _padding2;
    }
}
