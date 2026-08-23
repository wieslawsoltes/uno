# Correctness-gated renderer results — 2026-08-23

## Scope

The primary result is an eight-pair, alternating fresh-process comparison of
the Uno ProGPU and software-Skia drawing backends. It covers the seven primary
qualification scenarios; later sections add retained-output, native-stroke,
gradient-material, color-matrix-layer, unfiltered-isolation-layer, and
blend-mode-layer follow-ups. Every process renders the same semantic state,
reports zero unsupported operations, reads back the final target, and preserves
its raw timing distribution.

- Machine: Apple M3 Pro, 11 logical processors, 18 GB RAM, AC power.
- OS/runtime: macOS 26.6 arm64, .NET 10.0.5, Release.
- Target: 1280×720, premultiplied BGRA8.
- ProGPU: wgpu-native on Metal, one sample per pixel.
- Uno branch baseline: `6848a67e49`; ProGPU merged `main` gitlink:
  `d5d3e977527b25897387345122d7b5688803a69c`.
- `cached` through `clips`: 8 warmups, 100 blocking samples, and 9 batches of
  60 frames in each of eight processes per backend.
- `effects`: 4 warmups, 40 blocking samples, and 3 batches of 20 frames in each
  of eight processes per backend. This is 100 measured frames per process.
- Record construction, mutation, and final readback remain outside the timing
  regions.

An earlier two-process matrix also includes Uno's built-in WebGPU backend. It
remains useful integration context, but the tables below use only the stronger
eight-process ProGPU/Skia evidence.

## Bounded-batch throughput

Median milliseconds per frame, aggregated as the median of the eight process
medians. Each batch submits multiple frames before one completion wait. The
paired speedup is the median of eight same-index Skia/ProGPU ratios. The
conservative speedup divides the lowest Skia process median by the highest
ProGPU process median.

| Scenario | ProGPU | Skia | paired speedup | conservative speedup |
|---|---:|---:|---:|---:|
| cached, 768 fills | 0.156942 | 0.364693 | 2.33× | 2.14× |
| sparse retained-row mutation | 0.263191 | 0.362946 | 1.38× | 1.35× |
| 128 text runs | 0.170561 | 4.869669 | 28.59× | 24.48× |
| 1,000 paths | 0.259217 | 2.377015 | 9.18× | 8.94× |
| 240 images | 0.279068 | 1.068957 | 3.83× | 3.72× |
| 240 rounded clip holes | 0.279780 | 2.769357 | 9.89× | 7.71× |
| 12 backdrop/shadow cards | 2.489785 | 252.877792 | 101.59× | 98.40× |

ProGPU is faster in every individual process of every scenario. `sparse` is
the narrowest throughput margin. `effects` is the largest because Skia's
reference path repeatedly evaluates CPU backdrop blur and shadow filters while
ProGPU records GPU work and reuses the retained scene.

The effects paired-median bootstrap 95% interval is 98.95×–108.46× using a
deterministic 20,000-resample bootstrap over the eight paired ratios. This is a
distribution estimate for this machine and workload, not a cross-device
guarantee.

## CPU submission boundary

Median milliseconds per frame spent from `BeginPresent` through command
submission in the bounded batches. GPU completion is excluded.

| Scenario | ProGPU | Skia | paired speedup |
|---|---:|---:|---:|
| cached | 0.123570 | 0.364693 | 2.93× |
| sparse | 0.197587 | 0.362946 | 1.83× |
| text | 0.132607 | 4.869618 | 36.82× |
| paths | 0.159875 | 2.377013 | 14.86× |
| images | 0.219480 | 1.068953 | 4.93× |
| clips | 0.216220 | 2.769354 | 12.78× |
| effects | 2.234338 | 252.877785 | 113.31× |

The ProGPU effects CPU-submit bootstrap 95% interval is 110.30×–127.57×. The
retained effects scene reports 49 draws, 3,216 vector vertices, zero scene
uploads after warmup, zero mask passes, 24 retained-picture hits, and zero
retained-picture compilations per measured frame.

