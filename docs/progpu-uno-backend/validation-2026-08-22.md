# Validation record — 2026-08-22

## Revisions

- Uno baseline: `c4b1cd24d2c5ba5ac0a472e499f81d0ec22de2f9`
- Uno work branch: `feat/progpu-drawing-backend`
- ProGPU gitlink: `876ffc562cd047494839edbbd5762c591f9994f3`
- ProGPU dependency branch/PR: `feat/optional-winrt-contracts`, public PR #125
- Host: macOS 26.6 arm64, Apple M3 Pro, .NET SDK 10.0.201,
  runtime 10.0.5, wgpu-native/Metal

## Code-review assessment

- The provider only advertises `GraphicsContextKind.WebGpu` and receives typed
  WebGPU contexts/targets.
- Uno retains ownership of instance, adapter, device, queue, target view and
  present. ProGPU's external lifetime adapter polls but never releases them.
- Opaque handles are pointer-cast; all descriptors/enums cross the old/new
  WebGPU boundary through explicit translation.
- The target view is consumed synchronously during `BeginPresent` disposal and
  is never retained in a record.
- `Clear` uses source replacement, including transparent pixels.
- Backend geometry implements the Uno host marker required by
  `CompositionPath`.
- The native glyph object survives `IFont.BuildGlyphRun` and reaches ProGPU's
  glyph atlas when the drawing session is ProGPU; neutral layers/outlines remain
  interoperable with another backend.
- The backend project has no Skia reference. Its managed image and SVG adapters
  use the neutral services introduced by the Uno work.
- Known incomplete semantic areas are explicitly listed in the capability
  matrix and are not described as qualified.

## Compile validation

Passed, zero warnings and zero errors:

```bash
cd src
dotnet build \
  Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -c Release --no-restore

dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore
```

SamplesApp opt-in passed with zero errors and 16 existing macOS native-source
warnings:

```bash
dotnet restore \
  SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -p:UnoDrawingBackendProGpu=true
dotnet build \
  SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -c Release -p:UnoDrawingBackendProGpu=true --no-restore
```

The ordinary ProGPU-disabled SamplesApp build also passed after an incremental
graph containing the optional compatibility projection, proving the defensive
compiler alias does not make default builds depend on the opt-in flag:

```bash
dotnet build \
  SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
  -c Release -p:UnoDrawingBackendProGpu=false \
  -p:SkipMacOSAppBundle=true --no-restore
```

The repository-wide `Uno.UI-Skia-only.slnf` Release build was attempted and is
not a pass. It reached and built the new backend, smoke, and benchmark projects,
but the solution filter includes projects unavailable in this local toolchain:
unrestored mobile/WASM fixtures, net11 projects with only SDK 10 installed,
net48 without the targeting pack, and an existing package warning-as-error for
the development ICU dependency. Exact maintainer sequence:

```bash
cd src
dotnet restore Uno.UI-Skia-only.slnf
dotnet build Uno.UI-Skia-only.slnf -c Release --no-restore
```

It requires the repository's complete SDK/targeting-pack environment before
the result can be used as whole-filter evidence.

The ProGPU dependency change built in both default and
`ProGPUUseWinRTContracts=false` modes for `ProGPU.Vector`, and the false mode
also built `ProGPU.Scene`, all Release with zero warnings/errors. The full
ProGPU test project could not restore in the fresh worktree because its
Microsoft UI XAML data submodule was not initialized; that limitation is also
recorded on dependency PR #125.

## Runtime validation

Focused real-device smoke passed:

```text
[webgpu] init device — msaa=2x fmtFeatures=True colorFormat=BGRA8Unorm
ProGPU runtime smoke passed; center=DC5014FF, frame=1.
```

The smoke covers external device/queue initialization, offscreen clear and
primitives, ProGPU OpenType shaping/direct glyph recording, an isolated
drop-shadow layer, GPU readback and pixel assertions, host-owned texture/view
presentation, retained record replay, GPU completion, geometry host-marker
compatibility, and zero unsupported operations.

The Release SamplesApp was launched with `UNO_PROGPU=1`. Runtime evidence:

```text
Graphics backend 'ProGpuGraphicsProvider' won negotiation on context kind 'WebGpu'.
Found 1413 sample(s) in 132 categories.
Done loading 00:00:00.2304257
[webgpu] surface 2048x1536 format=BGRA8Unorm present=Fifo
```

An initial run found repeated `InvalidCastException` failures from
`BorderVisual.UpdatePathsAndCornerClip`: `ProGpuGeometry` did not implement the
host `Windows.Graphics.IGeometrySource2D`. The root invariant was fixed on the
producer type and a targeted smoke assertion added. Rebuild and relaunch then
remained free of backend exceptions through startup, catalog load, input-system
initialization, surface creation, and the observed presentation interval.

This is runtime startup/representative-shell evidence, not a systematic sweep
of every SamplesApp page. Windows, Linux, browser, injected device loss,
trimming/AOT, leak and long-duration stress validation remain open.

## Benchmark validation

All 15 backend/scenario diagnostic processes completed and emitted parseable
v1 JSON. ProGPU reported zero unsupported operations and semantic workload
hashes match across backends per scenario. GPU lanes included device completion
in the timed boundary. See
[performance-results-2026-08-22.md](performance-results-2026-08-22.md) for the
numbers and the explicit limits on performance confidence.

## Repository audits

- `git diff --cached --check`: clean.
- Private-name scan over implementation and documentation: clean.
- All 15 result artifacts and the schema parse as JSON.
- ProGPU PR title/body scan contains no host-framework or private-integration
  name.
- ProGPU submodule working tree is clean at the recorded gitlink.
