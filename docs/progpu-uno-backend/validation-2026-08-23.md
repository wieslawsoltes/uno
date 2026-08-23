# Validation record — 2026-08-23

## Revisions and environment

- Uno baseline: `c4b1cd24d2c5ba5ac0a472e499f81d0ec22de2f9`.
- Uno work branch: `feat/progpu-drawing-backend`; no Uno pull request opened.
- ProGPU gitlink: `13a80069699d99c2ff9d5f8762fd645bfaebb9f8`.
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

The ProGPU retained-picture and text-rendering-mode suites pass:

```text
Passed: 39, Failed: 0, Skipped: 0
```

The regression verifies final pixels after changing one nested picture while
unchanged siblings reuse compiled pages. The adaptive-admission variant uses
four-command pictures and validates that the minimum-command gate does not
disable profitable subtree reuse.

## Runtime and visual validation

The focused real-device executable covers borrowed-device initialization,
offscreen rendering, native glyph recording, effects, GPU readback, retained
replay, completion, geometry host-marker compatibility, and zero unsupported
operations. SamplesApp startup confirms provider negotiation with:

```text
Graphics backend 'ProGpuGraphicsProvider' won negotiation on context kind 'WebGpu'.
```

The representative catalog frame visually matches the Skia baseline after the
rounded-difference fix. The shader path is exercised for vector, text,
texture, retained glyph, image effect, backdrop, and compatible effect output.
This is macOS/Metal evidence; Windows, Linux, browser, injected device loss,
trimming/AOT, leak, and long-duration multi-page sweep remain open.

## Benchmark validation

- All 15 backend/scenario processes completed with 8 warmups, 100 measured
  frames, 9 batches of 60 frames, and parseable v2 JSON.
- Every artifact contains semantic and final-target SHA-256 values.
- Cached and sparse ProGPU readbacks are byte-identical to Skia.
- ProGPU reported zero unsupported operations.
- CPU frame submit is faster than Skia in all five workloads.
- Saturated batch throughput is faster than Skia for text, paths, and images;
  cached and sparse fill-only throughput remains slower and is documented.
- ProGPU text/path raster differences are smaller than Uno WebGPU differences
  against the same Skia reference.

See [performance-results-2026-08-23.md](performance-results-2026-08-23.md) for
the exact values and interpretation boundaries.

## Remaining qualification work

1. Run eight balanced fresh-process triplets with power/thermal controls.
2. Capture Metal System Trace and GPU timestamps for fill-only saturation.
3. Add effects, scrolling, control-density, first-present, startup, allocation,
   settled-memory, and leak scenarios.
4. Run a systematic SamplesApp page/pixel sweep and a long-duration resize,
   DPI, occlusion, minimize/restore, and device-loss sequence.
5. Run Windows/D3D12, Linux/Vulkan, browser, trimming, and NativeAOT lanes.