## Blocking CPU-frame boundary

The median of each process's per-frame CPU work is shown below. This is not the
same as ProGPU's total blocking duration, which includes quantized WebGPU device
polling.

| Scenario | ProGPU | Skia | paired speedup |
|---|---:|---:|---:|
| cached | 0.046600 | 0.359100 | 7.69× |
| sparse | 0.163450 | 0.359850 | 2.19× |
| text | 0.053150 | 4.532550 | 85.54× |
| paths | 0.054850 | 2.356100 | 43.18× |
| images | 0.253600 | 1.056100 | 4.17× |
| clips | 0.279650 | 2.759700 | 9.89× |
| effects | 1.011700 | 252.747050 | 249.93× |

The native `wgpuDevicePoll(wait=true)` boundary is quantized for small frames
and must not be called compositor CPU time. GPU timestamps or a Metal System
Trace are required to split queue execution from polling and wakeup latency.

## Pixel gate

RGB differences use the Skia readback as the reference. `px>8` and `px>32`
count pixels whose largest RGB-channel error exceeds the threshold.

| Scenario | RGB MAE | Max | px>8 | px>32 | PSNR |
|---|---:|---:|---:|---:|---:|
| cached | 0.000000 | 0 | 0% | 0% | ∞ |
| sparse | 0.000000 | 0 | 0% | 0% | ∞ |
| text | 0.708929 | 81 | 3.6036% | 0.4512% | 36.435 dB |
| paths | 0.181166 | 22 | 0.8025% | 0% | 47.334 dB |
| images | 0.041453 | 1 | 0% | 0% | 61.955 dB |
| clips | 0.424195 | 76 | 2.3698% | 0.1062% | 41.919 dB |
| effects | 3.411024 | 61 | 19.4972% | 0.5625% | 32.049 dB |

Cached and sparse output is byte-identical. Image output differs only by one
quantization level. Text, path, and clip differences are concentrated at
antialiased edges. Effects are semantically matched—backdrop, translucent card,
shadow-only filter, and explicit source replay are all present—but ProGPU's
fixed-tap single-pass backdrop kernel does not reproduce Skia's Gaussian kernel
exactly. Visual inspection confirms matching placement, ordering, opacity, and
shadow direction; the remaining difference is blur footprint.

The backdrop mapping was calibrated with identical 1.8×, 2.0×, 2.2×, and
3.344× sigma sweeps. The selected 2.0× mapping produced the lowest error:

| Mapping | RGB MAE | px>8 | px>32 |
|---|---:|---:|---:|
| 1.8× | 3.824211 | 23.3465% | 0.5926% |
| **2.0×** | **3.411024** | **19.4972%** | **0.5625%** |
| 2.2× | 3.744300 | 22.7428% | 0.5617% |
| 3.344× | 3.930181 | 26.4106% | 0.6832% |

The 2.0× result also improves over the earlier source-correct effects capture,
which measured 3.744 RGB MAE and 20.782% of pixels above eight levels.

## Optimization evidence

The current result incorporates these root-cause changes:

1. Exact rounded border differences remain analytic instead of entering the
   general geometry-mask path.
2. A rounded intersect followed by a contained rectangular difference is
   encoded as one even-odd analytic mask. The clips workload therefore uses
   zero offscreen mask passes or mask textures.
3. Retained-picture eligibility is cached with the picture instead of rescanned
   on every scene compile.
4. Immutable nested pictures reuse bounded compiled pages when a parent record
   changes; tiny one-shot pictures remain below the admission threshold.
5. Leading replacement clears become render-attachment clears while nested
   replacement ordering retains an explicit draw fallback.
6. Detached effect textures are swept before retained-scene cache admission, so
   an earlier effect frame cannot disable caching of a later effect-free scene.
