# Correctness-gated renderer results — 2026-08-23

## Scope

This is a two-repetition, fresh-process qualification matrix for the v2
drawing-backend harness. It separates CPU submit from GPU completion,
explicitly clears every frame, reads back the final target, and records 100
samples per backend/scenario. Each table reports the range across the two
current runs. It is not the final publication run: the protocol still requires
eight balanced independent process triplets under controlled power and thermal
conditions.

- Machine: Apple M3 Pro, 11 logical processors, 18 GB RAM.
- OS/runtime: macOS 26.6 arm64, .NET 10.0.9, Release.
- Target: 1280×720, premultiplied BGRA8.
- GPU lanes: wgpu-native on Metal; ProGPU uses one sample per pixel.
- Run shape: 8 warmups, 100 blocking samples, then 9 batches of 60 frames.
- Mutation/record construction and final readback are outside timed regions.
- ProGPU reported zero unsupported operations in all scenarios.

## CPU frame-submit result

Median milliseconds from `BeginPresent` through command submission. This is
the cleanest same-boundary measure of framework-to-renderer CPU cost. Lower is
better.

| Scenario | ProGPU | Uno WebGPU | Uno Skia |
|---|---:|---:|---:|
| cached, 768 fills | 0.0487–0.0575 | 0.0932–0.0942 | 0.3497–0.3554 |
| sparse retained-row mutation | 0.1861–0.2009 | 0.1240–0.1275 | 0.3555–0.3580 |
| 128 text runs | 0.0570–0.0601 | 9.5325–9.5784 | 4.4459–4.4529 |
| 1,000 paths | 0.0520–0.0558 | 9.9859–10.0193 | 2.3027–2.3335 |
| 240 images | 0.2107–0.2270 | 2.2628–2.7779 | 1.0442–1.0543 |

ProGPU beats software Skia on CPU submit in both runs of all five workloads.
The sparse result is the narrowest margin. Its late steady sample has 24
resident row pages, 23 retained-picture hits, zero retained-picture
compilations, 4 KiB of scene upload, and one merged draw call. Cached, text,
and path scenes use the whole-scene cache.

## Bounded-batch throughput

Median total milliseconds per frame after 60 submissions and one completion
wait. This deliberately saturates the command queue; it is useful throughput
evidence but does not model normal swap-chain/v-sync pacing.

| Scenario | ProGPU | Uno WebGPU | Uno Skia | conservative ProGPU speedup |
|---|---:|---:|---:|---:|
| cached | 0.1115–0.1130 | 0.1009–0.1046 | 0.3579–0.3624 | 3.17× |
| sparse | 0.2149–0.2170 | 0.1410–0.1430 | 0.3568–0.3633 | 1.64× |
| text | 0.1138–0.1181 | 9.8384–9.8744 | 4.4535–4.4577 | 37.71× |
| paths | 0.1368–0.1380 | 11.2952–11.3452 | 2.3222–2.3410 | 16.82× |
| images | 0.2274–0.2293 | 2.7495–2.7693 | 1.0571–1.0711 | 4.61× |

The conservative speedup divides the lower Skia result by the higher ProGPU
result, so it does not depend on selecting the favorable repetition. ProGPU is
faster than software Skia in both repetitions of all five scenarios. Uno
WebGPU remains slightly faster for the two fill-only workloads; ProGPU is much
faster for text, paths, and images.

## Blocking completion boundary

| Scenario | ProGPU total | Uno WebGPU total | Uno Skia total |
|---|---:|---:|---:|
| cached | 1.3128–1.3252 | 1.6157–1.6167 | 0.3497–0.3554 |
| sparse | 1.4593–1.4830 | 1.6554–1.6559 | 0.3555–0.3580 |
| text | 1.3250–1.3258 | 12.9179–13.1105 | 4.4459–4.4529 |
| paths | 1.3189–1.3225 | 15.8397–15.9179 | 2.3028–2.3338 |
| images | 1.4840–1.4993 | 4.6404–4.9612 | 1.0443–1.0544 |

The native `wgpuDevicePoll(wait=true)` boundary is quantized at roughly
1.51 ms for already-small ProGPU frames and dominates the blocking total. It
must not be labeled compositor CPU time. ProGPU's internal median compositor
times are 0.038–0.224 ms. A GPU timestamp or Metal System Trace is required to
split actual execution from polling/wakeup granularity.

## Pixel gate

RGB differences are measured against the Skia target readback. `px>8` and
`px>32` count pixels whose largest RGB-channel difference exceeds the stated
threshold.

