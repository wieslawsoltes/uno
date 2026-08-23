# Correctness-gated renderer results — 2026-08-23

## GPU-to-GPU Metal qualification

The current like-for-like GPU comparison is three alternating fresh-process
pairs of ProGPU/Metal and Skia/Metal on the same Apple M3 Pro. Both backends
render to retained 1280x720 premultiplied BGRA8 Metal textures. ProGPU uses
wgpu-native's Metal backend. Skia receives Uno's `IMetalDeviceContext`, creates
its normal Metal `GRContext`, flushes the frame, and is fenced by an empty
command buffer committed to the same `MTLCommandQueue`. Pixel readback occurs
after GPU completion and outside the timed region.

Each ordinary scenario used 6 warmups, 100 blocking samples, and 7 batches of
60 frames. `effects` used 4 warmups, 40 blocking samples, and 3 batches of 20.
The values below are medians of the three process medians. Batch speedup is the
median of the three same-index Skia/ProGPU ratios; its range shows all three
pairs.

| Scenario | ProGPU completed batch/frame | Skia/Metal completed batch/frame | paired speedup (range) | ProGPU blocking total | Skia/Metal blocking total | total speedup |
|---|---:|---:|---:|---:|---:|---:|
| cached | 0.001245 ms | 0.275845 ms | 223.33x (210.95x-252.30x) | 0.0032 ms | 0.5440 ms | 152.05x |
| sparse | 0.172092 ms | 0.273382 ms | 1.57x (1.37x-1.72x) | 0.4293 ms | 0.5271 ms | 1.23x |
| text | 0.001180 ms | 0.489477 ms | 414.81x (390.87x-444.68x) | 0.0035 ms | 0.9920 ms | 304.14x |
| paths | 0.001082 ms | 0.693403 ms | 641.05x (561.67x-645.17x) | 0.0033 ms | 1.0175 ms | 303.48x |
| strokes | 0.001208 ms | 1.939457 ms | 1,605.03x (1,543.33x-1,882.23x) | 0.0032 ms | 2.8183 ms | 879.13x |
| materials | 0.001147 ms | 1.246987 ms | 1,087.67x (1,062.77x-1,118.27x) | 0.0032 ms | 1.7929 ms | 527.32x |
| layers | 0.001205 ms | 0.826900 ms | 686.22x (656.55x-690.04x) | 0.0035 ms | 1.2625 ms | 364.54x |
| isolation layers | 0.001247 ms | 0.830947 ms | 665.53x (649.06x-685.79x) | 0.0032 ms | 1.3513 ms | 425.94x |
| mask layers | 0.001238 ms | 0.827257 ms | 669.17x (634.65x-706.05x) | 0.0034 ms | 1.3672 ms | 369.51x |
| blend layers | 0.001212 ms | 0.832727 ms | 684.46x (666.18x-715.31x) | 0.0032 ms | 1.4485 ms | 452.66x |
| blend corpus | 0.002952 ms | 4.047865 ms | 1,337.20x (1,280.08x-1,371.38x) | 0.0044 ms | 5.2236 ms | 1,187.18x |
| images | 0.001160 ms | 0.406012 ms | 350.01x (294.99x-377.62x) | 0.0034 ms | 0.6467 ms | 190.21x |
| clips | 0.001225 ms | 1.522812 ms | 1,243.11x (1,234.76x-1,263.37x) | 0.0033 ms | 2.1475 ms | 696.00x |
| shadows | 0.007932 ms | 0.458542 ms | 57.81x (55.29x-61.55x) | 0.0102 ms | 0.9300 ms | 86.11x |
| effects | 2.109830 ms | 6.077170 ms | 2.81x (2.81x-2.91x) | 2.4104 ms | 8.2955 ms | 3.41x |

ProGPU wins completed-batch throughput and synchronized blocking total in all
15 scenarios. It also wins the CPU-submit boundary in every scenario. The
narrowest GPU-to-GPU margin is the intentionally mutating `sparse` workload;
the effects workload remains 2.81x faster after both queues are completed.
Very large retained-scene ratios are real for this harness but should be read
as integration-path results: ProGPU can reuse an unchanged retained target,
whereas the Skia drawing contract still replays and flushes the recorded scene.

The first pair retained BGRA readbacks for the GPU-specific pixel gate. All
alpha bytes match exactly, semantic hashes match per scenario, and both cached
and sparse frames are byte-identical. RGB differences against Skia/Metal are:

| Scenario | RGB MAE | Max | PSNR |
|---|---:|---:|---:|
| text | 0.910325 | 105 | 33.892 dB |
| paths | 0.356533 | 54 | 41.052 dB |
| strokes | 0.674864 | 151 | 35.553 dB |
| materials | 2.203289 | 199 | 34.847 dB |
| layers | 0.070465 | 4 | 59.503 dB |
| isolation layers | 0.018131 | 3 | 63.342 dB |
| mask layers | 0.038623 | 6 | 59.119 dB |
| blend layers | 0.021881 | 3 | 61.981 dB |
| blend corpus | 0.061682 | 18 | 58.651 dB |
| images | 0.115367 | 1 | 57.510 dB |
| clips | 0.455348 | 76 | 41.520 dB |
| shadows | 0.357339 | 66 | 43.805 dB |
| effects | 3.473563 | 56 | 31.988 dB |

All 90 original matrix JSON artifacts satisfy the v3 schema invariants used by
the harness:
1280x720 target, 64-digit semantic and pixel hashes, and zero unsupported
operations. Raw local JSON/readbacks are under
`src/artifacts/performance/2026-08-23-gpu-vs-gpu-matrix/`. The historical
software-Skia qualification remains below because it is useful for separating
Skia CPU raster cost from Skia's GPU integration cost; it is not the basis for
the GPU-to-GPU claim.

The `materials` row and pixel metric include a subsequent three-pair
root-cause follow-up. Uno's gradient `localMatrix` is a forward shader matrix,
whereas ProGPU's retained brush stores the inverse destination-to-brush
coordinate transform. Inverting once at shader construction raises the full
materials comparison from 28.112 to 34.847 dB and reduces RGB MAE from
4.507509 to 2.203289 without adding measured per-frame work. The transformed
linear column rises from 23.864 to 63.327 dB. Remaining focal-radial
differences are dominated by the Skia backend's documented two-point-conical
approximation. The follow-up artifacts are under
`src/artifacts/performance/2026-08-23-gradient-matrix-fix/`.

## Scope

The primary result is an eight-pair, alternating fresh-process comparison of
the Uno ProGPU and software-Skia drawing backends. It covers the seven primary
qualification scenarios; later sections add retained-output, native-stroke,
gradient-material, color-matrix-layer, unfiltered-isolation-layer,
destination-in-mask-layer, and blend-mode-layer follow-ups. Every process
renders the same semantic state; a later section adds the all-27-mode blend
corpus. Every process
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

### Current-main completion and retained-target audit

A later clean refresh at ProGPU merge `d9a85cba` reran cached and sparse in
three alternating ProGPU/Skia pairs with 8 warmups, 100 blocking samples, and
nine 60-frame batches. This supersedes the earlier cached blocking-gap claim:
retained-target reuse now avoids both renderer submission and queue waiting for
the unchanged frame.

| Scenario and boundary | ProGPU process medians | Skia process medians | Conservative result |
|---|---:|---:|---:|
| cached blocking total | 0.0030–0.0036 ms | 0.3607–0.3688 ms | ProGPU at least 100.2× faster |
| cached completed batch/frame | 0.001133–0.001262 ms | 0.361048–0.393927 ms | ProGPU at least 286.2× faster |
| sparse blocking total | 0.3912–0.4793 ms | 0.3535–0.3617 ms | residual queue-completion gap |
| sparse renderer CPU | 0.0886–0.0986 ms | 0.3535–0.3617 ms | ProGPU at least 3.59× faster |
| sparse completed batch/frame | 0.139132–0.142742 ms | 0.365287–0.379483 ms | ProGPU at least 2.56× faster |

Every cached pair produced pixel hash `15DECAB...F718`; every sparse pair
produced `4ED36839...82B2`. Both are byte-identical across ProGPU and Skia, and
all ProGPU runs report zero unsupported operations. Sparse submits only 4 KiB,
two draws, and 3,072 vertices. Its median renderer CPU is below 0.1 ms; the
remaining blocking duration is almost entirely the host queue-completion wait.

A 20,000-frame sampled-thread trace attributes approximately 8.26 seconds of
inclusive time to the borrowed WebGPU lifetime poll, versus 1.38 seconds to
compositor rendering and 0.68 seconds to retained-picture compilation. That
ownership evidence drove five controlled candidates; all were rejected:

- callback-only completion stalled because callbacks require explicit device
  progress in this wgpu-native configuration;
- reducing the progress spin from 64 to 1 raised the 100-frame blocking median
  to 0.5346 ms;
- `wgpuInstanceWaitAny` is exported but aborts as unimplemented;
- indexed submission plus blocking device poll raised the median to 1.4053 ms,
  including an approximately 1.26 ms blocking-poll floor;
- submit-time completion futures raised blocking total to 0.5862 ms and
  completed-batch time to 0.312848 ms/frame.

The managed retained-page index-rebase loop was also tested with portable SIMD
in five alternating, binary-isolated 300-frame pairs. Scalar scene compilation
was 0.0577–0.0595 ms; SIMD was 0.0609–0.0721 ms. All ten pixel hashes matched,
but the SIMD path regressed the measured stage and was discarded. Native C++
parity was audited before the experiment: its semantic compiler emits one
scene-wide packed page directly and therefore has no equivalent local-page
rebase pass. This is a concrete representation-ownership exception, not an
unreviewed managed-only optimization.