7. Explicit translated effect bounds are preserved when a cached effect texture
   is composited back.
8. Drop shadow supports a shadow-only mode while retaining source-plus-shadow as
   the public default. The Uno adapter uses shadow-only mode and then follows
   Uno's explicit source replay contract.
9. Backdrop placement carries the active transform, and the adapter supplies
   conservative transformed content bounds for effect layers.
10. Cleanup uses a bounded submission window rather than serializing every
    eight-frame burst; per-frame completion polling remains non-blocking.
11. Retained pages compare their compact draw-call projection before expanding
    the full compositor draw-call structure, and compatible accumulated calls
    are mutated in place instead of copied on every page boundary.

## Sparse retained-update follow-up

The narrowest workload was profiled again after the primary qualification.
The baseline used the `d5d3e977` gitlink in four fresh alternating process
pairs. The candidate used merged ProGPU `main` commit `f51cad0f` in eight fresh
pairs with the same 8 warmups, 100 blocking samples, and nine 60-frame batches.

| Sparse metric | baseline | `f51cad0f` | change |
|---|---:|---:|---:|
| ProGPU CPU submit | 0.204890 ms | 0.191991 ms | -6.3% |
| ProGPU scene compile | 0.087950 ms | 0.083850 ms | -4.7% |
| ProGPU bounded total | 0.260555 ms | 0.260083 ms | -0.2% |
| Skia bounded total | 0.364835 ms | 0.362112 ms | measurement control |

The full-frame total remains dominated by completion timing at this scale, but
the CPU boundary improves materially. ProGPU is 1.39× faster than Skia by the
process-median bounded total; the slowest paired result is 1.37×. A prolonged
sampled-thread trace reduced `AppendIncrementalScenePage` from 4.09% to 0.22%
of sampled process time. The optimized path avoids expanding 23 already
mergeable row calls and avoids copying the large accumulated draw-call value.

The post-change confirmation matrix used four fresh pairs for `cached`,
`text`, `paths`, `images`, and `clips`, eight for `sparse`, and two for the
slower `effects` workload:

| Scenario | ProGPU | Skia | speedup |
|---|---:|---:|---:|
| cached | 0.157293 ms | 0.363549 ms | 2.31× |
| sparse | 0.260083 ms | 0.362112 ms | 1.39× |
| text | 0.163591 ms | 4.520163 ms | 27.63× |
| paths | 0.267791 ms | 2.359047 ms | 8.81× |
| images | 0.275967 ms | 1.063436 ms | 3.85× |
| clips | 0.288429 ms | 2.736351 ms | 9.49× |
| effects | 2.739260 ms | 251.099025 ms | 91.66× |

Every ProGPU process reports zero unsupported operations. All seven ProGPU
pixel hashes match the already qualified renderer output. Sparse remains
byte-identical to Skia, and the representative effects comparison retains the
same 3.411024 RGB MAE and fixed-tap-versus-Gaussian limitation documented
above. Raw local measurements, traces, and rendered inspection images are in
`src/artifacts/performance/2026-08-23-sparse-update/`.

## Retained-presentation and explicit-drain follow-up

A prolonged 80,000-frame cached trace against `f51cad0f` showed that the
framework creates a short-lived identity wrapper around the same retained
picture on every presentation. Rebuilding that wrapper also changed the visual
object identity, even though its immutable child command storage was unchanged.
The same trace confirmed that a host-requested blocking completion did not
advance ProGPU's deferred-submission accounting, permitting a later cleanup to
repeat an already satisfied wait.

ProGPU PR #130 flattens an identity-only picture recording to an independently
leased clone of the child storage and records successful explicit waits as
drained submissions. The host adapter recognizes shared command storage as an
unchanged visual and routes the benchmark completion boundary through the
factory so the wait and ProGPU accounting remain coherent.

