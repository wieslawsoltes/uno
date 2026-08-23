# Capability and conformance matrix

This matrix defines “complete” for the Uno ProGPU backend and records the
current implementation separately from qualification. A row is complete only
when implementation, focused tests, runtime rendering, and the applicable
pixel/metric comparison pass. Compile success alone is not completion.

Status terms: **working** means exercised on a real macOS/Metal device;
**implemented** means code is present but the full corpus has not run;
**partial** identifies a known semantic gap; **planned** is specification only.

| Area | Current status | Qualification / gap |
|---|---|---|
| context negotiation | working | SamplesApp log proves `ProGpuGraphicsProvider` won `WebGpu`; incompatible-context unit test remains |
| direct present | working | renders into borrowed `ColorView` and host presents; ownership trace remains |
| recording/replay | working | smoke replays retained `GpuPicture`; nested/resource-pressure corpus remains |
| transforms | implemented | set/concat/translate/scale; transform pixel fixtures remain |
| clips | working | rect, rounded/path, intersect/difference; representative nested rounded-ring rendering is analytic and visually qualified; broader clip corpus remains |
| layers | partial | balanced save/blend scopes and isolated blur/drop-shadow effect layers work; color-filter and arbitrary-effect isolation remain |
| primitives | working | clear, rect and rounded rect have pixel assertions; border/line corpus remains |
| paths | implemented | fill/stroke and backend geometry work; cap/join/dash behavior is supplied through widened geometry and needs corpus coverage |
| gradients | working | linear/radial, focal point, anisotropy, spread, local matrix, and duplicate-stop hard transitions are pixel-validated; broader fixtures remain |
| images | implemented | BGRA upload, sampling, opacity, color matrix/`SrcIn`, source/destination, nine-slice; corpus remains |
| offscreen | working | GPU render target and same-device reuse path exercised |
| snapshot | working | real GPU readback and byte-channel assertions pass |
| shadow | partial | retained drop-shadow visual works; anisotropic sigma and additive semantics are not complete |
| effects | partial | source blur, drop shadow, image matrix/tint and GPU-only ordered HostBackdrop capture are implemented; the live acrylic popup still differs from Skia and needs shader-cost tuning, while arbitrary neutral DAG/layer isolation remains |
| geometry factory | implemented | builders, primitives, host marker, bounds, hit test, transform, combine, trim, widen and streams; solid ellipse strokes retain exact analytic rings while join-aware general geometry uses widening; full numerical corpus remains |
| font matching | working | system default selection used by smoke/SamplesApp; byte/family/fallback corpus remains |
| shaping | working | direct OpenType shaping exercised for LTR; RTL/complex corpus remains |
| glyph output | implemented | outline, COLR/SVG layers and embedded bitmap routes exist; font corpus remains |
| direct glyph rendering | working | typed `ProGpuGlyphRunGeometry` reaches `DrawingContext.DrawGlyphRun` atlas path |
| image codec | implemented | neutral managed codec, not Skia; format/orientation corpus remains |
| SVG | implemented | neutral parser emits through ProGPU geometry/drawing; document corpus remains |
| lifetime | partial | deterministic disposal and borrowed lifetime implementation exist; host-handle release mock test remains |
| device loss | planned | generation counter exists; invalidation/recovery injection remains |
| diagnostics | implemented | CPU record/submit, compositor compile/upload/pass, draw/vertex/upload/mask/cache and retained-picture metrics are exported in benchmark schema v2; scene dumps can wait for HostBackdrop and include material parameters; atlas residency is not yet exported |
| AOT/trimming | planned | trimmed NativeAOT sample remains |
| Skia-free lane | partial | backend assembly has no Skia reference, but the current SamplesApp deliberately also packages other selectable backends |

## Backend acceptance gates

The backend can be described as working when all of these are true:

1. A minimal Uno Desktop application starts with only the ProGPU drawing stack,
   draws a representative control page, and presents on a hardware WebGPU
   adapter.
2. The runtime capability corpus reports zero unsupported operations.
3. A device/target ownership test proves no CPU presentation readback and no
   host-handle release.
4. Geometry and text metric contracts pass, including fallback and color fonts.
5. The Skia-free output/process audit passes.
6. The standard benchmark smoke run produces schema-valid raw artifacts and
   accepted final-target pixel comparisons for every compared backend.

Performance superiority is not an acceptance gate. Performance regressions are
reported honestly and investigated after correctness and workload equivalence
are established.

## Current acceptance snapshot

- Gate 1: **passed on macOS/Metal**. SamplesApp loaded its 1,413-sample catalog
  and presented through ProGPU without backend exceptions during the observed
  startup interval. The startup log, rather than the selection environment
  variable, proves that `ProGpuGraphicsProvider` won `WebGpu` negotiation.
- Gate 2: **passed only for the focused smoke and five diagnostic benchmark
  scenes**; the complete runtime capability corpus has not run.
- Gate 3: **presentation path passes by implementation and runtime observation;
  formal ownership instrumentation remains**.
- Gate 4: **partial**. The direct glyph route is exercised, but the full
  geometry/text/fallback/color-font corpus remains.
- Gate 5: **partial**. The backend project is Skia-free; a minimal packaged app
  and process-module audit remains.
- Gate 6: **passed for two full 100-sample processes per lane/scenario**.
  Cached and sparse output is byte-exact; text/path/image differences are
  quantified. Eight balanced independent process triplets remain before
  publication-level confidence.
