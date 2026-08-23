# ProGPU Uno renderer benchmark protocol

## 1. Purpose

The suite compares integration costs and rendering behavior, not merely API
call speed. Each measured boundary performs real work, submits to a persistent
GPU target, and waits for the same completion boundary. Correctness and feature
parity are mandatory for a performance result to be publishable.

## 2. Compared lanes

Primary same-framework comparison:

- Uno + Skia software drawing backend (`--backend skia`);
- Uno + Skia Metal drawing backend on macOS (`--backend skia-metal`);
- Uno + built-in WebGPU backend;
- Uno + ProGPU backend.

Context comparisons:

- ProGPU WinUI-compatible framework;
- Avalonia + ProGPU backend;
- C++ UI + ProGPU backend.

Cross-framework numbers explain integration overhead but are not presented as
renderer-only results. Each lane must consume the same semantic workload and
produce the same final state.

## 3. Standard workloads

| ID | Workload | Primary question |
|---|---|---|
| startup-cold | process launch to first correct present | initialization, shader/pipeline/font discovery cost |
| frame-first | first complete representative frame | cold compilation and upload cost |
| frame-cached | unchanged retained scene | replay/submission floor |
| mutate-sparse | one opaque fill changes | damage and incremental compilation cost |
| text-hit | stable 128-run mixed-script/color-font scene | glyph/layout/atlas reuse |
| text-miss | 127 runs change outside timed mutation boundary | shaping and first-use glyph cost |
| paths | 1,000 mixed Bézier paths with stable geometry | tessellation/cache behavior |
| strokes | 1,000 analytic arc/Bézier strokes across solid/dashed cap and join styles | native stroke compilation, quality, and retained replay |
| materials | 768 linear/radial gradients spanning focal, anisotropic, spread, local-matrix, and duplicate-stop cases | material batching and shader fidelity |
| layers | one retained color-matrix layer containing 1,536 overlapping opaque/translucent primitives and a non-identity alpha row | subtree isolation, effect-texture reuse, and whole-layer shader cost |
| isolation-layers | one unfiltered source-over layer containing a clipped transparent clear and 1,536 overlapping opaque primitives over gray | destination isolation, clear containment, restoration, and retained effect-surface reuse |
| mask-layers | 768 colored source cells and 768 smaller rounded masks inside nested source-over/DstIn layers | transparent-source destination coverage, composition-mask fidelity, and nested effect-surface reuse |
| blend-layers | one retained Multiply layer containing 1,536 opaque overlapping primitives over a gray destination | once-per-layer composition versus incorrect per-primitive blending |
| blend-corpus | all 27 Uno layer blend modes, each with an opaque destination and two overlapping translucent rounded sources | Porter-Duff, separable, and non-separable mode fidelity plus retained multi-layer throughput |
| images | retained image grid plus one changed upload | texture residency/upload cost |
| shadows | 128 anisotropic path shadows alternating horizontal/vertical sigma and source-over/additive composition | independent-axis filtering, retained shadow reuse, and additive blend fidelity |
| effects | shadows, blur, color matrix, backdrop | pass graph and bandwidth cost |
| controls-1000 | continuous 1,000-control Uno sample | end-to-end framework throughput |
| scroll | virtualized list with deterministic scroll trace | mutation, layout, and render interaction |
| memory-settled | repeated stable workload after GC/cache settling | managed/native/GPU resident footprint |

The renderer scorecard baseline is 1280×720 logical pixels and includes 768
opaque fills, 128 text runs, one color-font run, paths, images, clips, layers,
and effects. Counts and random seeds are versioned in the artifact schema.

## 4. Timing boundary

For steady-frame scenarios the harness records three boundaries rather than
collapsing dissimilar work into one number:

1. CPU frame submit begins immediately before `BeginPresent` and ends when the
   present session has recorded and submitted the frame.
2. GPU completion wait begins after CPU submit and ends after
   provider-confirmed completion.
3. Total blocking frame is the sum of those boundaries.

Scene mutation and requested final state are established outside all renderer
timed boundaries. Pixel readback and artifact encoding happen after timing. A
separate bounded-batch lane submits 60 frames and waits once; it reports CPU
submit per frame, GPU completion per batch, and total throughput per frame.
This lane intentionally measures queue saturation and is not normal
swap-chain/v-sync pacing.

Persistent devices, pipelines, and targets are used for warm scenarios. Cold
scenarios use fresh processes. At least four full application warmups are
untimed before warm sampling.