The before binary contains `f51cad0f`; the candidate binary contains
`8e1c39b8`, which merged unchanged as `77a28482`. Eight alternating process
pairs used 24 warmups, 300 blocking samples, 60 frames per batch, and 15
batches. Ratios are candidate divided by baseline; values below one are faster.

| Cached metric | paired median ratio | change |
|---|---:|---:|
| blocking total | 0.977537 | -2.25% |
| CPU frame | 0.740217 | -25.98% |
| GPU completion wait | 0.997377 | -0.26% |
| batched CPU/frame | 0.379376 | -62.06% |
| completed batched frame | 0.875304 | -12.47% |
| ProGPU record | 0.528571 | -47.14% |
| ProGPU submit | 0.805877 | -19.41% |
| ProGPU scene compile | 0.629167 | -37.08% |

CPU frame improved in all eight pairs and completed batched throughput improved
in all eight. Blocking total improved in six; the two small regressions track
native completion-wait noise rather than CPU work. Every pair produced
`15DECAB2395CA1F302CBBA88BE4B7B55AE751D4294A241C5B7E1001F3D73F718`
and zero unsupported operations.

The final cached ProGPU/Skia comparison used a separate eight-pair alternating
run with the same shape:

| Boundary | paired-median result |
|---|---:|
| ProGPU CPU frame versus Skia | 7.07× faster |
| ProGPU batched CPU/frame versus Skia | 14.17× faster |
| ProGPU completed batched frame versus Skia | 2.82× faster |
| ProGPU blocking total versus Skia | 4.40× slower |

The blocking result is intentionally not described as a renderer regression:
the ProGPU CPU frame is about 0.04–0.06 ms, while each forced Metal/WebGPU drain
is about 1.52 ms. Software Skia completes the tiny scene synchronously in about
0.36 ms. Production presentation permits work in flight, so bounded-batch
completion is the closest sustained-throughput boundary; the forced drain is
retained as a latency and synchronization diagnostic.

The final comparison matrix keeps ProGPU completed-batch throughput ahead of
Skia in all seven workloads: approximately 2.82× cached, 1.92× sparse, 29.50×
text, 10.01× paths, 4.12× images, 11.72× clips, and 114.21× effects. The exact
ProGPU hashes for cached, sparse, text, paths, images, clips, and effects equal
the previously inspected qualification artifacts. Cached and sparse remain
byte-identical to Skia; the documented text, path, image, clip-edge, and
fixed-tap effect raster differences remain unchanged.

Raw paired JSON, BGRA readbacks, stdout, the baseline sampled-thread trace, and
the isolated before/candidate binaries are under
`src/artifacts/performance/2026-08-23-cached-floor/`. A rejected command-buffer
label-removal experiment is retained there as negative evidence: it preserved
pixels but regressed both cached CPU and completed batch time, so it was not
landed.

## Retained target and queue-completion follow-up

The next trace separated two costs that the earlier retained-picture work did
not remove:

1. an unchanged retained scene was still submitted into the same populated
   target; and
2. the host's explicit completion boundary used a blocking device poll whose
   approximately 1.52 ms floor dominated tiny frames.

ProGPU `main` commit `63561c7e` supplies retained output stamps and texture
content generations. The host adapter now reuses a target only when picture
storage, transform, target object and view, dimensions, clear color, and
texture-content generation all match. Host-backdrop frames and scene dumps
remain ineligible. Focused runtime fixtures invalidate reuse after content,
alpha-mode, clear-color, view-wrapper, and target changes.

The borrowed WebGPU lifetime also requests queue completion through an
`AllowSpontaneous` callback while continuing non-blocking device progress.
This preserves the explicit synchronization contract without imposing the old
blocking-poll quantum on every already-completed queue. An attempted
`wgpuInstanceWaitAny` route was rejected after wgpu-native aborted; it was not
landed.

For unchanged retained scenarios, target reuse reduces cached, clips, images,
paths, and text presentation to approximately 0.0011–0.0012 ms on this machine.
Effects remains ineligible because it captures host content. A forced-redraw
matrix alternates two wrapper identities around the same native target view so
the renderer cannot reuse the populated output:

| Scenario | ProGPU total | Skia total | total speedup | ProGPU CPU | Skia CPU | CPU speedup | completed-batch speedup |
|---|---:|---:|---:|---:|---:|---:|---:|
| cached | 0.41650 ms | 0.34385 ms | 0.83× | 0.0196 ms | 0.34385 ms | 17.54× | 5.24× |
| sparse | 0.53300 ms | 0.34540 ms | 0.65× | 0.0852 ms | 0.34525 ms | 4.05× | 2.87× |
| text | 0.41270 ms | 4.39835 ms | 10.66× | 0.0211 ms | 4.39835 ms | 208.44× | 54.34× |
| paths | 0.50700 ms | 2.29905 ms | 4.53× | 0.0209 ms | 2.29905 ms | 110.27× | 20.24× |
| images | 0.75800 ms | 1.02795 ms | 1.36× | 0.1621 ms | 1.02795 ms | 6.34× | 5.46× |
| clips | 0.86115 ms | 2.65155 ms | 3.08× | 0.1855 ms | 2.65155 ms | 14.30× | 12.17× |
| effects | 2.35135 ms | 244.66510 ms | 104.05× | 0.8045 ms | 244.66510 ms | 304.12× | 234.37× |

All forced-redraw semantic hashes match their Skia pair and every ProGPU run
reports zero unsupported operations. Cached and sparse remain the two honest
blocking-total gaps: their GPU queue/fence floor is larger than completing the
tiny software-Skia frame synchronously. Both are nevertheless faster at the
renderer CPU boundary and completed-batch throughput. These residual latency
gaps remain open rather than being hidden by target reuse.

Raw results and readbacks are under
`src/artifacts/performance/2026-08-23-retained-target-reuse-queue-future/`.

Three follow-up queue-loop experiments were rejected. Removing device polling
stalled because this wgpu-native configuration does not make spontaneous
callbacks progress independently. Increasing the spin interval from 64 to 256
cycles completed correctly but regressed cached and sparse blocking medians by
about 7–9%. Reducing it to 16 cycles was neutral for cached, regressed sparse,
and increased CPU work. The 64-cycle loop therefore remains the measured safe
point; no timing-only shortcut weakened explicit completion semantics.

## Native analytic stroke follow-up

A real SamplesApp comparison exposed a quality and performance defect that the
synthetic fill-path workload did not cover. The geometry API correctly
preserved arcs, but `GetStrokeFillGeometry` flattened the centerline in
22.5-degree steps before a later solid `DrawPath`. Circular, elliptical, and
rotated arcs therefore reached ProGPU as faceted fill polygons and bypassed
its analytic arc/curve stroke compiler.

The backend now returns a deferred stroke geometry for finite, untrimmed,
non-boolean paths. A direct solid draw maps Uno's complete `StrokeStyle` to a
native ProGPU pen. Filled-region consumers—bounds, hit testing, transforms,
combines, clips, streams, trims, or foreign backends—still receive the lazy
widened fallback, so the public geometry contract is unchanged.

The benchmark suite adds `strokes`: 1,000 repeated rotated-arc and mixed
Bézier strokes across four solid/dashed cap/join styles. Three fresh paired
forced-redraw processes used six warmups, 200 blocking samples, and seven
60-frame batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.684100 ms | 10.227800 ms | 14.95× | 14.62×–15.25× |
| CPU frame | 0.022800 ms | 10.227800 ms | 448.59× | 430.14×–498.92× |
| completed batch/frame | 0.396895 ms | 10.199105 ms | 25.70× | 25.62×–25.93× |
| batched CPU/frame | 0.038555 ms | 10.199103 ms | 264.53× | 256.26×–305.48× |