| Scenario | Backend | RGB MAE | Max | px>8 | px>32 |
|---|---|---:|---:|---:|---:|
| cached | ProGPU | 0.000000 | 0 | 0% | 0% |
| cached | Uno WebGPU | 0.000000 | 0 | 0% | 0% |
| sparse | ProGPU | 0.000000 | 0 | 0% | 0% |
| sparse | Uno WebGPU | 0.000000 | 0 | 0% | 0% |
| text | ProGPU | 0.708929 | 81 | 3.6036% | 0.4512% |
| text | Uno WebGPU | 1.392791 | 123 | 4.7063% | 2.2434% |
| paths | ProGPU | 0.181166 | 22 | 0.8025% | 0% |
| paths | Uno WebGPU | 0.615427 | 75 | 3.2635% | 0.7521% |
| images | ProGPU | 0.041453 | 1 | 0% | 0% |
| images | Uno WebGPU | 0.041453 | 1 | 0% | 0% |

Cached and sparse ProGPU output is byte-identical to Skia. Image differences
are only one quantization level. ProGPU text and path rasterization is closer
to Skia than Uno WebGPU by MAE, severe-error fraction, and visual inspection.
This provides no signal of a text-quality regression from the retained glyph
path.

## Optimization evidence

Four root causes were addressed:

1. Exact rounded border differences previously entered the general geometry
   mask path. Analytic ring recognition and matching-parent clip reduction cut
   a representative SamplesApp frame from 47 mask draws/46 mask passes to
   15 mask draws/14 mask passes; median render-pass CPU fell from about
   2.515 ms to 0.816 ms in the diagnostic capture.
2. A changing root recording invalidated compilation of immutable nested
   pictures. Bounded retained-picture pages now reuse unchanged subtrees.
   Admission ignores one-shot and tiny analytic pictures, avoiding the measured
   regression from caching 768 one-command cells. The benchmark uses 24
   32-command row subtrees and changes one row per frame.
3. A leading replacement clear was emitted as a redundant full-target draw.
   Clear metadata now becomes the render-pass attachment clear while ordered
   nested replacement semantics retain the explicit draw fallback.
4. Resource cleanup forced a blocking device drain every eight submissions,
   even though completed native work is polled non-blockingly every frame. A
   managed CPU trace attributed 11.22 seconds of a 21.34-second profiled run to
   queue polling below cleanup. ProGPU now exposes the finite drain bound; this
   backend selects 64 submissions, preserving the default value of eight for
   other hosts. The 60-frame benchmark batch consequently measures overlapped
   submission instead of an artificial fence every eight frames.

The dependency changes are outside the drawing SPI and keep unsupported and
masked cases on the existing correctness path. The queue change does not alter
scene, glyph, text-shaping, or shader output; every final pixel hash matches
the corresponding pre-change artifact.

## HostBackdrop diagnostic boundary

The five standard workloads contain no HostBackdrop command, so their direct
present path and reported results are not HostBackdrop measurements. A live
SamplesApp AutoSuggestBox popup was separately captured after proving ProGPU
won backend negotiation. Its scene contained an Acrylic/HostBackdrop material
with a 60 px blur and used the conditional offscreen capture route. That one
instrumented frame reported 251.404 ms total (250.626 ms render pass). The
popup also remained visibly different from the matched Skia capture. This
single sample is diagnostic evidence only; it identifies a required effects
benchmark and shader optimization, and is not included in comparative tables.

## Optimization progression

A Metal trace and scene dump exposed one further root cause after the balanced
matrix: a leading Uno `Clear` became a two-million-unit source-replacement quad
whenever retained children followed it. ProGPU already clears the render-pass
attachment, so this produced a redundant full-target draw. The integration now
carries a leading clear as recording metadata, applies it as the attachment
clear, and materializes the replacement quad only when that record is nested
after existing ordered content.

The focused 20-warmup, 200-sample, 30-by-120-frame check retained the exact
cached SHA-256 and reduced the steady scene from 3,076 vertices/two draws to
3,072 vertices/one draw. Cached saturated throughput first fell from 0.4556 to
0.2835 ms/frame. Sparse remained at 0.4584 ms/frame, which led to the managed
CPU trace and the queue-drain correction above. Two subsequent alternating
120-frame-batch checks measured ProGPU sparse at 0.1696 and 0.1907 ms/frame
against Skia at 0.3576 and 0.3603 ms/frame. The final standard-shape matrix is
the range reported in the current tables.

The raw current artifacts are named
`{backend}-{scenario}-queue-window-run{1,2}.json` under `artifacts/`. The older
`*-diagnostic.json` files preserve the pre-queue-window qualification baseline.

## Reproduction

Build once, then run each backend/scenario in a fresh process:

```bash
cd src
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1

Uno.UI.Composition.Backend.Benchmarks/bin/Release/net10.0/Uno.UI.Composition.Backend.Benchmarks \
  --backend progpu \
  --scenario sparse \
  --warmups 8 \
  --samples 100 \
  --batch-size 60 \
  --batches 9 \
  --output ../docs/progpu-uno-backend/artifacts/progpu-sparse-diagnostic.json \
  --pixels-output /tmp/progpu-sparse.bgra
```

Valid backends are `progpu`, `webgpu`, and `skia`; valid scenarios are
`cached`, `sparse`, `text`, `paths`, and `images`.
