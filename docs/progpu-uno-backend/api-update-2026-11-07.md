# Uno master and ProGPU update — 2026-11-07

The local `feat/progpu-drawing-backend-latest` branch integrates Uno master
`968398de4a1f46032ac2c20c8cceab9a14a032e3`, including the final head of
[PR #24153](https://github.com/unoplatform/uno/pull/24153)
(`6789ab0058acc269afc347e91115a70e0658a439`). ProGPU is updated from
`93ca8d1170a8911cf5b4f94b6c380663cea48f9f` to
`23f68615192ef5fb96beb72262d9a093340064df`, the fetched `main` head, one commit
after `v0.1.0-preview.70`. These are immutable snapshots of the upstream heads
checked during this update. The original integration branch is preserved.
The integration and regression tests are committed as `220a0342259`.

## Integration and root-cause fixes

- **Root-cause fix — host API ownership:** the backend now forwards through
  `IWebGpuDeviceContext.Api`. It borrows the host's typed device, queue, and
  texture view and never releases those handles. The obsolete independent
  native ABI adapter is removed. Two-dimensional render-pass attachments use
  an undefined depth slice; a zero slice caused native WebGPU validation to abort.
- **Root-cause fix — concurrent rendering:** native presentation and UI-thread
  offscreen captures share one mutable ProGPU compositor. Render, capture,
  readback, upload, and teardown transactions now use its context render lock.
  Frame notifications run outside the lock, permitting a subscriber to wait
  synchronously for readback without deadlocking.
  A two-thread regression reproduced corrupted pending-upload state before the
  fix; the UI border suite also exposed a native GPU draw validation failure.
- **Contract migration:** presentation accepts damage and conservatively repaints
  the full target. SVG, managed content factory creation, asynchronous font
  matching, font tables, and shaping options match current Uno interfaces.
- **Root-cause fix — rounded corners:** every corner retains independent X/Y
  radii. Adjacent-radius normalization preserves valid asymmetric corners
  larger than half the rectangle. Uniform circular and square borders retain
  the analytic stroke path; other borders use geometry difference.
- **Root-cause fix — effects:** texture input scales are applied. Shadow blur
  covariance follows scale and rotation, including anisotropic oblique shadows.
  Effect captures round outwards to the pixel grid, preventing an identity
  effect layer from resampling its contents.
- **Root-cause fix — retained effects:** replayed records carry their content
  bounds into effect layers, preserving non-analytic ThemeShadow output.
  Static color/texture blend inputs are compiled and composited on the GPU;
  their opacity and texture ownership survive retained replay.
- **Root-cause fix — geometry:** dash lengths and offsets use geometry units,
  converted to width multiples only at the ProGPU pen boundary. Curve
  subdivision uses the requested rasterization scale for flatten/trim/widen.
- **Root-cause fix — text:** inferred shaping direction uses Unicode segment
  properties. Explicit language/script and feature options reach the shaper;
  regional BCP47 tags preserve supported localized substitutions. Font table
  reads return independent copies.
- SamplesApp preserves the current Uno shell and backend diagnostics. Compiling
  ProGPU without selecting it still permits managed default content services.
  Benchmark fixtures use public backend factories and semantic hash version 4
  to distinguish current geometry-unit dash semantics.

## Validation environment

macOS arm64, Apple M3 Pro, .NET SDK 10.0.201, runtime 10.0.5, native WebGPU
provisioned by Uno (`wgpu-native` v29.0.1.1). `UNO_WEBGPU_BACKENDS=metal` selects
Metal explicitly. The GPU harness uses Uno's API bridge and backend selection.

Builds and runtime runs must be sequential: project outputs can share hard-linked
assemblies. Rebuilding a dependency while SamplesApp is running can overwrite
loaded assemblies and invalidate the run. An initial overlapping run was
excluded from the evidence below.

**Compile:** ProGPU runtime smoke and benchmark projects build for `net10.0`
with zero warnings/errors. SamplesApp builds for `net10.0-desktop` with ProGPU
selected at compile time. Flags used:

```sh
dotnet build src/Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -c Release -f net10.0 -p:UnoTargetFrameworkOverride=net10.0 \
  -p:UnoFastDevBuild=true -p:ProGPUUseWinRTContracts=false

dotnet build src/Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release -f net10.0 -p:UnoTargetFrameworkOverride=net10.0 \
  -p:UnoFastDevBuild=true -p:ProGPUUseWinRTContracts=false

dotnet build src/SamplesApp/SamplesApp/SamplesApp.csproj \
  -c Release -f net10.0-desktop -p:UnoTargetFrameworkOverride=net10.0-desktop \
  -p:UnoDrawingBackendProGpu=true -p:UnoFastDevBuild=true \
  -p:ProGPUUseWinRTContracts=false
```

**Runtime — focused GPU suite:** real Metal rendering, readback and assertions
cover host presentation, retained textures, content adapters and managed Lottie,
elliptical fills/borders/clips, large unequal corners, texture scale, scaled and
rotated shadows, geometry-unit dashes, magnified curves, inferred RTL text,
explicit kerning/localization, and font-table ownership. Multiple factories
share a host device; disposing one leaves the other functional across differently
sized targets. Small damage rectangles still result in correct full repaint.
Concurrent presentation, offscreen capture, and readback execute 32 iterations
per worker with pixel assertions. A frame-completion handler also waits for
readback under a bounded timeout. Retained shadow replay matches immediate
rendering byte-for-byte, and fixed-input multiply blends cover color and texture
foregrounds at half opacity.

The 45-degree shadow differs from an independently rotated pixel reference by
0.065 alpha levels on average (0–255 range); nesting it in an identity effect
produces identical pixels. Texture-scale, shadow-scale, inferred-direction,
regional Romanian localization, and oblique identity-layer regressions were
observed before their fixes and pass afterwards.

```sh
UNO_WEBGPU_BACKENDS=metal dotnet \
  src/Uno.UI.Composition.ProGpu.RuntimeTests/bin/Release/net10.0/Uno.UI.Composition.ProGpu.RuntimeTests.dll
```

**Runtime — SamplesApp:** **77 passed, 0 failed, 2 inconclusive, 1 skipped**.
The six filtered classes are `Given_Border`, `Given_ManagedLottie`,
`Given_DrawingBackend_Conformance`, `Given_EffectBrush_Parity`,
`Given_Visual_Damage`, and `Given_RenderTargetBitmap`. The app logged
`ProGpuGraphicsProvider` winning negotiation on `WebGpu`. The final log contains
no render-thread exception, native panic, or GPU validation error. Temporary
startup drawable unavailability recovered before test execution.

The run uses the runtime-tests workflow: base64-encode the pipe-separated fully
qualified class names into `UITEST_RUNTIME_TESTS_FILTER`, set `UNO_PROGPU=1` and
`UNO_WEBGPU_BACKENDS=metal`, and launch the already-built `SamplesApp.dll` with
`--runtime-tests=<absolute XML output path>`. On this machine, `DYLD_LIBRARY_PATH`
includes the output's `runtimes/osx/native`, `runtimes/osx-arm64/native`, and root
output directory. The broader unit suite described below is a separate run.

**Runtime — benchmark harness:** all **21 smoke invocations** complete with
GPU completion and final-target pixel readback. ProGPU runs all 15 scenarios:
cached, sparse, text, paths, strokes, materials, layers, isolation-layers,
mask-layers, blend-layers, blend-corpus, images, clips, shadows, and effects.
Every ProGPU result reports zero unsupported operations. Skia Metal and Uno
WebGPU each run cached, text, and images. Each invocation uses `--warmups 1
--samples 2 --batches 1 --batch-size 2`, with its backend/scenario and a JSON
output path. These are execution checks, not a statistical performance study
or cross-backend pixel-parity qualification. No new comparative performance
claim is made; historical timings remain historical.

**Compile / Runtime — broader Uno unit suite:** the full unit solution cannot
build with the installed SDK because `Uno.UI.RemoteControl.TestProcessor`
requires .NET 11. The standalone `Uno.UI.UnitTests` project builds cleanly for
.NET 10. Its run reports **4,377 passed, 119 failed, 23 skipped** (4,519 total).
Failures comprise ResourceCandidate (44), AbsolutePathComparer (28), Android
resource conversion (20), Calendar (12), Android resource-name encoding (10),
iOS resource conversion (4), and TimePicker (1). These tests do not exercise the
ProGPU renderer. A subsequent clean-master run at
`968398de4a1f46032ac2c20c8cceab9a14a032e3`, in an isolated worktree with the
same SDK, target framework, Release configuration, Europe/Warsaw timezone, and
native ICU library paths, reports the same **4,377 passed, 119 failed, 23 skipped**.
Comparison by fully qualified test class and data-row display name finds **119
shared failures, zero integration-only failures, and zero master-only failures**.
These broader failures are therefore excluded from the ProGPU fix scope. No
unrelated path, globalization, or TimePicker fixes are included in this branch.

**Code review:** eight lenses reviewed the integration. Contract, graphics
correctness, test coverage, performance, and configuration findings were fixed
and re-reviewed. Compilation and runtime evidence are distinguished from
inspection throughout this record.

## Qualification boundaries

- Native desktop Metal is the runtime-qualified target in this update. Windows,
  Linux, browser, and native C++ custom-shader paths were not qualified here.
  A browser ProGPU provider remains unimplemented and is explicitly rejected.
- Generic live-backdrop blend graphs remain unsupported. A negative test checks
  explicit failure, one diagnostic, and unchanged drawing state/pixels; this
  does not assert complete effect-graph parity.
- Combined geometry can still be resolved by ProGPU's fixed-tolerance path
  boolean solver before subdivision. The magnified curve guarantee does not
  establish full combined-geometry parity.
- Presentation currently repaints the full target; damage-region optimization
  is not implemented. This is allowed by Uno's presentation contract.
- Device loss, other GPU drivers, and external WebGPU texture interop are not
  covered by the resize/shared-device test. No blanket WinUI parity claim is made.
