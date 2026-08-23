# Correctness-gated renderer results — 2026-08-23

## Scope

The primary result is an eight-pair, alternating fresh-process comparison of
the Uno ProGPU and software-Skia drawing backends. It covers all seven current
steady-state harness scenarios. Every process renders the same semantic state,
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
`paths`, `images`, `clips`, and `effects`. Raw local artifacts for this run are
under `src/artifacts/performance/2026-08-23-retained-eligibility/`; the
retained-update follow-up is under
`src/artifacts/performance/2026-08-23-sparse-update/`.

A post-merge smoke from the exact final gitlink rebuilt both harnesses and
preserved the qualified clip and effects semantic/pixel hashes. It is stored
under `src/artifacts/performance/2026-08-23-final-merge-smoke/`; this short run
is a source-integration check, not a replacement for the eight-process result.
