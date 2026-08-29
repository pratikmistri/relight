# Relight

A native **WinUI 3** recreation of the TypeGPU
[*Monocular Light Injection*](https://docs.swmansion.com/TypeGPU/examples/#example=image-processing--monocular-light-injection)
example, driven by a live camera feed.

A monocular depth network estimates a depth map from each camera frame; that depth field is
turned into a surface (slope + height-field occlusion) and then relit on the GPU with a movable
virtual light — complete with ray-marched cast shadows, a visible bulb, glow, and tone mapping.

## Pipeline

| Stage | Where | Notes |
|---|---|---|
| Camera capture | `Services/CameraSession.cs` | `MediaFrameReader`, BGRA8, colour source only |
| Preprocess | `Services/FramePreprocessor.cs` | Full frame (no crop) → mirror → bilinear resize → ImageNet normalise |
| Depth inference | `Services/DepthEstimator.cs` | Depth Anything V2 Small (ONNX) on ONNX Runtime + **DirectML** |
| Range stabilisation | `Services/DisparityRange.cs` | Robust 2/98 percentiles, exponentially blended between frames |
| Depth prepare | `DepthPrepareCS` (HLSL) | Normalises disparity into the height field, motion-adaptive temporal filter |
| Surface | `SurfaceCS` (HLSL) | Gradient/slope + two-radius height-field occlusion → RGBA16F |
| Relight | `RelightPS` (HLSL) | Per-light Lambert + specular, 32-step ray-marched shadows, bulb, glow, tone map |

Inference is **decoupled from rendering**: the network runs continuously on a background thread
while the relight pass presents independently.

### Aspect ratio

The whole camera frame is processed — there is no square crop (the original example cropped only
because its canvas was square). Nothing in the pipeline requires 1:1:

- **Model input** is sized from the frame's aspect ratio, with both dimensions snapped to a
  multiple of 14 (the ViT patch size). The pixel budget is held near the selected quality
  squared, so latency stays comparable across aspect ratios.
- **The height field** keeps a fixed 448-texel height and takes its width from the aspect, which
  keeps a texel at 1/448 of the frame height so the shader tunables stay calibrated.
- **The relight shader** multiplies UV space by `WorldScale = (width / height, 1)` before doing
  any distance maths. Without this, light falloff, shadow rays and the bulb would stretch
  horizontally on a non-square frame.
- **Letterboxing** is computed once in `FittedRect` and shared by the Direct3D viewport and
  pointer hit-testing, so the light always lands exactly under the cursor.

## Performance and latency

Measured on a Snapdragon X Plus (Adreno X1-85) with a 4:3 (640×480) camera. "Coherent rate" is
the video frame rate when **Sync image to depth** is on, since the image then advances once per
inference:

| Quality | Model input | Latency | Coherent rate |
|---|---|---|---|
| Fastest | 182×140 | ~55 ms | ~18 fps |
| **Fast (default)** | **266×196** | **~86 ms** | **~11 fps** |
| Balanced | 364×266 | ~150 ms | ~6.6 fps |
| Detailed | 532×392 | ~300 ms | ~3 fps |

Preprocessing is parallel across rows and costs ~1-3 ms; the rest is the model. Depth latency also
depends on how many lights cast shadows, since the relight pass shares the GPU with inference —
a two-caster preset such as Neon Nights costs roughly 15% more than a single-key preset.

### Keeping shading and image in sync

Depth for a frame is only ready ~80 ms after that frame was captured, and it is then reused until
the next inference. If the live camera image keeps advancing during that window, the shadow
silhouette necessarily trails the subject — which reads as a bug rather than as latency.

**Sync image to depth** (on by default) holds each camera frame and presents it together with the
depth field inferred from *that* frame. Shading then always matches the image; the whole feed is
uniformly late by one inference instead of being internally inconsistent. Turn it off to get the
camera's full frame rate and accept trailing shadows.

Two supporting details:

- **The temporal filter is time-based, not per-frame.** The original blended with fixed alphas
  (0.32 steady, 0.8 in motion) assuming depth updated every 60 fps frame. Those alphas are stored
  as time constants (`TEMPORAL_TAU`, `MOTION_TAU`) and resolved against the measured interval
  between depth updates. Applying the raw alphas at a slower rate smears old frames into a
  visible ghost trail.
- **The relight pass presents every second vblank** (`PresentInterval = 2`). It ray-marches
  shadows per pixel, so running it at full refresh competes with DirectML for the same GPU and
  slows inference.

## Requirements

- Windows 11 with a DirectX 12 / DirectML-capable GPU
- .NET 10 SDK
- A camera, and **Settings → Privacy & security → Camera → Let desktop apps access your camera** enabled
  (unpackaged desktop apps do not show a consent dialog)

## Build and run

The depth model (~47 MB) is not committed. Fetch it once, then build:

```powershell
.\scripts\fetch-model.ps1

cd src\Relight
$Platform = $env:PROCESSOR_ARCHITECTURE
dotnet build -c Debug -p:Platform=$Platform
dotnet run -c Debug -p:Platform=$Platform
```

The app writes startup diagnostics to `relight.log` next to the executable.

## Controls

The UI is a full-bleed viewport with an overlay that appears when the pointer nears the bottom
edge (or on <kbd>H</kbd>) and fades after a couple of seconds.

| Input | Action |
|---|---|
| Move the pointer | Steers the key light; idles into a slow orbit when the pointer leaves |
| Click | Pins the light; click the light again to release it |
| Scroll | Pushes the key light toward or away from the camera |
| <kbd>←</kbd> / <kbd>→</kbd> or <kbd>Space</kbd> | Previous / next lighting preset |
| <kbd>1</kbd>–<kbd>9</kbd> | Jump straight to a preset |
| <kbd>V</kbd> | Cycle view: relit, camera, depth, normals |
| <kbd>H</kbd> | Show or hide the overlay |

## Lighting presets

Each preset sets the global response (ambient, relief, specular, shadow, occlusion) **and** a
complete light rig. The first light in a rig is the key, and it is the one the pointer steers.

| Preset | Rig |
|---|---|
| Rembrandt | Single warm key high on one side, deep falloff |
| Butterfly | Key above centre with a soft cool under-fill |
| Split | Hard side light, half the subject in shadow |
| Three-Point | Key, cool fill and a bright rim |
| Neon Nights | Opposing magenta and cyan sources, both casting |
| Golden Hour | Low warm sun with a soft bounce |
| Candlelit | Close, warm and dim with strong relief |
| Clamshell | Even beauty lighting from above and below |
| Moonlight | Cool single source, high and distant |

## Multiple lights

The shader supports up to **four simultaneous lights** (`MAX_LIGHTS`), each with its own
position, depth, colour, intensity and shadow flag. Every light contributes diffuse, specular,
a visible bulb and glow.

Ray-marched shadows are by far the most expensive term — 32 texture-sampling steps per light per
pixel — so each light carries a `CastsShadow` flag and the presets reserve it for lights that
genuinely shape the scene. Fills and rims are unshadowed, which is also how they behave in a real
studio.

One porting note: the bulb's antialiasing originally used `fwidth`, which is a screen-space
derivative and is unreliable inside a dynamic loop. It is now computed analytically from
`PixelWorldSize`, giving the same edge softness with no gradient instructions in the loop.

## Model

`Assets/Models/depth-anything-v2-small-fp16.onnx` comes from
[onnx-community/depth-anything-v2-small](https://huggingface.co/onnx-community/depth-anything-v2-small)
and is downloaded by `scripts/fetch-model.ps1` from a pinned revision. It is deliberately kept out
of git so clones stay small. Its input dimensions must each be a multiple of 14 (the ViT patch
size).

The original example ships a hand-written WGSL transformer; this port runs the equivalent network
through ONNX Runtime instead, which keeps the inference stack small while staying GPU-accelerated.

## Using the output as a webcam in Teams / Zoom

Not supported yet, and it is a separate project rather than a small addition. Findings:

- The right API is the **Media Foundation Virtual Camera** (`MFCreateVirtualCamera`, Windows 11
  build 22000+). MSIX packaging is *not* required — unpackaged Win32 apps work.
- The catch: `MFCreateVirtualCamera` takes the **CLSID of a separately registered COM DLL** that
  implements `IMFMediaSource`. Windows loads that DLL **into the Camera Frame Server service**,
  not into this app, and it must be registered in `HKLM` (admin). So the rendered frames have to
  cross a process boundary — in practice a D3D11 read-back to a named shared-memory segment that
  the DLL reads in `RequestSample()`.
- Teams does not hand a Direct3D manager to the media source, so the GPU-sharing shortcut is out;
  the CPU read-back path is required. Serve **NV12**, not just RGB32 — Teams' call encoder is
  reported to reject RGB32-only sources.
- There is a known unresolved bug in the reference samples where an MF virtual camera shows in the
  Teams *preview* but does not reach the far end of a call. Worth proving out before committing.
- On ARM64, MF is clearly the better path. DirectShow (and OBS's DirectShow-based virtual camera)
  cannot serve native ARM64 and emulated x64 apps at the same time, and OBS's ARM64 virtual camera
  is experimental.

Closest reference to fork: [`smourier/VCamNetSample`](https://github.com/smourier/VCamNetSample)
(C#, .NET 10, explicit ARM64 support, with a Native AOT variant of the source DLL). Realistic
effort is a couple of weeks including Teams format debugging.

## Notes on the port

- `Graphics/Shaders/Relight.hlsl` is a direct translation of the example's `shaders.ts`. The
  tunable constants are preserved, except that the temporal-filter alphas became time constants
  (for this project's lower depth rate) and the geometry is scaled into an isotropic world space
  so non-square frames light correctly.
- The camera-orientation `uvTransform`/`swapAxes` handling is omitted: desktop webcams deliver
  upright frames, so only the mirror toggle is needed.
- The square centre crop of the original is dropped in favour of processing the whole frame.
- The disparity range is stabilised on the CPU (it is only two floats) rather than in a dedicated
  compute pass, since the depth map already round-trips through system memory.
