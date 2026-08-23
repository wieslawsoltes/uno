# Validation record — 2026-08-23

## Revisions and environment

- Uno branch baseline: `6848a67e49`.
- Uno work branch: `feat/progpu-drawing-backend`; no Uno pull request opened.
- ProGPU gitlink: merged `main` commit
  `d5d3e977527b25897387345122d7b5688803a69c`.
- ProGPU dependency changes: public PRs #125 through #128, all merged; PR #128
  completed all 26 CI checks successfully before merge.
- Host: macOS 26.6 arm64, Apple M3 Pro, .NET SDK 10.0.201,
  runtime 10.0.5, wgpu-native/Metal.

## Root-cause changes

| Invariant | Classification | Correction |
|---|---|---|
| an exact rounded border must not require an arbitrary texture mask | root-cause fix | preserve rounded metadata, recognize uniform inset differences, and evaluate analytic ring coverage in every affected shader |
| a matching outer rounded clip must not duplicate ring coverage work | root-cause fix | reduce the nested matching pair to the ring analytic mask |
| a contained rectangular hole in a rounded clip must not allocate an offscreen mask | root-cause fix | compose the two contours into one even-odd analytic mask and retain the original outer clip when the nested scope is restored |
| unchanged nested pictures must retain compilation identity when their parent record changes | root-cause fix | cache immutable picture pages by full compilation context and replay their arrays/draw calls |
| caching must cost less than recompilation | root-cause fix | admit after reuse and reject pictures below a configurable minimum command count |
| a leading replacement clear must not become a redundant full-target draw | root-cause fix | carry the leading clear as record metadata and apply it as the attachment clear while retaining ordered nested-record replacement semantics |
| retained solid pages must not scan an empty brush map per vertex | root-cause fix | bulk-copy inline-color vertices and rebase index arrays through contiguous spans |
| duplicate gradient stops must make an exact offset select the later stop | root-cause fix | use the previous stop only for strictly smaller offsets in vector and hatch shaders |
| solid ellipse strokes must retain their analytic curve | root-cause fix | preserve ellipse radii and emit exact even-odd rounded-ring geometry instead of a 22.5-degree flattened outline |
| queue cleanup must bound residency without serializing every small burst | root-cause fix | expose the conservative drain bound and select a 64-submission window while retaining per-frame non-blocking polling |
| HostBackdrop must sample already-rendered content without sampling the borrowed swapchain view | root-cause fix | conditionally render backdrop frames into a bindable same-device texture, split and ping-pong at ordered backdrop commands, then GPU-blit to the borrowed view |
| an effect-backdrop command must occupy the same transformed coordinates as its clip and border | root-cause fix | record the session's current matrix on the backdrop command instead of identity; a translated pixel fixture covers the formerly missing region |
| an effect layer must rasterize the content it actually recorded, including non-zero and transformed coordinates | root-cause fix | accumulate conservative transformed primitive bounds, set `EffectContentBounds`, and propagate nested effect output bounds to the parent layer |
| a shadow-only framework filter must not composite its source twice | root-cause fix | add an invalidating `DrawSource` effect property whose default remains source-plus-shadow, and opt the adapter into shadow-only composition followed by explicit source replay |
| detached effect resources must not disable a later effect-free scene cache | root-cause fix | retire unused effect textures before compiled-scene cache admission and validate zero remaining effect bytes before the next cache hit |
| disposing a backend that borrowed the host WebGPU context must not release that device | root-cause fix | keep device ownership with the host and validate another native allocation after factory disposal |
| Uno blur sigma and ProGPU backdrop radius must have a measured conversion | measurement correction | compare identical pixel fixtures at 1.8×, 2.0×, 2.2×, and 3.344× and select the lowest-error 2.0× mapping |
| benchmark pixels must represent one frame | measurement correction | explicitly clear every frame and read back the final target after timing |
| GPU wait must not be mislabeled as renderer CPU time | measurement correction | publish CPU submit, GPU completion, blocking total, batch throughput, and ProGPU internal stages separately |