Raw results and the trace are under
`src/artifacts/performance/2026-08-23-blocking-completion-baseline/` and
`src/artifacts/performance/2026-08-23-sparse-profile/`.

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

This earlier forced-redraw checkpoint predates the gradient local-matrix
correction. The current GPU-to-GPU section at the top of this report supersedes
its transformed-gradient quality result while retaining it as historical
evidence for the original optimization tranche.

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

## Destination-in composition-mask follow-up

Uno's composition-mask brush records its source in an outer source-over layer,
then restores a nested DstIn layer containing the alpha mask. A retained blend
surface must evaluate transparent source across the preceding destination
bounds: outside the mask, DstIn clears the source rather than leaving it
unchanged. The failed-before real-device fixture instead retained opaque red at
the masked-out pixel (`0000FFFF`).

The root-cause correction makes replacement clears inside explicitly clipped
effect layers contribute their clip to retained bounds, then expands blend
surfaces to the preceding destination bounds for the six Porter-Duff modes
whose transparent source clears destination. Focused pixels cover `Src`,
`Modulate`, `DstIn`, `SrcIn`, `SrcOut`, and `DstAtop`, plus a completely empty
DstIn mask and state restoration after the nested layers.

The `mask-layers` workload records 768 colored source cells and 768 smaller
rounded alpha masks inside nested source-over/DstIn layers. Three alternating
fresh-process pairs used six warmups, 200 blocking samples, and seven 60-frame
batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.554600 ms | 2.739200 ms | 4.94× | 4.83×–4.97× |
| CPU frame | 0.066100 ms | 2.739200 ms | 41.44× | 41.15×–42.04× |
| completed batch/frame | 0.113120 ms | 2.725747 ms | 24.10× | 23.96×–24.19× |
| batched CPU/frame | 0.080617 ms | 2.725745 ms | 33.81× | 32.65×–33.95× |

Every process reports semantic hash `055E3B4D...BCAA`, stable ProGPU pixel hash
`13FAAC2D...B08D`, stable Skia pixel hash `BE4411CA...6C3A`, and zero unsupported
operations. ProGPU emits three draws and no mask passes. The inspected outputs
preserve all 768 rounded masks and exact alpha, measuring 49.73 dB mean
per-channel RGB PSNR and 0.998704 RGB SSIM against Skia.

A 40-sample built-in WebGPU compatibility smoke preserves the same semantic
state with zero unsupported operations and exact alpha. It measures 43.58 dB
mean per-channel RGB PSNR and 0.993545 RGB SSIM against Skia; its 2.5052 ms
blocking median and 1.077125 ms completed-batch median are diagnostic only.
Raw JSON, BGRA readbacks, PNGs, stdout, and the visually inspected outputs are
under `src/artifacts/performance/2026-08-23-mask-layers/`.

## All-mode blend corpus follow-up

The `blend-corpus` scene exercises every one of Uno's 27 `BlendMode` values in
a separate clipped isolation tile. Each tile has an opaque, mode-specific
destination and two overlapping translucent rounded sources. This covers all
Porter-Duff, separable, and non-separable modes exposed by the drawing seam and
forces destination sampling, source premultiplication, effect restoration, and
cross-tile isolation through one deterministic retained scene.

Three alternating fresh-process pairs used six warmups, 200 blocking samples,
and seven 60-frame batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 0.557400 ms | 14.795100 ms | 26.54× | 26.54×–26.93× |
| CPU frame | 0.141200 ms | 14.795100 ms | 104.78× | 103.70×–107.14× |
| completed batch/frame | 0.169973 ms | 14.811122 ms | 87.14× | 86.78×–87.58× |
| batched CPU/frame | 0.164473 ms | 14.811120 ms | 90.05× | 89.49×–91.34× |

Every process reports semantic hash `AE571E0D...CF98A`, stable ProGPU pixel
hash `0929F8EB...B779`, stable Skia pixel hash `824FC7F1...58A9`, and zero
unsupported operations. ProGPU emits 28 final draws with 3,072 vector vertices
and no mask passes. Original-resolution visual inspection finds no structural
mismatch; the complete image has exact alpha, 0.072627 RGB mean absolute error,
52.61 dB mean per-channel RGB PSNR, and 0.999581 RGB SSIM against Skia.

The built-in WebGPU compatibility smoke completes with the same semantic hash
and zero unsupported operations but maps most modes to source-over. Its output
has non-matching alpha, 26.606 RGB mean absolute error, 14.75 dB mean
per-channel RGB PSNR, and 0.813692 RGB SSIM against Skia. Its 6.1219 ms
blocking median and 3.089835 ms completed-batch median are diagnostic only and
must not be interpreted as a conforming comparison. Raw JSON, BGRA readbacks,
PNGs, and the visually inspected outputs are under
`src/artifacts/performance/2026-08-23-blend-corpus/`.

