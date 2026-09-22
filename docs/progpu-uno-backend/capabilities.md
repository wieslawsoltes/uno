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
| context negotiation | working | SamplesApp log proves `ProGpuGraphicsProvider` won `WebGpu`; runtime contract asserts exactly one preferred context and only `IGraphicsProvider<IWebGpuDeviceContext>` |
| direct present | working | renders into borrowed `ColorView`; a real-device ownership test disposes a factory and then creates another native texture from the still-live host device |
| recording/replay | working | smoke replays retained `GpuPicture`; nested/resource-pressure corpus remains |
| transforms | working | set/concat/translate/scale, transformed retained records, clips, strokes, and HostBackdrop placement have pixel fixtures |
| clips | working | rect, rounded/path, intersect/difference; nested rounded rings and rounded-plus-rectangular holes are analytic, pixel-qualified, and benchmarked with zero mask passes; broader arbitrary clip corpus remains |
| layers | partial | balanced save scopes plus translated and nested blur/drop-shadow/color-matrix effects work; unfiltered, color-matrix, and blend-mode layers isolate the complete subtree on one retained GPU surface and preserve nested scope restoration; clipped transparent clear, DstIn composition masks, empty masks, all 27 Uno layer blend modes with translucent overlap, Multiply overlap, and live Screen mutation are pixel-qualified, while alpha-zero/low-alpha blend edges and arbitrary effects remain |
| primitives | working | clear, solid/gradient rect, uniform/non-uniform rounded rect, border, and line routes have direct or effect-bound pixel assertions; broader antialias corpus remains |
| paths | working | fill/stroke, transformed replay, effect bounds, and backend geometry have runtime assertions; eligible solid and dashed strokes preserve native analytic lines/Béziers/arcs with distinct cap/join styles, while trim, clips, hit testing, and foreign consumers retain widened-fill fallback semantics; broader numerical corpus remains |
| gradients | working | linear/radial, focal point, anisotropy, spread, local matrix, and duplicate-stop hard transitions are pixel-validated; broader fixtures remain |
| images | working | BGRA upload, sampling, opacity, color matrix/`SrcIn`, source/destination, and nine-slice are benchmarked or real-device smoke-tested, including effect-bound propagation; codec corpus remains |
| offscreen | working | GPU render target and same-device reuse path exercised |
| snapshot | working | real GPU readback and byte-channel assertions pass |
| shadow | working | translated retained shadows, shadow-only composition, explicit source replay, nested output-bound propagation, independent X/Y sigma including a zero axis, additive composition, and translucent mask color are real-device runtime-qualified; a broader randomized corpus remains |
| effects | partial | source blur, drop shadow, image matrix/tint, isolated subtree blend, and GPU-only ordered HostBackdrop capture are implemented; translated/nested layer bounds, detached texture retirement, the 27-mode layer corpus, Multiply/Screen blend state, and live acrylic placement are qualified, while arbitrary neutral DAG and blend edge-case conformance remain |
| geometry factory | working | builders, primitives, host marker, bounds, hit test, transform, combine, trim, widen and streams are real-device exercised; ellipse clips retain exact rings and direct strokes defer to ProGPU's analytic pen stack; full numerical corpus remains |
| font matching | working | system default selection used by smoke/SamplesApp; byte/family/fallback corpus remains |
| shaping | working | direct OpenType shaping exercised for LTR; RTL/complex corpus remains |
| glyph output | implemented | outline, COLR/SVG layers and embedded bitmap routes exist; font corpus remains |
| direct glyph rendering | working | typed `ProGpuGlyphRunGeometry` reaches `DrawingContext.DrawGlyphRun` atlas path |
| image codec | implemented | neutral managed codec, not Skia; format/orientation corpus remains |
| SVG | implemented | neutral parser emits through ProGPU geometry/drawing; document corpus remains |
| lifetime | working | deterministic resource disposal is exercised; a real borrowed-device test proves disposing the backend does not release the host WebGPU device, and detached effect textures are retired before later scene-cache admission |
| managed/native optimization parity | working | dependency acceptance requires an explicit C# and C++ applicability audit; the current lifetime fix pairs the managed Silk-native synchronization domain with a recursive non-Dawn C++ process scope, while documenting why the managed bind-group dictionary change has no native analogue |
| device loss | planned | generation counter exists; invalidation/recovery injection remains |
| diagnostics | implemented | CPU record/submit, compositor compile/upload/pass, draw/vertex/upload/mask/cache, retained-picture, retained-target-reuse, and forced-redraw metrics are exported in benchmark schema v3; scene dumps can wait for HostBackdrop and include material parameters; atlas residency is not yet exported |
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
- Gate 2: **passed for the expanded real-device smoke and fifteen benchmark
  scenes**; the systematic SamplesApp page corpus has not run.
- Gate 3: **passed on macOS/Metal**. The runtime test disposes a factory that
  borrowed the host context, then successfully allocates and releases another
  native WebGPU texture from that same device.
- Gate 4: **partial**. The direct glyph route is exercised, but the full
  geometry/text/fallback/color-font corpus remains.
- Gate 5: **partial**. The backend project is Skia-free; a minimal packaged app
  and process-module audit remains.
- Gate 6: **passed for eight alternating ProGPU/Skia processes across the seven
  primary scenarios, plus three fresh forced-redraw pairs for the native-stroke,
  materials, color-matrix-layer, unfiltered-isolation-layer, destination-in
  mask-layer, single-mode layer, all-mode blend-corpus, and anisotropic/additive
  shadow scenarios**. Cached and sparse output is
  byte-exact; text/path/stroke/material/layer/image/clip and effect differences
  are quantified. The earlier Uno WebGPU context lane has two processes and
  still needs eight-process promotion.