Arbitrary/non-uniform geometry, masks, hit testing, unsupported draw commands,
volatile variants, and stale atlas generations fail closed to normal
compilation. Cache capacity, variants, and age are bounded.

## Compile validation

The runtime test, benchmark, and their complete dependency graphs built in
Release with zero warnings and zero errors using serial MSBuild:

```bash
cd src
dotnet build \
  Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -c Release --no-restore -m:1
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1
```

An earlier parallel attempt was not code evidence: duplicate Uno project
builds raced while rewriting `Uno.dll` and `Uno.UI.pdb`. The serial rerun
resolved the environmental file-lock race without source changes.

## Dependency tests

The current ProGPU branch passes the complete locally available suites:

```text
ProGPU.Tests:          3,700 passed, 0 failed, 0 skipped
ProGPU.Tests.Headless:   240 passed, 0 failed, 0 skipped
```

`ShapingContractsTests` is excluded from the first command because that lane
depends on separately provisioned upstream source contracts; the dedicated CI
text-contract job supplies that coverage. The 27-test focused effect and visual
change-version run also passes. It covers translated effect placement,
shadow-only output, detached resource retirement, and cache invalidation when
`DrawSource` changes.

The regression verifies final pixels after changing one nested picture while
unchanged siblings reuse compiled pages. The adaptive-admission variant uses
four-command pictures and validates that the minimum-command gate does not
disable profitable subtree reuse. The real-device smoke additionally asserts
that a cleared cached presentation emits one content draw/four vertices, and
that replaying a cleared record after existing content still replaces the
earlier pixels in order.

## Runtime and visual validation

The focused real-device executable covers typed context negotiation,
borrowed-device ownership, offscreen rendering, native glyph recording,
effects, GPU readback, retained replay, completion, geometry host-marker
compatibility, and zero unsupported operations. It also verifies HostBackdrop
blur/capture, foreground ordering, translated and nested effect-layer bounds,
shadow-only source omission, gradient/non-uniform-rounded/border/path/stroke/
line/image/color-filtered-image/nine-slice content-bound propagation,
rounded-outer/rectangular-hole clip restoration, and the conditional
present/blit route. Its final
Release run reports:

```text
[webgpu] init device — msaa=2x fmtFeatures=True colorFormat=BGRA8Unorm
ProGPU runtime smoke passed; center=DC5014FF, frame=6.
```

SamplesApp was built with
`UnoDrawingBackendProGpu=true`; startup confirms provider negotiation with:

```text
Graphics backend 'ProGpuGraphicsProvider' won negotiation on context kind 'WebGpu'.
```

The same final binary was launched once with `UNO_PROGPU=1` and once without
it. The gallery title and status surface reported `ProGPU · WebGPU · macOS`
for the first run and `Skia · Metal · macOS` for the second. A locally built
`libUnoNativeMac.dylib` had no `LC_RPATH`; the runtime invariant was restored
without source changes by pointing `DYLD_LIBRARY_PATH` at the already copied
`runtimes/osx/native` directory. `--no-launch-profile` avoids the unrelated
commented launch-settings parse warning.

This check caught a launch-configuration trap during validation: setting
`UNO_PROGPU=1` on a binary compiled with the flag disabled still launches the
normal Skia backend. Only the negotiation log is accepted as proof.

Matched live catalog captures verify the complete shell rather than the blank
frame in the original failure report. The text sample content crop measures
0.1559 RGB MAE against Skia. The static rounded-border sample measures 0.4444
RGB MAE. After duplicate-stop and analytic ellipse-stroke corrections, the
gradient sample measures 0.9457 RGB MAE over the scene; the 100×100 stroked
ellipse has 0.8326 RGB MAE and no pixel above a 32-level channel difference.
The shader path is exercised for vector, text, texture, retained glyph, image
effect, backdrop, and compatible effect output. This is macOS/Metal evidence;
Windows, Linux, browser, injected device loss, trimming/AOT, leak, and
long-duration multi-page sweep remain open.