## 5. Pairing and order

Comparison runs alternate fresh-process order:

```text
Skia / Uno WebGPU / ProGPU
ProGPU / Uno WebGPU / Skia
```

The macOS GPU-to-GPU qualification alternates `skia-metal / progpu` and
`progpu / skia-metal`. Both lanes use retained 1280x720 BGRA8 Metal textures.
Skia receives Uno's real `IMetalDeviceContext` and creates its normal
`GRContext` Metal renderer; ProGPU uses wgpu-native's Metal backend. After
each measured submission, the harness places an empty command buffer on the
same Metal command queue and waits for it. This fence is ordered after Skia's
flush, so its completion boundary cannot be mistaken for CPU-only submission.

Context-framework comparisons use the same balanced forward/reverse ordering.
No backend may reuse a previously presented image without issuing and
completing the measured redraw.

Default publication gate:

- 8 or more independent process pairs;
- 100 or more measured frames per steady scenario per process;
- no thermal throttling or unrelated sustained CPU/GPU load;
- identical adapter, power source/mode, display scale, target size, and build;
- raw samples retained, not only aggregates.

## 6. Reported statistics

For each process and aggregated comparison report:

- median, p95, p99, maximum, MAD, and missed-frame count;
- ratio of renderer medians;
- same-index paired median ratios and absolute differences;
- deterministic bootstrap 95% interval for the paired median;
- CPU utilization and allocation rate;
- settled RSS/private bytes, managed heap, and GPU allocation;
- draw/dispatch/pass counts, command bytes, upload bytes, atlas residency,
  pipeline/cache hits and misses;
- cold-start and first-present times separately.

Averages may be included for continuity with existing Uno samples but are not
the primary statistic.

## 7. Correctness gate

Every measured process captures the exact final state. The harness records:

- unsupported operation count (must be zero for a publishable scene);
- output dimensions, format, scale, and semantic-state hash;
- exact-different pixels and different-pixel fraction;
- per-channel MAE, maximum error, and PSNR;
- optional masked regions only when the nondeterminism is documented before
  the run.

Non-text vector/image regions target byte-identical output when color-space and
sampling rules match. Text comparisons report raster differences separately
while requiring metric, cluster, fallback-family, and caret contracts to match.
A faster incomplete or stale frame fails.

## 8. Environment manifest

Every artifact directory contains a machine-readable manifest with:

```text
Uno commit and dirty state
ProGPU gitlink commit and dirty state
.NET SDK/runtime and workload versions
OS/build and architecture
CPU, RAM, GPU, driver/API/backend
display scale, target size, refresh rate
power source/mode and thermal notes
build configuration and AOT/JIT mode
exact command line and environment variables
tool versions and timestamps
```

## 9. Profiling workflow

1. Use counters/traces to classify CPU, allocation/GC, lock, I/O, GPU, or
   presentation ownership.
2. Capture CPU samples with exact symbols and preserve the trace.
3. Use allocation/VM tools for retained-memory questions.
4. Use GPU tooling for pass count, occupancy, bandwidth, synchronization, and
   CPU/GPU overlap. On macOS this includes Metal System Trace.
5. Change one variable at a time and rerun the identical workload.
6. Repeat enough times to distinguish a real effect from noise.

## 10. Result language

Reports explicitly separate:

- code-review assessment;
- compile validation;
- runtime correctness validation;
- performance evidence.

Contended or thermally unstable runs are diagnostic evidence only. Absolute
latency and memory claims require an idle-machine publication run. Raw artifacts
remain the authority if a summary or interpretation changes.

## 11. Current harness and artifacts

`src/Uno.UI.Composition.Backend.Benchmarks` implements fifteen steady
micro-scenarios (`cached`, `sparse`, `text`, `paths`, `strokes`, `materials`,
`layers`, `isolation-layers`, `mask-layers`, `blend-layers`, `blend-corpus`,
`images`, `clips`, `shadows`, and `effects`) for ProGPU, Uno WebGPU where
supported, Uno software Skia, and Uno Skia/Metal on macOS. Every frame
explicitly clears the target; this prevents translucent antialiasing, paths,
images, and effects from accumulating across samples. WebGPU lanes use
`wgpuDevicePoll(wait=true)` only in the separate completion boundary. The
Skia/Metal lane uses a command-buffer fence on Skia's own queue at the same
boundary and reads pixels directly from its retained Metal texture.

