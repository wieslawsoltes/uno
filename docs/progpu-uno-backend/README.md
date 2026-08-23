# ProGPU drawing backend for Uno Platform

Status: working macOS/Metal implementation and qualification branch,
2026-08-23. The backend negotiates Uno's WebGPU host context, presents the
SamplesApp through ProGPU, passes the focused real-device smoke test, and has a
correctness-gated v3 benchmark matrix. The remaining qualification gaps are
listed in [capabilities.md](capabilities.md); this status does not claim
cross-platform or full effect/layer conformance.

This folder is the source of truth for the ProGPU drawing backend built on the
pluggable drawing abstraction introduced by Uno Platform PR #24153. The backend
is additive: it implements the public drawing and content seams and does not
modify the Skia or WebGPU backend implementations from that work.

Documents:

- [Analysis of Uno PR #24153 and source integrations](pr-24153-analysis.md)
- [Architecture and integration specification](architecture.md)
- [Capability and conformance matrix](capabilities.md)
- [Implementation and validation plan](implementation-plan.md)
- [Performance benchmark protocol](performance-benchmarks.md)
- [Current performance results](performance-results-2026-08-23.md)
- [Current validation record](validation-2026-08-23.md)
- [Initial diagnostic performance results](performance-results-2026-08-22.md)
- [Initial validation record](validation-2026-08-22.md)

The implementation consumes ProGPU as a git submodule pinned to immutable
`main` merge commit `d9a85cba9ccb10dd5a65d83273b66f0f9a9a8444`. Public
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
[#134](https://github.com/wieslawsoltes/ProGPU/pull/134), and
[#135](https://github.com/wieslawsoltes/ProGPU/pull/135) are merged. Together
they provide optional WinRT contracts, analytic difference clips, bounded
retained-picture compilation and eligibility caching, duplicate-stop gradient
semantics, bounded queue cleanup, GPU-only HostBackdrop capture, translated
and shadow-only effects, prompt retirement of detached effect textures, and
in-place merging of compact retained-page draw calls. Identity-only picture
recordings now share their immutable command storage directly, and explicit
completion waits advance the bounded submission-drain accounting. Retained
target stamps prevent unchanged output submission. Unfiltered source-over
layers, destination-in masks, color matrices, and all 27 Uno layer blend modes
can now isolate complete visual subtrees through retained GPU effect paths.
Independent shadow axes and additive composition are preserved, and managed
and native C++ wgpu-native lifetime operations use equivalent process-wide
synchronization domains.

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
5. Geometry, text shaping, font fallback, glyph outlines/color glyphs, images,
   SVG, effects, and drawing are registered as ProGPU implementations. A
   missing capability is reported by diagnostics and fails conformance; it is
   not silently delegated to Skia.
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
```

Individual seam overrides remain possible through Uno's existing host-builder
API, but benchmark and qualification runs use the complete ProGPU stack.

The repository SamplesApp uses an explicit opt-in because this is an
experimental backend:

```bash
cd src
dotnet restore SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -p:UnoDrawingBackendProGpu=true
dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -c Release -p:UnoDrawingBackendProGpu=true --no-restore
UNO_PROGPU=1 dotnet run \
  --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -c Release -p:UnoDrawingBackendProGpu=true --no-build --no-restore \
  --no-launch-profile
```

When a local macOS source build produces `libUnoNativeMac.dylib` without an
embedded `LC_RPATH`, prefix the final command with the directory that already
contains the copied native assets:

```bash
DYLD_LIBRARY_PATH="$PWD/SamplesApp/SamplesApp.Skia.Generic/bin/Release/net10.0/runtimes/osx/native" \
UNO_PROGPU=1 dotnet run \
  --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -c Release -p:UnoDrawingBackendProGpu=true --no-build --no-restore \
  --no-launch-profile
```

`UNO_PROGPU=1` selects ProGPU only in an application compiled with
`UnoDrawingBackendProGpu=true`. The definitive runtime check is the startup
message `Graphics backend 'ProGpuGraphicsProvider' won negotiation on context
kind 'WebGpu'.`; the environment variable alone is not proof of backend
selection.

On macOS, a locally built host library without an embedded search path requires
the same `DYLD_LIBRARY_PATH` prefix whether it is loaded through `dotnet` or
through the generated app-bundle executable.

## Focused verification

```bash
cd src
dotnet run \
  --project Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -c Release

dotnet run \
  --project Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release -- --backend progpu --scenario text --warmups 4 --samples 100
```

The smoke executable creates an actual wgpu-native device, initializes ProGPU
from borrowed device/queue handles, renders offscreen, reads back and checks
pixels, presents a retained record to a host-owned texture view, exercises the
native glyph-atlas route, and asserts the Uno `IGeometrySource2D` host marker.

The benchmark executable reports blocking frame latency, CPU submit latency,
GPU completion wait, bounded-batch throughput, ProGPU compositor stages,
retained-picture counters, and exact final-target readback. See the current
results for the required distinction between CPU-side renderer cost and the
coarse native GPU completion wait.