A fresh matched `Border_CornerRadius` capture from the merged gitlink confirms
the live sample geometry remains visually aligned. The first five rounded
shapes measure 0.409735 RGB MAE, with 0.5288% of pixels above a 32-level
channel difference; the cyan ellipse fixture measures 0.055579 RGB MAE and
0.1198% above 32. Differences remain concentrated on antialiased edges. Raw
captures are under
`src/artifacts/performance/2026-08-23-final-merge-gallery/`.

The live AutoSuggestBox popup produced an opt-in scene dump at frame 34 with
an Acrylic/HostBackdrop command (`blur=60`, `luminosity alpha=0.847`) and an
offscreen-render cache-miss reason, proving the GPU capture route was active.
The dump identified an identity backdrop transform next to clips at
`[2,0,0,2,8,210]`; after correction the popup covers its complete transformed
height. A matched 988×768 capture measures 0.4985 RGB MAE over the full window,
2.3644 over the popup region, and 2.2757 over its formerly broken lower region
against Skia. The corrected diagnostic frame reports 2.669 ms total and
1.146 ms render-pass CPU. These are live parity diagnostics, not a standalone
effects throughput benchmark.

## Benchmark validation

- All 112 primary ProGPU/Skia processes completed: eight fresh processes for
  each backend across seven scenarios. The first six scenarios use 100
  blocking samples plus nine 60-frame batches. Effects uses 40 blocking
  samples plus three 20-frame batches, for 100 measured frames per process.
- Every artifact contains semantic and final-target SHA-256 values.
- Cached and sparse ProGPU readbacks are byte-identical to Skia.
- ProGPU reported zero unsupported operations.
- CPU frame submit and bounded-batch throughput are faster than Skia in every
  process of all seven workloads.
- Conservative bounded-batch speedups range from 1.35× for sparse mutation to
  98.40× for effects. Paired-median speedups range from 1.38× to 101.59×.
- Every ProGPU final-target hash matches its corresponding pre-optimization
  artifact where semantics did not change. The effects hash intentionally
  changed when shadow-only source replay and sigma calibration were corrected,
  then remained stable across all eight final processes.
- ProGPU text/path raster differences are smaller than Uno WebGPU differences
  against the same Skia reference.
- The effects fixture measures 3.411 RGB MAE, 19.4972% of pixels above eight
  levels, and 0.5625% above 32; visual inspection confirms correct placement,
  ordering, opacity, and shadow direction with a remaining fixed-tap versus
  Gaussian blur-footprint difference.
- A final-gitlink smoke retained clip hash `2711C481...`, effects semantic hash
  `143132D5...`, and ProGPU/Skia effects pixel hashes `AAF267FF...` /
  `EE3C6A61...`; the clips frame used zero mask passes/textures and both final
  scenarios reported zero unsupported operations.

See [performance-results-2026-08-23.md](performance-results-2026-08-23.md) for
the exact values and interpretation boundaries.

## Remaining qualification work

1. Promote the earlier Uno WebGPU context lane to eight balanced fresh-process
   triplets; the primary ProGPU/Skia lane now has eight pairs.
2. Add WebGPU timestamps; managed CPU and Metal traces have been captured, but
   their instrumentation overhead excludes them from comparative timing
   tables.
3. Add scrolling, control-density, first-present, startup, allocation,
   settled-memory, and leak scenarios.
4. Run a systematic SamplesApp page/pixel sweep and a long-duration resize,
   DPI, occlusion, minimize/restore, and device-loss sequence.
5. Run Windows/D3D12, Linux/Vulkan, browser, trimming, and NativeAOT lanes.
6. Complete anisotropic/additive shadows and arbitrary color-filter/effect DAG
   layer isolation.
