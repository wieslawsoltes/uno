# ProGPU drawing backend for Uno Platform

Status, 2026-09-22: checkpoint of the integration updated to Uno Platform
[PR #24153](https://github.com/unoplatform/uno/pull/24153) at
`e82519bc235faabdf2e9d78598573a7f3e28dba4`, on
`feat/progpu-drawing-backend-latest`. The original
`feat/progpu-drawing-backend` branch is preserved. The GPU smoke suite and two
Gallery Lottie runtime tests pass. Generic live-backdrop blend graphs remain
unsupported; browser and native C++ custom-shader qualification remain open.
Work is paused at the user's request. Current evidence and remaining work are in the
[API update record](api-update-2026-09-22.md).

The August 23–24 validation and benchmark reports describe the previous API
and renderer revisions. Their timings, ratios, and correctness findings are
historical evidence, not measurements of the updated implementation. The
built-in WebGPU renderer has changed substantially, and the migrated benchmark
fixtures have a new semantic hash version. New comparisons must rerun pixel
qualification and matched GPU completion measurements for all backends.

This folder is the source of truth for the ProGPU drawing backend built on the
pluggable drawing abstraction introduced by Uno Platform PR #24153. The backend
is additive: it implements the public drawing and content seams and does not
modify the Skia or WebGPU backend implementations from that work.

Documents:

- [Current API migration and validation record](api-update-2026-09-22.md)
- [Historical rendering contract and three-backend review, August 24](final-rendering-contract-review-2026-08-24.md)
- [Analysis of Uno PR #24153 and source integrations](pr-24153-analysis.md)
- [Architecture and integration specification](architecture.md)
- [Capability and conformance matrix](capabilities.md)
- [Implementation and validation plan](implementation-plan.md)
- [Performance benchmark protocol](performance-benchmarks.md)
- [Historical performance results, August 23](performance-results-2026-08-23.md)
- [Historical validation record, August 23](validation-2026-08-23.md)
- [Initial diagnostic performance results](performance-results-2026-08-22.md)
- [Initial validation record](validation-2026-08-22.md)

The implementation consumes ProGPU as a git submodule pinned to immutable
`main` merge commit `93ca8d1170a8911cf5b4f94b6c380663cea48f9f`; this migration
does not change that pin. The existing integration foundation includes public
dependency changes
[#125](https://github.com/wieslawsoltes/ProGPU/pull/125),
[#126](https://github.com/wieslawsoltes/ProGPU/pull/126),
[#127](https://github.com/wieslawsoltes/ProGPU/pull/127),
[#128](https://github.com/wieslawsoltes/ProGPU/pull/128),
[#129](https://github.com/wieslawsoltes/ProGPU/pull/129),
[#130](https://github.com/wieslawsoltes/ProGPU/pull/130),
[#131](https://github.com/wieslawsoltes/ProGPU/pull/131),
[#132](https://github.com/wieslawsoltes/ProGPU/pull/132),
[#133](https://github.com/wieslawsoltes/ProGPU/pull/133),
[#134](https://github.com/wieslawsoltes/ProGPU/pull/134),
[#135](https://github.com/wieslawsoltes/ProGPU/pull/135),
[#136](https://github.com/wieslawsoltes/ProGPU/pull/136), and
[#137](https://github.com/wieslawsoltes/ProGPU/pull/137) are merged. Together
they provide optional WinRT contracts, analytic difference clips, bounded
retained-picture compilation and eligibility caching, duplicate-stop gradient
semantics, bounded queue cleanup, GPU-only HostBackdrop capture, translated
and shadow-only effects, prompt retirement of detached effect textures, and
in-place merging of compact retained-page draw calls. Identity-only picture
recordings now share their immutable command storage directly, and explicit
completion waits advance the bounded submission-drain accounting. Retained
target stamps prevent unchanged output submission. Unfiltered source-over
layers, destination-in masks, color matrices, and the existing blend operations
can now isolate complete visual subtrees through retained GPU effect paths.
Bounded completion polling, compatible texture-draw batching, and cached-scene
render-bundle replay are implemented in both managed C# and native C++; the
Dawn provider also records and replays the retained bundles on Metal.
Independent shadow axes and additive composition are preserved, and managed
and native C++ wgpu-native lifetime operations use equivalent process-wide
synchronization domains. These statements describe the pinned dependency's
existing implementation; they are not new performance results for this update.

## Non-negotiable invariants

1. The presentation path is GPU-only: Uno's host-owned WebGPU texture view is
   borrowed for the frame and rendered by ProGPU on the same device. No CPU
   bitmap readback, `WriteableBitmap`, Skia surface, or software presentation
   bridge is allowed.
2. Uno owns scheduling, target acquisition, damage, and presentation. ProGPU
   owns translation, retained resources, pipelines, atlases, and GPU commands.
3. The Uno WebGPU context owns the instance, adapter, device, queue, and target
   view. ProGPU borrows them and never releases host-owned handles.
4. Device loss is one generation boundary. The ProGPU context, compositor,
   pipelines, atlases, textures, retained recordings, and pending readbacks are
   invalidated together.
5. Geometry, text shaping, font fallback, glyph outlines/color glyphs, effects,
   and drawing use the ProGPU integration. Image codecs and SVG use the
   existing managed content adapters; Lottie uses Uno's managed shape-layer
   engine. All draw through the registered ProGPU backend. A missing
   capability must be reported and cannot silently select a Skia renderer.
6. Performance claims require correct pixels, zero unsupported operations for
   the measured scene, raw samples, and reproducible environment metadata.
7. Every managed ProGPU rendering optimization requires a native C++
   applicability audit. Applicable work lands in both implementations with
   matched behavior, quality, lifetime, allocation/upload, and performance
   gates; a concrete ownership mismatch must be documented for any one-sided
   change.

## Intended app registration

```csharp
builder.UseProGpuDrawingBackend();
```

The extension registers the following independent Uno seams as one coherent
stack:

```text
GraphicsBackend       -> ProGpuGraphicsProvider
GeometryFactory       -> ProGpuGeometryFactory
FontProvider          -> ProGpuFontProvider
ImageEncoderDecoder   -> ProGpuImageEncoderDecoder
SvgRenderer           -> ProGpuSvgRenderer
LottieRenderer        -> ManagedLottieRenderer
```

Individual seam overrides remain possible through Uno's existing host-builder
API, but benchmark and qualification runs use the complete ProGPU stack.

The consolidated SamplesApp enables ProGPU through a desktop-only opt-in.
`UnoTargetFrameworkOverride=net10.0-desktop` selects that head while the shared
libraries use `net10.0`. Run from the repository root:

```bash
cd src
dotnet restore SamplesApp/SamplesApp/SamplesApp.csproj \
  -p:UnoTargetFrameworkOverride=net10.0-desktop \
  -p:UnoDrawingBackendProGpu=true -p:UnoFastDevBuild=true \
  -p:ProGPUUseWinRTContracts=false
dotnet build SamplesApp/SamplesApp/SamplesApp.csproj \
  -f net10.0-desktop -c Release --no-restore \
  -p:UnoTargetFrameworkOverride=net10.0-desktop \
  -p:UnoDrawingBackendProGpu=true -p:UnoFastDevBuild=true \
  -p:ProGPUUseWinRTContracts=false
UNO_PROGPU=1 UNO_WEBGPU_BACKENDS=metal dotnet run \
  --project SamplesApp/SamplesApp/SamplesApp.csproj \
  -f net10.0-desktop -c Release --no-build --no-restore \
  -p:UnoTargetFrameworkOverride=net10.0-desktop \
  -p:UnoDrawingBackendProGpu=true -p:UnoFastDevBuild=true \
  -p:ProGPUUseWinRTContracts=false \
  --no-launch-profile
```

`UNO_PROGPU=1` selects ProGPU only in an application compiled with
`UnoDrawingBackendProGpu=true`. The definitive runtime check is the startup
message `Graphics backend 'ProGpuGraphicsProvider' won negotiation on context
kind 'WebGpu'.`; the environment variable alone is not proof of backend
selection. `UNO_WEBGPU_BACKENDS=metal` makes the underlying macOS WebGPU API
selection explicit instead of relying on wgpu-native's platform default.

The current native provisioning copies wgpu-native under the import name
`libwebgpu.dylib` on macOS. If a locally built `libUnoNativeMac.dylib` has no
embedded native-asset search path, set `DYLD_LIBRARY_PATH` to the actual native
asset directory of this build. Do not reuse the old deleted sample head's
output path.

## Focused verification

```bash
cd src
UNO_WEBGPU_BACKENDS=metal dotnet run \
  --project Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -f net10.0 -c Release -p:UnoTargetFrameworkOverride=net10.0 \
  -p:UnoFastDevBuild=true -p:ProGPUUseWinRTContracts=false

UNO_WEBGPU_BACKENDS=metal dotnet run \
  --project Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -f net10.0 -c Release -p:UnoTargetFrameworkOverride=net10.0 \
  -p:UnoFastDevBuild=true -p:ProGPUUseWinRTContracts=false \
  -- --backend progpu --scenario text --warmups 4 --samples 100 --force-redraw
```

The smoke executable creates an actual wgpu-native device, initializes ProGPU
from borrowed device/queue handles, renders offscreen, reads back and checks
pixels, presents a retained record to a host-owned texture view, exercises the
native glyph-atlas route, and asserts the Uno `IGeometrySource2D` host marker.

The benchmark executable reports blocking frame latency, CPU submit latency,
GPU completion wait, bounded-batch throughput, ProGPU compositor stages,
retained-picture counters, and exact final-target readback. Use `skia-metal`
for real Skia GPU rendering on macOS; `skia` is the CPU raster baseline and
`webgpu` selects the built-in renderer. The JSON structure remains v4 while
the fixture semantic-hash input is v3 after the API migration. Keep historical
results separate and follow the [benchmark protocol](performance-benchmarks.md)
for pixel gates and CPU-submit versus completed-GPU measurements.

The new browser contract exposes JavaScript `GPUDevice` and `GPUTextureView`
objects. A direct browser ProGPU provider is not implemented in this branch;
the native provider rejects browser execution. See the dedicated browser
section in the [API update record](api-update-2026-09-22.md).
