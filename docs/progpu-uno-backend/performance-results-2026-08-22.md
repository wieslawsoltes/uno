# Diagnostic renderer results — 2026-08-22

## Scope and confidence

These are short diagnostic measurements of the drawing-SPI harness, not
publication results and not whole-application frame rates. They answer whether
the three backends execute equivalent retained workloads, wait for completion,
and expose obvious integration costs. They do not satisfy the protocol's
eight-process/100-sample publication gate.

- Machine: Apple M3 Pro, 11 logical processors, 18 GB RAM.
- OS/runtime: macOS 26.6 arm64, .NET 10.0.5, Release.
- GPU lanes: wgpu-native on Metal, 1280×720 BGRA8, device polling included in
  every timed sample.
- Skia lane: Uno's software target on the CPU. It is synchronous but is not a
  GPU-equivalent completion path, so comparisons to it are context only.
- Run shape: one process per backend/scenario, 4 warmups, 12 samples.
- Work mutation and recording occur outside the timed submit boundary.
- Every ProGPU scene reported zero unsupported operations. Backends have the
  same semantic workload hash per scenario; this version does not yet store a
  cross-backend pixel-difference artifact.

## Results

Median and p95 are milliseconds. Ratios below are ProGPU divided by the named
backend; less than 1 means ProGPU was faster.

| Scenario | ProGPU median / p95 | Uno WebGPU median / p95 | Uno Skia median / p95 | ProGPU/WebGPU | ProGPU/Skia |
|---|---:|---:|---:|---:|---:|
| cached, 768 fills | 1.8445 / 1.9020 | 1.4784 / 1.7975 | 0.2705 / 0.2924 | 1.25× | 6.82× |
| sparse mutation | 1.8940 / 2.1007 | 1.4612 / 1.5755 | 0.2865 / 0.4645 | 1.30× | 6.61× |
| 128 text runs | 2.4012 / 3.7210 | 14.2924 / 15.4927 | 4.3250 / 4.8763 | 0.17× | 0.56× |
| 1,000 paths | 3.3540 / 14.7228 | 24.9382 / 26.7044 | 2.2627 / 2.9005 | 0.13× | 1.48× |
| 240 images | 2.3082 / 13.4057 | 4.5365 / 5.5966 | 0.9514 / 1.0521 | 0.51× | 2.43× |

The 12-sample nearest-rank p95 is the maximum sample, so the ProGPU path/image
spikes are visible but cannot be characterized statistically from this run.
Raw samples are in [artifacts](artifacts/).

## Interpretation

The cached/sparse result shows a roughly 0.37–0.43 ms diagnostic fixed cost
above Uno WebGPU for this small scene. Likely contributors are ProGPU picture
replay/visual construction and compositor preparation; a Metal trace is needed
before attributing it to any one stage.

The text result is the important integration signal: the typed glyph-run
geometry preserves ProGPU's glyph atlas route and avoids rebuilding/drawing the
same neutral outlines on every replay. In this diagnostic it was 5.95× faster
than Uno WebGPU and 1.80× faster than software Skia by median. This is evidence
for the fast-path design, not yet a general text-performance claim.

The path result shows a large improvement over the current Uno WebGPU path
route (7.44× by median), while software Skia remains 1.48× faster. The image
result is 1.97× faster than Uno WebGPU and 2.43× slower than software Skia.
Both GPU scenarios contain one large ProGPU outlier and require paired
multi-process reruns plus GPU traces.

## Cross-framework comparison boundary

ProGPU's WinUI-compatible framework and Avalonia integration already expose
frame metrics, retained-scene cache counters, allocation/GC state, GPU upload,
render and compositor timing. Their host boundaries differ from Uno:

| Lane | Framework work inside frame | ProGPU entry | Presentation |
|---|---|---|---|
| Uno | Uno layout/composition records drawing SPI | retained `GpuPicture` replay | borrowed Uno WebGPU target |
| ProGPU WinUI-compatible framework | its own layout and semantic scene publication | native semantic scene/compositor | provider-owned window target |
| Avalonia + ProGPU | Avalonia retained composition and platform scheduling | ProGPU render backend/resource cache | Avalonia external-GPU target lease |

Existing application measurements from those lanes are not numerically mixed
into this table because they do not run the same semantic scene or timing
boundary. Doing so would compare UI workloads and schedulers while labeling
the result as renderer cost. The standard port requires the same counts, seed,
target, final-state hash, completion wait, warmup/sampling policy, and balanced
fresh-process order defined in [performance-benchmarks.md](performance-benchmarks.md).

## Exact diagnostic command

Run once for every backend/scenario pair:

```bash
cd src
dotnet run \
  --project Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release -- \
  --backend progpu \
  --scenario cached \
  --warmups 4 \
  --samples 12 \
  --output ../docs/progpu-uno-backend/artifacts/progpu-cached-diagnostic.json
```

Valid backends are `progpu`, `webgpu`, and `skia`; current scenarios are
`cached`, `sparse`, `text`, `paths`, and `images`.

## Next publishable run

1. Add GPU/readback pixel hashes and per-channel difference metrics to the
   schema, then reject mismatched outputs before reporting time.
2. Run at least eight balanced fresh-process triplets with at least 100 samples
   per steady scenario.
3. Add cold start, first frame, effects, control density, scrolling, and settled
   memory scenarios.
4. Capture .NET allocation/CPU traces and Metal System Trace for the fixed
   overhead and ProGPU outliers.
5. Port the exact renderer scorecard scene to the WinUI-compatible and Avalonia
   hosts; report framework time and ProGPU compositor time separately.