Each backend produced one stable pixel hash across all three processes, both
reported the same semantic hash, and ProGPU reported zero unsupported
operations and zero mask passes. The raw benchmark side-by-side measures
38.07 dB PSNR and 0.9928 SSIM; differences are confined to antialiased stroke
edges.

The real `Geometry_PathMarkup_Showcase` page was then launched once through
ProGPU/WebGPU and once through Skia/Metal from the same Release build. The
previous ProGPU circular arc had a 37-pixel flat top in its capture. The
corrected ProGPU capture has a 16-pixel top, exactly matching the 16-pixel Skia
reference, while circular, elliptical, and rotated arcs all remain smooth.
The content contact sheet confirms matching path topology and layout. Raw JSON,
BGRA readbacks, and benchmark images are under
`src/artifacts/performance/2026-08-23-native-strokes/`; live gallery captures
are under `src/artifacts/performance/2026-08-23-samplesapp-visual-parity/`.

## Gradient material follow-up

The next coverage expansion adds `materials`: the retained base grid plus 768
independently transformed cells cycling through four linear and four radial
gradients. The set covers clamp/repeat/mirror spread, focal and anisotropic
radial geometry, local rotation, translucent stops, and an exact duplicate-stop
hard transition.

Three fresh paired forced-redraw processes used six warmups, 200 blocking
samples, and seven 60-frame batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.538800 ms | 3.599500 ms | 6.68× | 6.63×–6.82× |
| CPU frame | 0.019700 ms | 3.599500 ms | 182.72× | 175.69×–187.13× |
| completed batch/frame | 0.217948 ms | 3.558363 ms | 16.33× | 16.05×–16.68× |
| batched CPU/frame | 0.034400 ms | 3.558363 ms | 103.44× | 102.24×–114.06× |

ProGPU batches the base grid and all gradient variants into two draws with
6,144 vertices, zero mask passes, and a retained-scene hit after warmup. Each
backend produced one stable pixel hash across all three processes, both report
the same semantic hash, and unsupported operations remain zero. The inspected
contact sheet preserves every gradient family and hard stop; the raw comparison
measures 29.32 dB PSNR and 0.9670 SSIM, with differences distributed through
gradient interpolation rather than missing geometry or variants. Raw JSON,
BGRA readbacks, and the contact sheet are under
`src/artifacts/performance/2026-08-23-materials/`.

Short built-in WebGPU integration smokes also complete with zero unsupported
operations: 8.6350 ms blocking median for `materials` and 17.1597 ms for
`strokes`, versus the ProGPU process medians above of 0.5388 and 0.6841 ms.
These 40-sample smokes prove scenario compatibility but are not substituted for
the balanced ProGPU/Skia qualification distributions.

## Isolated color-matrix layer follow-up

ProGPU `main` merge commit `ecc9787b` adds a reusable `ColorMatrixEffect` that
renders an entire visual subtree once to a retained source texture, then routes
that texture through the existing GPU image-effect shader. The Uno adapter now
maps `SaveLayer(IColorFilter)` matrices to this effect instead of reporting an
unsupported operation or attempting a per-primitive color rewrite.

The new `layers` workload records one isolated layer containing 1,536
overlapping rounded/rectangular primitives. Its 4x5 matrix mixes RGB channels
and scales the already-composited alpha; this makes layer isolation part of the
pixel contract rather than a performance-only convention. Three alternating
fresh-process pairs used six warmups, 200 blocking samples, and seven 60-frame
batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.619800 ms | 3.807200 ms | 6.14× | 5.49×–6.82× |
| CPU frame | 0.083800 ms | 3.807200 ms | 45.43× | 44.34×–48.25× |
| completed batch/frame | 0.111765 ms | 3.785140 ms | 33.87× | 33.74×–34.41× |
| batched CPU/frame | 0.093043 ms | 3.785138 ms | 40.68× | 40.24×–42.29× |

