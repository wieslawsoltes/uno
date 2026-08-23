# Validation record — 2026-08-23

## Revisions and environment

- Uno baseline: `c4b1cd24d2c5ba5ac0a472e499f81d0ec22de2f9`.
- Uno work branch: `feat/progpu-drawing-backend`; no Uno pull request opened.
- ProGPU gitlink: `7b050a9c44decfb04c6bf2a6ff134dee84a58b17`.
- ProGPU dependency changes: public PRs #125 and #126.
- Host: macOS 26.6 arm64, Apple M3 Pro, .NET SDK 10.0.201,
  runtime 10.0.9, wgpu-native/Metal.

## Root-cause changes

| Invariant | Classification | Correction |
|---|---|---|
| an exact rounded border must not require an arbitrary texture mask | root-cause fix | preserve rounded metadata, recognize uniform inset differences, and evaluate analytic ring coverage in every affected shader |
| a matching outer rounded clip must not duplicate ring coverage work | root-cause fix | reduce the nested matching pair to the ring analytic mask |
| unchanged nested pictures must retain compilation identity when their parent record changes | root-cause fix | cache immutable picture pages by full compilation context and replay their arrays/draw calls |
| caching must cost less than recompilation | root-cause fix | admit after reuse and reject pictures below a configurable minimum command count |
| a leading replacement clear must not become a redundant full-target draw | root-cause fix | carry the leading clear as record metadata and apply it as the attachment clear while retaining ordered nested-record replacement semantics |
| retained solid pages must not scan an empty brush map per vertex | root-cause fix | bulk-copy inline-color vertices and rebase index arrays through contiguous spans |
| duplicate gradient stops must make an exact offset select the later stop | root-cause fix | use the previous stop only for strictly smaller offsets in vector and hatch shaders |
| solid ellipse strokes must retain their analytic curve | root-cause fix | preserve ellipse radii and emit exact even-odd rounded-ring geometry instead of a 22.5-degree flattened outline |
| queue cleanup must bound residency without serializing every small burst | root-cause fix | expose the conservative drain bound and select a 64-submission window while retaining per-frame non-blocking polling |
| HostBackdrop must sample already-rendered content without sampling the borrowed swapchain view | root-cause fix | conditionally render backdrop frames into a bindable same-device texture, split and ping-pong at ordered backdrop commands, then GPU-blit to the borrowed view |
| an effect-backdrop command must occupy the same transformed coordinates as its clip and border | root-cause fix | record the session's current matrix on the backdrop command instead of identity; a translated pixel fixture covers the formerly missing region |
| benchmark pixels must represent one frame | measurement correction | explicitly clear every frame and read back the final target after timing |
| GPU wait must not be mislabeled as renderer CPU time | measurement correction | publish CPU submit, GPU completion, blocking total, batch throughput, and ProGPU internal stages separately |

Arbitrary/non-uniform geometry, masks, hit testing, unsupported draw commands,
volatile variants, and stale atlas generations fail closed to normal
compilation. Cache capacity, variants, and age are bounded.

## Compile validation

The benchmark and its complete dependency graph built in Release with zero
warnings and zero errors using serial MSBuild:

```bash
cd src
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1
```

An earlier parallel attempt was not code evidence: duplicate Uno project
builds raced while rewriting `Uno.dll` and `Uno.UI.pdb`. The serial rerun
resolved the environmental file-lock race without source changes.

## Dependency tests

The focused ProGPU queue-context suite passes:

```text
Passed: 30, Failed: 0, Skipped: 0
```

The combined backdrop-material, gradient, layer-render, text-rendering-mode,
and queue-context suites pass:

```text
Passed: 78, Failed: 0, Skipped: 0
```

The expanded backdrop-material suite, including six consecutive frames of a
scaled nested HostBackdrop, passes 7/7. The Uno real-device smoke now also
samples a translated portion of a backdrop blur that was outside the
identity-transformed extension quad before the fix.

A full dependency run passed 3,756 of 3,757 tests. The sole failure was the
allocation-sensitive `RepeatedPrehistorySamplingIsAllocationFree` check after
49,200 bytes were attributed during the combined run; its immediate isolated
rerun passed with zero failures. The three spline/static-extension pixel tests
most relevant to compositor extension batching passed in the rebuilt run.

The regression verifies final pixels after changing one nested picture while
unchanged siblings reuse compiled pages. The adaptive-admission variant uses
four-command pictures and validates that the minimum-command gate does not
disable profitable subtree reuse. The real-device smoke additionally asserts
that a cleared cached presentation emits one content draw/four vertices, and
that replaying a cleared record after existing content still replaces the
earlier pixels in order.

## Runtime and visual validation

The focused real-device executable covers borrowed-device initialization,
offscreen rendering, native glyph recording, effects, GPU readback, retained
replay, completion, geometry host-marker compatibility, and zero unsupported
operations. It also verifies HostBackdrop blur/capture, foreground ordering,
and the conditional present/blit route. SamplesApp was built with
`UnoDrawingBackendProGpu=true`; startup confirms provider negotiation with:

```text
Graphics backend 'ProGpuGraphicsProvider' won negotiation on context kind 'WebGpu'.
```

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

- All 30 current backend/scenario processes completed as two fresh-process
  repetitions with 8 warmups, 100 measured frames, 9 batches of 60 frames,
  and parseable v2 JSON.
- Every artifact contains semantic and final-target SHA-256 values.
- Cached and sparse ProGPU readbacks are byte-identical to Skia.
- ProGPU reported zero unsupported operations.
- CPU frame submit is faster than Skia in all five workloads.
- The current qualification matrix shows faster ProGPU saturated throughput
  than Skia in both repetitions of cached, sparse, text, paths, and images. The
  conservative speedups range from 1.64× for sparse mutation to 37.71× for
  text.
- Every ProGPU final-target hash matches its corresponding pre-optimization
  artifact, including the text hash.
- ProGPU text/path raster differences are smaller than Uno WebGPU differences
  against the same Skia reference.

See [performance-results-2026-08-23.md](performance-results-2026-08-23.md) for
the exact values and interpretation boundaries.

## Remaining qualification work

1. Run eight balanced fresh-process triplets with power/thermal controls.
2. Add WebGPU timestamps; managed CPU and Metal traces have been captured, but
   their instrumentation overhead excludes them from comparative timing
   tables.
3. Add effects, scrolling, control-density, first-present, startup, allocation,
   settled-memory, and leak scenarios.
4. Run a systematic SamplesApp page/pixel sweep and a long-duration resize,
   DPI, occlusion, minimize/restore, and device-loss sequence.
5. Run Windows/D3D12, Linux/Vulkan, browser, trimming, and NativeAOT lanes.