## Anisotropic and additive shadow follow-up

The backend now preserves Uno's independent `sigmaX`/`sigmaY` contract through
the ProGPU retained effect and compute stack. Zero-radius axes bypass their
Gaussian pass, effect bounds are padded per axis, and additive shadows use the
native `Plus` blend pipeline. The direct-shadow opacity mask is recorded as
opaque white; this prevents the requested translucent color and alpha from
being multiplied once while building the mask and again while tinting it.

The new `shadows` workload draws 128 path shadows, alternates `(6, 2)` and
`(2, 6)` sigma, enables additive composition for 32 shadows, and explicitly
draws all 128 sources. Three alternating fresh-process pairs used eight
warmups, 100 blocking samples, and nine 60-frame batches:

| Boundary | ProGPU process-median | Skia process-median | speedup | paired range |
|---|---:|---:|---:|---:|
| blocking total | 1.484700 ms | 2.667000 ms | 1.81× | 1.73×–1.82× |
| CPU frame | 1.083400 ms | 2.666900 ms | 2.47× | 2.30×–2.49× |
| completed batch/frame | 1.127227 ms | 2.678258 ms | 2.38× | 2.24×–2.39× |
| batched CPU/frame | 1.119553 ms | 2.678258 ms | 2.39× | 2.26×–2.41× |

Every process reports semantic hash `97ADF831...AF37`, stable ProGPU pixel
hash `20A59930...116B`, stable Skia pixel hash `C388B13E...57B`, and zero
unsupported operations. ProGPU emits 257 final draws, 3,584 vector vertices,
and no mask passes. Original-resolution inspection finds matching topology,
anisotropy, source placement, and additive highlights. Alpha is byte-exact;
the complete image measures 0.331633 RGB mean absolute error, 46.29 dB mean
per-channel RGB PSNR, and 0.997474 RGB SSIM against Skia. Only 0.00467% of
pixels exceed a 32-level RGB channel difference.

A built-in WebGPU compatibility smoke completes the same semantic workload
with zero unsupported operations. Its short timing distribution is diagnostic
only. Raw JSON, BGRA readbacks, PNGs, and the inspected side-by-side are under
`src/artifacts/performance/2026-08-23-anisotropic-shadows/`.

## WebGPU lifetime serialization checkpoint

Six alternating fresh-process runs compared the previous merged ProGPU
revision with the process-wide managed/native lifetime correction using the
managed-picture workload (384 primitives, 60 warmups, 300 iterations, Release).
Both revisions report zero managed bytes per stable frame. Median native
submission changed from 0.08775 ms to 0.08440 ms, managed submission from
0.51930 ms to 0.51825 ms, native total from 1.61735 ms to 1.60715 ms, and
managed total from 2.54845 ms to 2.45995 ms. These small changes establish no
regression; they are not presented as a speedup claim.

The matched output differs by at most 11 channel levels, with only three pixels
above 3/255 and mean channel error approximately 0.000311/255. A direct-apphost
Time Profiler comparison measured 3.1999 versus 3.2080 ms/frame, while Metal
System Trace measured 3.1664 versus 3.1677 ms/frame with exactly 8,434
submissions in each trace and identical peak/final allocation counters. The
Allocations and VM Tracker templates suspended the unsigned direct apphost, so
no native-allocation conclusion is drawn from those instruments; the renderer's
own stable-frame managed allocation counter remains zero.

The same change passed matched managed and native C++ concurrency tests and the
27-check dependency matrix, including the exact Metal-provider hardware gate
and package consumers on six OS/architecture combinations. This evidence is a
mandatory managed/native parity gate, not merely a managed-backend check.

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

Repeat with `--backend skia` for software Skia or, on macOS, with
`--backend skia-metal` for the real Skia `GRContext` Metal path. Valid
scenarios are `cached`, `sparse`, `text`,
`paths`, `strokes`, `materials`, `layers`, `isolation-layers`, `mask-layers`,
`blend-layers`, `blend-corpus`, `images`, `clips`, `shadows`, and `effects`. Add
`--force-redraw` to
alternate target wrappers and disable retained populated-target reuse. Raw
local artifacts for this run are under
`src/artifacts/performance/2026-08-23-retained-eligibility/`; the
retained-update follow-up is under
`src/artifacts/performance/2026-08-23-sparse-update/`.

A post-merge smoke from the exact final gitlink rebuilt both harnesses and
preserved the qualified clip and effects semantic/pixel hashes. It is stored
under `src/artifacts/performance/2026-08-23-final-merge-smoke/`; this short run
is a source-integration check, not a replacement for the eight-process result.