Every run reports the same semantic hash, one stable pixel hash per backend,
and zero unsupported operations. ProGPU composites the retained layer and base
scene in two draws with no mask passes. The inspected side-by-side preserves
all 768 overlap cells and measures 55.68 dB PSNR and 0.999721 SSIM; the small
remaining differences are edge/color-rounding differences rather than missing
layer content. Raw JSON, BGRA readbacks, PNGs, and the contact sheet are under
`src/artifacts/performance/2026-08-23-color-matrix-layers/`.

A 40-sample built-in WebGPU integration smoke completes the same forced layer
workload with the same semantic hash and zero unsupported operations. Its
blocking median is 3.7880 ms and completed-batch median is 2.84402 ms; these
short-run values establish compatibility only and are not mixed into the
balanced ProGPU/Skia table.

## Isolated blend-mode layer follow-up

ProGPU `main` merge commit `64271d7f` supplies the retained blend effect. The
next layer gate fixes a semantic invariant in the adapter: `SaveLayer`'s
blend mode applies once to the already-composited isolated subtree, not to each
primitive recorded inside the scope. ProGPU's retained blend effect stores one
source surface, commits preceding destination draws before switching blend
state, composites the source once, and restores the previous mode. Focused GPU
tests cover Multiply overlap and live mutation to Screen.

The `blend-layers` workload places 1,536 opaque overlapping primitives inside
one Multiply layer over an opaque gray destination. Three alternating
fresh-process pairs used six warmups, 200 blocking samples, and seven 60-frame
batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.512700 ms | 2.667500 ms | 5.20× | 5.11×–5.40× |
| CPU frame | 0.064500 ms | 2.667500 ms | 41.36× | 40.29×–41.90× |
| completed batch/frame | 0.116472 ms | 2.682465 ms | 23.03× | 22.84×–23.69× |
| batched CPU/frame | 0.077272 ms | 2.682462 ms | 34.72× | 34.10×–35.18× |

Every process reports semantic hash `9537804D...716D9`, one stable pixel hash
per backend, and zero unsupported operations. ProGPU emits three draws and no
mask passes. The inspected 768-cell contact sheet preserves the destination,
single-source, and overlap regions and measures 49.04 dB mean per-channel RGB
PSNR and 0.999077 RGB SSIM, with byte-exact alpha. Raw JSON, BGRA readbacks,
PNGs, stdout, and the contact sheet are under
`src/artifacts/performance/2026-08-23-blend-mode-layers/`.

The built-in WebGPU lane also completes a 40-sample smoke with the same
semantic hash and zero unsupported operations, but its 13.47 dB mean
per-channel RGB PSNR and 0.494672 RGB SSIM against
Skia exposes a correctness failure: the current lane maps this Multiply layer
to source-over. Its 3.5384 ms blocking median, 1.9427 ms CPU median,
2.114397 ms completed-batch median, and 2.062037 ms batched CPU median are
therefore diagnostic values only. Completion and an empty unsupported counter
do not establish visual conformance. Image metrics were recomputed from the
stored PNGs with scikit-image 0.26.0 (`data_range=255`, RGB channels only).

## Unfiltered source-over layer follow-up

Uno uses parameterless `SaveLayer()` for composition masks and for isolated
multi-region drawing. The adapter previously mapped it to `Save()`, which
preserved only transform/clip state and allowed a transparent clear inside the
scope to erase the destination. The focused real-device reproduction starts
with opaque gray, clips the layer, clears its source to transparent, draws
overlapping red/green content, restores, and draws a following blue primitive.
Before the fix, the undrawn clipped gap read back as `00000000` instead of gray.
The root-cause correction routes the parameterless operation through ProGPU's
retained source-over blend effect, isolating the complete subtree before one
final composite.

