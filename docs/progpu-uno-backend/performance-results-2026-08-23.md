# Correctness-gated renderer results — 2026-08-23

## Scope

This is a single-process qualification matrix for the v2 drawing-backend
harness. It is stronger than the initial diagnostic run because it separates
CPU submit from GPU completion, explicitly clears every frame, reads back the
final target, and records 100 samples per backend/scenario. It is not the final
publication run: the protocol still requires eight balanced independent
process triplets and profiler traces.

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
better; speedup is Skia median divided by ProGPU median.

| Scenario | ProGPU | Uno WebGPU | Uno Skia | ProGPU speedup vs Skia |
|---|---:|---:|---:|---:|
| cached, 768 fills | 0.0521 | 0.0989 | 0.3607 | 6.92× |
| sparse retained-row mutation | 0.2283 | 0.1445 | 0.3629 | 1.59× |
| 128 text runs | 0.0487 | 9.7512 | 4.5205 | 92.82× |
| 1,000 paths | 0.0482 | 9.7453 | 2.3597 | 48.96× |
| 240 images | 0.2451 | 2.3605 | 1.0676 | 4.36× |

ProGPU beats software Skia on CPU submit in all five measured workloads. The
sparse result is the narrowest margin. Its late steady sample has 24 resident
row pages, 23 retained-picture hits, zero retained-picture compilations, 4 KiB
of scene upload, and three draw calls. Cached, text, and path scenes use the
whole-scene cache and have median scene-compile cost at or below 0.002 ms.

## Bounded-batch throughput

Median total milliseconds per frame after 60 submissions and one completion
wait. This deliberately saturates the command queue; it is useful throughput
evidence but does not model normal swap-chain/v-sync pacing.

| Scenario | ProGPU | Uno WebGPU | Uno Skia | ProGPU / Skia |
|---|---:|---:|---:|---:|
| cached | 0.4556 | 0.1691 | 0.3653 | 1.25× |
| sparse | 0.5939 | 0.1794 | 0.3661 | 1.62× |
| text | 0.4032 | 10.2827 | 4.5412 | 0.09× |
| paths | 0.4285 | 11.5401 | 2.3581 | 0.18× |
| images | 0.6501 | 2.6608 | 1.0674 | 0.61× |

ProGPU has higher saturated GPU throughput cost than software Skia for the two
small fill-only scenes. It is 11.26× faster for text, 5.50× faster for paths,
and 1.64× faster for images. This distinction is intentional: “faster than
Skia” is supported for CPU frame cost across all scenarios and for saturated
throughput on the content-heavy scenarios, but not for fill-only saturation.

## Blocking completion boundary

| Scenario | ProGPU total | ProGPU CPU | ProGPU completion wait | Uno WebGPU total | Uno Skia total |
|---|---:|---:|---:|---:|---:|
| cached | 1.5873 | 0.0521 | 1.5115 | 1.6264 | 0.3607 |
| sparse | 1.7831 | 0.2283 | 1.5173 | 1.6843 | 0.3630 |
| text | 1.5743 | 0.0487 | 1.5095 | 16.1699 | 4.5205 |
| paths | 1.5742 | 0.0482 | 1.5095 | 16.7478 | 2.3597 |
| images | 1.7851 | 0.2451 | 1.5165 | 8.5603 | 1.0676 |

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

Two root causes were addressed:

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

Both changes are outside Uno's drawing SPI and keep unsupported/masked cases on
the existing correctness path.

## Post-matrix attachment-clear optimization

A Metal trace and scene dump exposed one further root cause after the balanced
matrix: a leading Uno `Clear` became a two-million-unit source-replacement quad
whenever retained children followed it. ProGPU already clears the render-pass
attachment, so this produced a redundant full-target draw. The integration now
carries a leading clear as recording metadata, applies it as the attachment
clear, and materializes the replacement quad only when that record is nested
after existing ordered content.

The focused 20-warmup, 200-sample, 30-by-120-frame check retained the exact
cached SHA-256 and reduced the steady scene from 3,076 vertices/two draws to
3,072 vertices/one draw. Cached saturated throughput measured 0.2835 ms/frame,
below the qualification matrix's 0.3653 ms Skia result. The corresponding
sparse check measured 0.4584 ms/frame and remains an optimization target; it
is not folded into the earlier table because the run shape differs. Retained
solid pages now also skip empty brush-map scans and use contiguous index
rebasing. A balanced fresh-process rerun is still required before publishing
replacement matrix values.

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