The `strokes` workload repeats four immutable centerlines: rotated elliptical
arcs and mixed quadratic/cubic paths, with solid and dashed styles covering
round, square, triangle, and butt caps plus round, bevel, miter, and
miter-or-bevel joins. It therefore catches both a flattened-curve quality
regression and a backend that reports a fast result by omitting authored pen
semantics.

The `materials` workload layers 768 independently transformed gradient cells
over the retained base grid. Four linear and four radial definitions cover
clamp, repeat, and mirror spread; focal and anisotropic radial geometry; local
matrix rotation; translucent stops; and an exact duplicate-stop hard edge.
Stable semantic and pixel hashes prevent a fast result from dropping a shader
variant or material transition.

The `layers` workload records 1,536 overlapping rounded and rectangular
primitives inside one color-matrix `SaveLayer`. Its matrix mixes RGB channels
and scales the already-composited alpha, making whole-subtree isolation
observable: applying the matrix independently to each primitive does not
produce the same overlap pixels. Stable replays additionally exercise retained
effect-surface reuse without permitting final-target reuse in forced mode.

The `isolation-layers` workload starts with an opaque gray destination, opens
an unfiltered `SaveLayer`, clips it to the target, clears the isolated source to
transparent, and records 1,536 overlapping opaque primitives. Correct output
preserves gray in every undrawn gap when the layer is restored. Treating
`SaveLayer()` as a state-only save instead clears those gaps on the destination
and fails both the focused pixel assertion and the final-target readback gate.

The `mask-layers` workload mirrors Uno's composition-mask sequence: an outer
source-over layer is clipped and cleared, 768 colored source cells are
recorded, and a nested DstIn layer supplies 768 smaller rounded alpha masks.
Correct output removes every source pixel outside those masks. Restricting the
final DstIn draw to mask content bounds leaves rectangular source cells behind
and fails the focused pixel and final-target readback gates.

The `blend-layers` workload records 1,536 opaque overlapping primitives inside
one Multiply `SaveLayer` over an opaque gray destination. Correct output
requires ordinary source-over composition inside the isolated surface followed
by exactly one Multiply operation against the destination. Applying Multiply
to each primitive separately produces black overlap pixels and fails the
readback gate even if timing and unsupported-operation counts look healthy.

The `blend-corpus` workload places each of Uno's 27 layer blend modes in its
own clipped isolation tile. Every tile starts with an opaque destination and
composites two overlapping translucent rounded sources once at layer restore.
The final readback therefore catches missing mode mappings, wrong
premultiplication, destination-sampling mistakes, cross-tile contamination,
and state-restoration failures in one deterministic scene.

The sparse workload is a retained 24-row tree containing 768 rectangles. One
row changes per frame. This models immutable subtrees without turning every
single analytic rectangle into a cache entry. ProGPU's cache admission rejects
tiny pictures that are cheaper to compile directly.

The clips workload draws 240 independently transformed rounded rectangles,
each with one contained rectangular difference hole. It qualifies analytic
even-odd mask composition and rejects a fast result if any unsupported
operation is recorded. ProGPU diagnostics also expose offscreen mask-pass,
mask-draw, and peak mask-texture demand for this scenario.

The shadows workload draws 128 retained path silhouettes over the common grid.
It alternates `(sigmaX, sigmaY)` between `(6, 2)` and `(2, 6)`, uses additive
composition for one quarter of the shadows, and replays every unblurred source.
The semantic and pixel artifacts therefore reject scalar-sigma substitution,
missing additive blend state, double-applied translucent color, or omitted
source content.

The effects workload draws 12 cards. Each card captures a clipped host
backdrop, applies blur and translucent material composition, renders an offset
shadow-only effect layer, and explicitly replays its source. This matches the
framework's non-analytic shadow fallback contract and catches missing content
bounds, stale-origin placement, source duplication, or stale effect resources.
Because the reference Skia path is CPU-intensive, its qualification shape uses
40 blocking frames plus three batches of 20 frames: 100 measured frames per
fresh process.

The v3 result contract is
[benchmark-result.schema.json](benchmark-result.schema.json). It includes raw
timing samples, stage-separated ProGPU metrics, retained-picture counters, a
forced-redraw state, retained-target-reuse samples, a semantic-state hash, and
a BGRA readback hash/path. Current results are in
[performance-results-2026-08-23.md](performance-results-2026-08-23.md).