The `isolation-layers` workload records one clipped transparent clear and
1,536 opaque overlapping primitives inside the unfiltered layer. Three
alternating fresh-process pairs used six warmups, 200 blocking samples, and
seven 60-frame batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.510700 ms | 2.623800 ms | 5.14× | 3.81×–5.20× |
| CPU frame | 0.062900 ms | 2.623800 ms | 41.71× | 41.33×–42.65× |
| completed batch/frame | 0.112073 ms | 2.630628 ms | 23.47× | 23.46×–23.80× |
| batched CPU/frame | 0.079242 ms | 2.630627 ms | 33.20× | 32.60×–33.42× |

Every process reports semantic hash `AD6F2629...BD67`, stable ProGPU pixel hash
`DC65A307...9996`, stable Skia pixel hash `CDAFE4F7...CF9D`, zero unsupported
operations, and exact alpha. ProGPU emits three draws and no mask passes. The
visually inspected 768-cell outputs align across all cells and measure 50.65 dB
mean per-channel RGB PSNR and 0.999293 RGB SSIM. The pre-fix diagnostic emitted
1,539 draws, measured 2.8352 ms blocking, and produced only 10.58 dB PSNR,
0.417786 RGB SSIM, and non-exact alpha; after isolation the retained scene
collapses to three draws while restoring correct destination pixels.

A 40-sample built-in WebGPU compatibility smoke preserves the same semantic
state with zero unsupported operations, exact alpha, 41.14 dB mean per-channel
RGB PSNR, and 0.993500 RGB SSIM against Skia. Its 3.5126 ms blocking median and
1.740095 ms completed-batch median are diagnostic only, not a balanced timing
distribution. Raw JSON, BGRA readbacks, PNGs, and the inspected contact sheet
are under `src/artifacts/performance/2026-08-23-isolation-layers/`.

A final source rebuild from gitlink `64271d7f` completed both harness graphs
with zero warnings or errors. The real-device smoke passed at frame 12, and a
fresh short ProGPU/Skia pair preserved semantic hash `AD6F2629...BD67`, pixel
hashes `DC65A307...9996` / `CDAFE4F7...CF9D`, three ProGPU draws, zero mask
passes, and zero unsupported operations.

## Reproduction

Build once with serial MSBuild:

```bash
cd src
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1
```

Run the standard steady scenarios with 100 blocking samples and nine 60-frame
batches:

```bash
dotnet Uno.UI.Composition.Backend.Benchmarks/bin/Release/net10.0/Uno.UI.Composition.Backend.Benchmarks.dll \
  --backend progpu \
  --scenario clips \
  --warmups 8 \
  --samples 100 \
  --batch-size 60 \
  --batches 9 \
  --output artifacts/performance/progpu-clips.json \
  --pixels-output artifacts/performance/progpu-clips.bgra
```

Run the effects workload with the qualification shape used here:

```bash
dotnet Uno.UI.Composition.Backend.Benchmarks/bin/Release/net10.0/Uno.UI.Composition.Backend.Benchmarks.dll \
  --backend progpu \
  --scenario effects \
  --warmups 4 \
  --samples 40 \
  --batch-size 20 \
  --batches 3 \
  --output artifacts/performance/progpu-effects.json \
  --pixels-output artifacts/performance/progpu-effects.bgra
```

Repeat with `--backend skia`. Valid scenarios are `cached`, `sparse`, `text`,
`paths`, `strokes`, `materials`, `layers`, `isolation-layers`, `blend-layers`,
`images`, `clips`, and `effects`. Add `--force-redraw` to
alternate target wrappers and disable retained populated-target reuse. Raw
local artifacts for this run are under
`src/artifacts/performance/2026-08-23-retained-eligibility/`; the
retained-update follow-up is under
`src/artifacts/performance/2026-08-23-sparse-update/`.

A post-merge smoke from the exact final gitlink rebuilt both harnesses and
preserved the qualified clip and effects semantic/pixel hashes. It is stored
under `src/artifacts/performance/2026-08-23-final-merge-smoke/`; this short run
is a source-integration check, not a replacement for the eight-process result.
