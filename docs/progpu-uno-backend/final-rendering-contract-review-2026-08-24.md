# Final review of Uno drawing contracts and GPU backends

Date: 2026-08-24

Review branch: `feat/progpu-drawing-backend`

Reviewed Uno commit: `5bcd1888cfbd54feb7a1aa71fd7a32b642cdbbd9`

Reviewed ProGPU submodule: `93ca8d1170a8911cf5b4f94b6c380663cea48f9f`

Upstream contract work: [Uno Platform PR #24153](https://github.com/unoplatform/uno/pull/24153)

## Executive conclusion

Uno's new drawing abstraction is a viable foundation for independent GPU drawing backends. It successfully separates host context negotiation, backend resource creation, neutral geometry/font/image content, retained recording, and typed presentation. ProGPU integrates without modifying the Skia or built-in WebGPU implementations and uses the complete ProGPU drawing, geometry, text, font, image, SVG, effects, retained-scene, and WebGPU stack.

The abstraction is not ready to be treated as a stable third-party backend contract without another lifecycle and conformance pass. The most important findings are:

1. **ProGPU is the fastest correct backend in this macOS/Metal matrix.** Across all 15 forced-redraw scenarios, its synchronized frame median is geometrically `1.936x` faster than Skia/Metal and `8.806x` faster than the built-in WebGPU backend. Its completed-batch throughput is geometrically `5.544x` faster than Skia/Metal and `14.419x` faster than built-in WebGPU. ProGPU wins every individual scenario at both boundaries.
2. **The built-in WebGPU backend is not conformant for three measured scenarios.** `blend-layers`, `blend-corpus`, and `clips` have large output errors. The raw `clips` timing that appears faster than Skia is invalid as a performance win because it renders different pixels.
3. **Backend identity and ownership are still partly process-global.** `IDrawingSession.Factory` is the correct per-session owner, but several composition/resource paths still call `DrawingFactory.Current`. This can create resources from one device/backend and replay them into another window's device/backend.
4. **Opaque resources have an incomplete lifetime contract.** `IShader` and `IColorFilter` are not disposable, even though the Skia implementations wrap disposable native `SKShader` and `SKColorFilter` objects. Raw WebGPU handles and `NativeSurface` also have no explicit borrow scope, ABI identity, device generation, or thread domain.
5. **Capabilities are implicit.** A backend can silently reduce an operation to a different one while still reporting success. `CreateEffectFilter` returning `null` is the only systematic optional-capability signal. A common capability manifest, diagnostics contract, and backend-neutral conformance suite are required.
6. **Custom shaders and custom visual effects need opt-in feature contracts, not more methods on the base drawing interface.** The proposed design below uses scoped leases, device-owned disposable resources, immutable shader/effect descriptors, explicit fallback, and bounds/damage metadata.
7. **Browser WebGPU should not require a native WebGPU ABI.** ProGPU already proves a managed .NET/Wasm renderer can send a coarse, versioned command stream directly to JavaScript `navigator.gpu`, with no wgpu-native, Dawn native library, Emscripten WebGPU C bridge, or per-draw JS interop. Uno's async browser host and pluggable drawing seams are close to supporting the same model, but `IWebGpuDeviceContext` and `IWebGpuRenderTarget` currently assume native-style `nint` handles.

The recommended release gate is: fix the two built-in WebGPU correctness defects, remove global factory usage from device-bound composition paths, define resource/lease ownership, and run a shared pixel conformance suite against every registered backend. Performance work should follow those correctness gates.

## Scope and evidence

This review covers:

- the drawing/context contracts introduced by PR #24153;
- the Skia, built-in WebGPU, and external ProGPU implementations;
- host negotiation and per-window presentation on macOS;
- neutral geometry, text/font, image, SVG, retained-record, layers, clips, blend modes, shadows, and effects;
- device/resource ownership, borrowing, disposal, device loss, and extension points;
- a fresh 15-scenario, three-backend GPU benchmark matrix;
- integration lessons from the ProGPU WinUI framework, the ProGPU Avalonia backend, a C++ UI integration, and LibreWPF/WPF integration.

It does not claim cross-platform qualification. The measured platform is macOS/Metal. It also does not treat a matching semantic-input hash as proof of rendering correctness; final pixels were read back and compared independently.

Browser architecture was reviewed from source but was not included in the macOS benchmark matrix. The browser proposal below therefore separates code-review assessment from runtime evidence and defines its own required qualification gates.

## Contract architecture assessment

### What is strong

The new design has five useful boundaries:

| Boundary | Contract | Assessment |
|---|---|---|
| Host negotiation | `IGraphicsProvider`, `IGraphicsProvider<TContext>`, `GraphicsContextKind` | Good typed narrowing. A backend cannot win a context kind unless it implements the matching typed factory. |
| Device-bound drawing | `IDrawingFactory`, `IDrawingFactory<TTarget>` | Correctly keeps resource creation and presentation on the backend/device side. |
| Frame recording | `ICommandRecorder`, `IRenderRecord`, `IDrawingSession`, `IPresentSession` | Supports both native retained objects and neutral command fallback without prescribing an implementation. |
| Neutral content | `IGeometry`, `IFont`, `IImage`, SVG/Lottie seams | Lets a non-Skia renderer consume the UI framework without a CPU bitmap bridge. Curves, glyphs, color glyphs, and image pixels can cross the boundary. |
| Typed presentation | `IWebGpuRenderTarget`, `IMetalRenderTarget`, other typed targets | Prevents a backend from type-switching an untyped render target after negotiation. |

This division is materially better for a third-party renderer than a contract built around `SKCanvas`, `DrawingContext`, `DrawingVisual`, or another framework-native surface. ProGPU could keep its native retained scene, glyph atlases, analytic vector renderer, effect graph, render bundles, and WebGPU resource caches rather than emulate Skia.

The abstraction also learned the right lesson from other UI integrations: framework layout and composition should emit semantic drawing operations, while the renderer owns the most efficient realization. The C++ UI and WPF integrations needed a larger translation layer because their drawing objects and lifetime rules are framework-specific. Avalonia's custom-rendering seams provide useful native interop, but integrating an entire renderer still requires coordination across platform graphics, glyphs, images, and compositor lifetime. Uno's explicit, independently registered content seams make a complete renderer replacement clearer.

### Current contract risks

#### 1. Process-global factory versus per-window factory

`IDrawingSession.Factory` documents that resources must be created through the factory owning the current session. That is the correct invariant:

> Every device-backed object consumed by a session must have been created by that session's factory and device generation.

The implementation does not consistently maintain it. Composition paths still use `DrawingFactory.Current`, including gradient brushes, nine-grid rendering, surface/effect brushes, acrylic, shadow filters, alpha masks, image surfaces, effect graph parsing, SVG/image sources, glyph rendering, and visual recording.

This is more than a style issue. `GraphicsRegistry.Initialize` installs every successful window's backend in a process-global `DrawingFactory.Current`. With multiple windows or devices, the latest initialized window can replace the value while existing visuals keep resources cached from an earlier factory. `IRenderRecord.Replay` meanwhile states that native records are backend-bound. The result can be a foreign-resource failure, stale device use, or accidental retention of a closed device.

Recommended invariant:

- painting code uses `session.Factory` exclusively;
- persistent resources are cached by `(backend owner identity, device generation, content revision)`;
- visual recording receives its owning factory explicitly rather than looking it up globally;
- `DrawingFactory.Current` remains only a compatibility/bootstrap facility and is not used by composition hot paths.

#### 2. Incomplete resource disposal

`IGeometry`, `ITexture`, `IEffectFilter`, `IRenderRecord`, contexts, targets, and present sessions carry explicit disposal. `IShader` and `IColorFilter` do not. The Skia wrappers directly hold `SKShader` and `SKColorFilter`, both native disposable resources, but cannot expose deterministic cleanup through the interface.

This should be corrected before the contracts stabilize. The preferred shape is a common device resource base:

```csharp
public interface IGraphicsResource : IDisposable
{
	GraphicsResourceOwner Owner { get; }
	ulong DeviceGeneration { get; }
}

public interface IShader : IGraphicsResource { }
public interface IColorFilter : IGraphicsResource { }
public interface IEffectFilter : IGraphicsResource { }
public interface ITexture : IGraphicsResource { }
public interface IRenderRecord : IGraphicsResource { }
```

If adding `IDisposable` immediately is too breaking, introduce `IShader2`/`IColorFilter2` or a capability-provided resource owner, obsolete the old interfaces, and update composition caches first. Relying on finalizers is not sufficient for high-frequency GPU/native resource churn.

#### 3. Backend and device lifetime at window close

Hosts generally retain one context and one renderer per window, which is correct. The macOS host close callback currently unregisters the window and raises `Closed`, but the reviewed path does not directly dispose `_renderer` or `_context`. Other hosts have explicit renderer/context disposal paths. Backend and context disposal must be an ordered, cross-host invariant:

1. stop frame callbacks and prevent new `BeginPresent` calls;
2. dispose per-window composition records and resources;
3. drain or cancel backend work according to the completion contract;
4. dispose the drawing factory/backend;
5. release the host context/device and swapchain;
6. invalidate the device generation so late resource use fails deterministically.

The host audit should be completed for every platform and covered by repeated open/close and multi-window tests. This review identifies the macOS gap by source inspection; it does not claim a measured leak.

#### 4. Raw WebGPU handles are underspecified

`IWebGpuDeviceContext` exposes `Instance`, `Adapter`, `Device`, and `Queue` as `nint`; `IWebGpuRenderTarget` exposes a per-frame `ColorView`. This keeps a particular binding library out of the contract, but it does not identify:

- whether the ABI is Dawn, wgpu-native, or another implementation;
- the header/API revision and descriptor layout;
- whether every handle is borrowed or owned;
- the thread/queue on which it may be used;
- enabled features and limits;
- device-loss generation;
- the exact lifetime of the target view;
- how GPU completion is requested without calling implementation-specific polling functions.

The ProGPU integration could not safely reinterpret modern Uno WebGPU descriptors as another ABI. It required a descriptor translation adapter and an explicit borrowed-lifetime implementation. That is evidence that raw pointer equality is not an interoperability contract.

#### 5. Capabilities and fallback are implicit

`CreateEffectFilter` can return `null`, but most operations are mandatory and have no capability query. A backend can ignore an argument or silently substitute a cheaper operation. That happened in the built-in WebGPU backend while `UnsupportedOperations` still remained zero.

Required additions:

- immutable capability manifest attached to the factory/context;
- support levels such as `Native`, `Emulated`, `Fallback`, and `Unsupported`;
- per-feature limits such as maximum samples, texture size, gradient stops, layer blend modes, custom shader languages, and effect graph features;
- structured diagnostics for every fallback or unsupported operation;
- a strict mode that throws during qualification instead of degrading;
- a conformance version so a provider states which contract suite it passes.

#### 6. Retained record compatibility is documented but not enforceable

`IRenderRecord.Replay` says native records only work with sessions from their producing backend. The type system does not enforce this, and the record has no owner identity or device generation. The downcast failure occurs late at replay.

Add owner metadata and a cheap compatibility check. A record should also expose immutable hints useful to the compositor: content revision, logical bounds, damage/outset bounds, whether it depends on backdrop/time, and whether it is safe to cache or compile into a native bundle.

## Implementation review

### Skia/Metal

The Skia backend creates/reuses a Metal-backed `GRContext`, wraps each host texture in an `SKSurface`, records visual content as `SKPicture`, and replays through native `SKCanvas`. `IDrawingSession.NativeSurface` exposes the live canvas for legacy/custom drawing.

Strengths:

- mature and broadly conformant drawing semantics;
- efficient native display-list recording and replay;
- strong geometry, text, filter, blend, and clip coverage;
- no per-verb WebGPU FFI translation in managed code;
- stable baseline pixels for this qualification.

Limits relevant to the contract:

- native-surface escape couples custom rendering to Skia and lacks a scope lease;
- shader and color-filter wrappers have no deterministic disposal contract;
- native `SKPicture` records remain factory/device-bound despite the untyped replay API;
- global factory use can mix devices in multi-window configurations.

### Built-in WebGPU

The built-in backend records managed `WebGpuCommand` objects, lowers/coalesces them at presentation, creates WebGPU buffers/uniforms/bind groups, and submits direct render operations. It includes caches and some retained-bundle replay, but present-time command lowering and WebGPU object churn remain substantial.

Correctness defects found by the benchmark and confirmed by source inspection:

1. `SaveLayer(BlendMode)` maps only `DstIn` to a special composite pipeline; all other blend modes are mapped to ordinary source-over. The 27-value `BlendMode` contract is therefore silently collapsed to two behaviors.
2. `ClipRect` ignores `ClipOperation` and always tightens the scissor. `ClipPath` calls it before setting the path-exclusion flag, so a `Difference` path incorrectly restricts rendering to the excluded path bounds instead of preserving the area outside it.

Performance structure observed in the reviewed implementation:

- layers allocate/render full offscreen surfaces rather than using supplied tight bounds;
- buffers, uniforms, and bind groups are often created during presentation;
- text and paths pay present-time managed traversal, conversion, tessellation, and FFI costs;
- shadows require coverage, blur, and composite passes and are the slowest measured case;
- the implementation comments document that one render-bundle route was slower with the selected wgpu-native path, so direct replay is retained for parts of the pipeline.

These performance results should not be generalized to WebGPU itself. They describe this implementation and its current resource/encoding strategy on wgpu-native/Metal.

### ProGPU/WebGPU

ProGPU receives the same host-created WebGPU instance/adapter/device/queue and renders directly into the borrowed host texture view. `UnoModernWebGpuApi` translates the host ABI into ProGPU's expected operations; `UnoBorrowedWebGpuLifetime` prevents ProGPU from releasing host-owned handles.

The integration uses ProGPU's complete stack:

- retained `GpuPicture` recordings and scene compilation;
- cached render bundles and scene/resource identity;
- analytic vector paths and difference clips;
- ProGPU shaping/font resolution, glyph outlines, color glyphs, and glyph atlases;
- device-resident textures, gradients, images, masks, layers, and effect surfaces;
- neutral effect-tree translation and GPU-only backdrop capture;
- bounded completion polling and bounded cache/resource retirement.

The large throughput lead is consistent with this retained design: stable scenes reuse compiled GPU work, text reuses atlases, and unchanged resource identity avoids reconstructing the entire WebGPU submission graph. The backend still performs real redraw/submission in this benchmark; `--force-redraw` prevents populated-target reuse from turning the measurement into a no-op.

Known qualification limits remain:

- device-loss recovery is designed but not fully runtime-qualified across all Uno hosts;
- custom user shader/effect injection is not yet exposed by Uno's contracts;
- exact pixels can differ from Skia for antialiasing, gradient interpolation, clipping edges, shadows, and blur kernels;
- cross-platform WebGPU backends and non-WebGPU ProGPU providers are outside this run.

Every ProGPU optimization used by this integration is subject to the project rule that managed C# and native C++ implementations receive the same applicable optimization. The reviewed ProGPU revision includes matched native work for render-bundle replay, retained caches, completion/lifetime synchronization, effect reuse, draw-call merging, and related gates. A future Uno-specific optimization must include the same C++ applicability audit, implementation where applicable, and parity tests before its performance result is accepted.

## Three-backend benchmark

### Environment

| Item | Value |
|---|---|
| Machine | Apple M3 Pro, 11 CPU cores (5 performance + 6 efficiency), 14 GPU cores |
| Memory | 18 GB unified memory |
| OS | macOS 26.6, arm64 |
| Graphics API | Metal 4; WebGPU forced to Metal |
| Display | Built-in 3024 × 1964 |
| Power | AC power, battery fully charged |
| .NET | SDK 10.0.201, runtime 10.0.5, MSBuild 18.3 |
| Benchmark build | Release, no debugger |

Hardware serials and other unique identifiers are intentionally omitted.

### Protocol

- Backends: `skia-metal`, `webgpu`, and `progpu`.
- Fifteen scenarios: `cached`, `sparse`, `text`, `paths`, `strokes`, `materials`, `layers`, `isolation-layers`, `mask-layers`, `blend-layers`, `blend-corpus`, `images`, `clips`, `shadows`, and `effects`.
- Three independent fresh processes per backend/scenario.
- Latin-square order to reduce order and thermal bias:
  - repetition 1: Skia, built-in WebGPU, ProGPU;
  - repetition 2: ProGPU, Skia, built-in WebGPU;
  - repetition 3: built-in WebGPU, ProGPU, Skia.
- Six warm-up frames, 100 synchronized samples, 60 frames per batch, seven completed batches.
- Resolution: 1280 × 720 BGRA8888 premultiplied.
- `--force-redraw` enabled.
- `UNO_WEBGPU_BACKENDS=metal` set for both WebGPU implementations.
- Every timed sample includes an explicit GPU-completion wait. The batch metric amortizes completion over 60 submitted frames.
- Reported aggregate: median of the three independent process medians. Reported p95: median of the three process p95 values.
- First-repetition pixels captured for all backend/scenario pairs. All three repetitions retain a final pixel SHA-256.

The synchronized total is a latency/serialization boundary, not normal application throughput. It deliberately prevents CPU submission from being mistaken for completed GPU work. `Batched total` is the better sustained-throughput comparison because it amortizes explicit completion.

### Reproduction

Build once:

```bash
cd src
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1
```

Run one backend/scenario in a fresh process:

```bash
UNO_WEBGPU_BACKENDS=metal dotnet run \
  --project Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --backend progpu --scenario text \
  --warmups 6 --samples 100 --batch-size 60 --batches 7 \
  --force-redraw \
  --output artifacts/performance/run.json \
  --pixels-output artifacts/performance/run.bgra
```

Replace `progpu` with `skia-metal` or `webgpu`, and replace `text` with each scenario. Use the Latin-square process order above. The complete local artifact set is in:

```text
src/artifacts/performance/2026-08-24-three-backend-final-review/
```

It contains 135 schema-valid v4 JSON files, 45 first-repetition BGRA captures, and a local visual contact sheet. Raw artifacts are intentionally not committed to the source tree.

### Synchronized latency results

All values are milliseconds per completed frame. `Skia/ProGPU` and `WebGPU/ProGPU` are speedups, so values above 1 favor ProGPU. `Skia/WebGPU` above 1 favors built-in WebGPU.

| Scenario | Skia/Metal median | WebGPU median | ProGPU median | Skia/WebGPU | Skia/ProGPU | WebGPU/ProGPU |
|---|---:|---:|---:|---:|---:|---:|
| Cached | 0.5132 | 1.6187 | 0.4452 | 0.32x | 1.15x | 3.64x |
| Sparse | 0.5297 | 1.6587 | 0.5237 | 0.32x | 1.01x | 3.17x |
| Text | 0.7732 | 12.4981 | 0.4020 | 0.06x | 1.92x | 31.09x |
| Paths | 0.9744 | 15.3925 | 0.5287 | 0.06x | 1.84x | 29.11x |
| Strokes | 2.4801 | 16.9443 | 0.7107 | 0.15x | 3.49x | 23.84x |
| Materials | 1.4878 | 7.5113 | 0.6162 | 0.20x | 2.41x | 12.19x |
| Layers | 1.2110 | 4.8365 | 0.6785 | 0.25x | 1.78x | 7.13x |
| Isolation layers | 1.1781 | 4.3724 | 0.6557 | 0.27x | 1.80x | 6.67x |
| Mask layers | 1.1187 | 2.5137 | 0.6617 | 0.45x | 1.69x | 3.80x |
| Blend layers | 1.2345 | 4.4597 | 0.6477 | 0.28x | 1.91x | 6.89x |
| Blend corpus | 4.1899 | 5.9901 | 0.5693 | 0.70x | 7.36x | 10.52x |
| Images | 0.6322 | 4.1289 | 0.5844 | 0.15x | 1.08x | 7.07x |
| Clips | 2.1445 | 1.9864 | 0.9371 | 1.08x | 2.29x | 2.12x |
| Shadows | 0.7668 | 40.9408 | 0.5918 | 0.02x | 1.30x | 69.18x |
| Effects | 6.0715 | 9.2367 | 2.5554 | 0.66x | 2.38x | 3.61x |
| **Geometric mean** | — | — | — | **0.220x** | **1.936x** | **8.806x** |

The built-in WebGPU `clips` row is not a valid win because its output is incorrect. Built-in WebGPU loses the other 14 raw synchronized comparisons. ProGPU wins all 15 comparisons against both alternatives.

### Completed-batch throughput results

Values are milliseconds per frame after submitting 60 frames and waiting for batch completion.

| Scenario | Skia/Metal | WebGPU | ProGPU | Skia/WebGPU | Skia/ProGPU | WebGPU/ProGPU |
|---|---:|---:|---:|---:|---:|---:|
| Cached | 0.247222 | 0.128478 | 0.056517 | 1.92x | 4.37x | 2.27x |
| Sparse | 0.247867 | 0.148262 | 0.122568 | 1.67x | 2.02x | 1.21x |
| Text | 0.450765 | 9.366622 | 0.082443 | 0.05x | 5.47x | 113.61x |
| Paths | 0.661650 | 10.989025 | 0.106123 | 0.06x | 6.23x | 103.55x |
| Strokes | 1.840735 | 12.439647 | 0.377808 | 0.15x | 4.87x | 32.93x |
| Materials | 1.170468 | 4.556002 | 0.204917 | 0.26x | 5.71x | 22.23x |
| Layers | 0.817608 | 1.289038 | 0.105277 | 0.63x | 7.77x | 12.24x |
| Isolation layers | 0.812137 | 1.320398 | 0.109735 | 0.62x | 7.40x | 12.03x |
| Mask layers | 0.758742 | 0.770238 | 0.105465 | 0.99x | 7.19x | 7.30x |
| Blend layers | 0.819080 | 1.317918 | 0.104843 | 0.62x | 7.81x | 12.57x |
| Blend corpus | 2.565452 | 2.857515 | 0.082753 | 0.90x | 31.00x | 34.53x |
| Images | 0.354670 | 2.655157 | 0.088378 | 0.13x | 4.01x | 30.04x |
| Clips | 1.459708 | 0.497342 | 0.237707 | 2.94x | 6.14x | 2.09x |
| Shadows | 0.451363 | 22.645508 | 0.227348 | 0.02x | 1.99x | 99.61x |
| Effects | 3.880593 | 3.886115 | 1.111512 | 1.00x | 3.49x | 3.50x |
| **Geometric mean** | — | — | — | **0.384x** | **5.544x** | **14.419x** |

Built-in WebGPU has three raw batch wins over Skia (`cached`, `sparse`, and `clips`), but `clips` is incorrect. ProGPU wins all 15 completed-batch comparisons.

### Tail latency

Median-of-process p95 synchronized latency, in milliseconds:

| Scenario | Skia/Metal p95 | WebGPU p95 | ProGPU p95 |
|---|---:|---:|---:|
| Cached | 0.8812 | 1.6637 | 0.9225 |
| Sparse | 0.9543 | 1.7243 | 0.8882 |
| Text | 1.2897 | 14.0007 | 0.7261 |
| Paths | 1.4547 | 26.8049 | 0.9592 |
| Strokes | 3.0438 | 27.7329 | 1.0213 |
| Materials | 1.8303 | 9.8004 | 1.1258 |
| Layers | 1.6565 | 5.5279 | 0.9838 |
| Isolation layers | 1.5571 | 5.4287 | 0.9397 |
| Mask layers | 1.4785 | 3.8886 | 0.9577 |
| Blend layers | 1.7147 | 5.4591 | 0.9675 |
| Blend corpus | 5.3484 | 6.3559 | 0.8459 |
| Images | 0.9512 | 5.6808 | 1.2467 |
| Clips | 2.5592 | 2.2533 | 1.2992 |
| Shadows | 1.1208 | 47.6294 | 0.8803 |
| Effects | 6.9757 | 12.1277 | 3.8695 |

At the synchronized boundary, built-in WebGPU exceeds a 16.67 ms frame budget at the median for strokes and shadows, and at p95 for paths, strokes, and shadows. Neither Skia nor ProGPU exceeds it in this matrix.

Across scenarios, the median coefficient of variation of the three independent process medians was 2.72%/0.70% for Skia synchronized/batch, 0.71%/1.26% for built-in WebGPU, and 1.97%/1.36% for ProGPU. The largest single synchronized variation was ProGPU text at 20.51%; taking the median of three processes limits its influence, but more repetitions are required before treating small differences such as sparse as a release gate.

## Pixel qualification

All 135 JSON files report GPU execution, the expected graphics API and Apple M3 Pro adapter, a non-null GPU completion wait, stable semantic-input hashes, and stable per-backend pixel hashes across repetitions. The v4 JSON files pass the repository JSON schema.

That metadata did not catch unsupported operations in built-in WebGPU because the benchmark's `UnsupportedOperations` counter is currently wired only to ProGPU diagnostics. Pixel comparison is therefore the decisive gate.

First-repetition BGRA captures were compared at 1280 × 720. The table reports RGB mean absolute error and the percentage of pixels whose maximum RGB channel error exceeds 32, using Skia as the visual reference. Differences near antialiased edges, gradient interpolation, and blur kernels are expected; large scene-wide differences are not.

| Scenario | ProGPU RGB MAE | ProGPU pixels >32 | WebGPU RGB MAE | WebGPU pixels >32 | Qualification |
|---|---:|---:|---:|---:|---|
| Cached | 0.000 | 0.000% | 0.000 | 0.000% | Both exact |
| Sparse | 0.000 | 0.000% | 0.000 | 0.000% | Both exact |
| Text | 0.910 | 1.098% | 1.332 | 2.764% | Qualified implementation-specific rasterization |
| Paths | 0.357 | 0.139% | 0.503 | 0.899% | Qualified edge rasterization differences |
| Strokes | 0.675 | 0.801% | 0.780 | 0.876% | Qualified edge rasterization differences |
| Materials | 2.203 | 0.013% | 2.194 | 0.002% | Qualified gradient interpolation differences |
| Layers | 0.070 | 0.000% | 0.926 | 0.000% | Qualified |
| Isolation layers | 0.018 | 0.000% | 0.533 | 0.000% | Qualified |
| Mask layers | 0.039 | 0.000% | 0.395 | 0.000% | Qualified |
| Blend layers | 0.022 | 0.000% | 39.230 | 65.413% | **WebGPU fail** |
| Blend corpus | 0.062 | 0.000% | 26.613 | 45.356% | **WebGPU fail** |
| Images | 0.115 | 0.000% | 0.115 | 0.000% | Qualified one-level sampling difference |
| Clips | 0.455 | 0.106% | 28.526 | 74.443% | **WebGPU fail** |
| Shadows | 0.357 | 0.026% | 0.647 | 0.569% | Qualified shadow-kernel differences |
| Effects | 3.474 | 0.604% | 1.473 | 0.013% | Qualified; blur kernels differ |

ProGPU preserves alpha exactly in every scenario. Built-in WebGPU also preserves alpha except for the blend corpus, where alpha MAE is 4.121. ProGPU is therefore correctness-qualified for all measured scenarios relative to the reference tolerances. Built-in WebGPU is qualified for 12 of 15.

The exact correctness thresholds should become scenario-specific golden/tolerance rules in a shared conformance harness. Skia is a useful reference implementation here, not a specification that requires bit-identical rasterization.

## Performance interpretation

The benchmark supports the following conclusions:

- ProGPU's retained scene and render-bundle design materially reduces both synchronized latency and sustained completed-GPU cost.
- Text and paths are the largest differentiators against built-in WebGPU, consistent with atlas/geometry reuse versus repeated lowering and resource encoding.
- Built-in WebGPU shadows need focused pass/resource profiling; 40.94 ms synchronized and 22.65 ms batched are not viable for interactive use at this workload.
- Skia remains a strong, correct general baseline. ProGPU's synchronized advantage over Skia ranges from only 1.01x in sparse content to 7.36x in the blend corpus, so scenario selection matters.
- ProGPU's larger completed-batch wins than synchronized wins show that its submission/reuse architecture is especially effective when normal GPU pipelining is allowed.
- The built-in WebGPU `cached` and `sparse` batch wins over Skia show that WebGPU submission can be competitive for simple work, but the current complex primitive, text, layer, and shadow paths dominate broader workloads.
- No performance claim should include a scenario whose pixels fail conformance.

This is not a complete application benchmark. It excludes layout, input, accessibility, data binding, application logic, compositor scheduling jitter, display-vsync latency, and memory-pressure behavior. Gallery and runtime-smoke execution prove integration, while this harness isolates rendering work.

## Proposed contract improvements

### Priority 0: correctness and conformance

1. Fix built-in WebGPU layer blending so every declared `BlendMode` is either implemented correctly or explicitly rejected/fallback-routed.
2. Fix `ClipRect`/`ClipPath` difference behavior so the scissor is not tightened to an excluded region.
3. Move the all-blend-mode, intersect/difference clip, nested layer, mask, effects, text, path, image, and resource-lifetime tests into a backend-neutral conformance project. Run the same test vectors against Skia, built-in WebGPU, and ProGPU.
4. Make unsupported-operation and fallback diagnostics part of the Uno contract rather than a ProGPU-only counter. Qualification mode must fail on any silent degradation.
5. Gate benchmark comparisons on pixel qualification before calculating a winner.

### Priority 0: owner and generation correctness

1. Remove `DrawingFactory.Current` from composition painting and cached-resource creation. Use `IDrawingSession.Factory` or an explicitly captured factory.
2. Add stable backend-owner and device-generation identity to device resources and retained records.
3. Reject foreign or stale resources at the API boundary with a deterministic diagnostic.
4. Define and test renderer-before-context disposal for every host, including repeated window open/close and independent multi-window devices.
5. Invalidate all records, textures, shaders, filters, atlases, and pending readbacks as one device-loss generation.

### Priority 1: deterministic resource ownership

1. Make shaders and color filters disposable device resources.
2. Document whether factory disposal waits, cancels, or detaches outstanding work.
3. Add cancellation to asynchronous readback/snapshot operations.
4. Define whether resource disposal is thread-safe and which thread performs final native release.
5. Add cache budget/eviction signals so the compositor can respond to memory pressure without backend-specific casts.

### Priority 1: capability discovery

Prefer an optional feature query over continually widening `IDrawingFactory`:

```csharp
public interface IGraphicsFeatureProvider
{
	GraphicsCapabilities Capabilities { get; }
	bool TryGetFeature<TFeature>(out TFeature feature) where TFeature : class;
}
```

`GraphicsCapabilities` should be immutable and include a conformance version, supported context/pixel/color-space modes, layer blend modes, clip operations, effect nodes, custom shader languages/stages, maximum sizes/counts, presentation features, and support levels. Feature objects remain device-owned and generation-bound.

### Priority 1: public ordered fallback registration

`GraphicsRegistry` already stores an ordered provider list internally, but the public builder extension registers one provider. Expose ordered registration so an application can request, for example, ProGPU first and Skia second without duplicating internal negotiation logic:

```csharp
builder.GraphicsBackends(
	new ProGpuGraphicsProvider(options),
	new SkiaGraphicsProvider());
```

The diagnostic result should report every attempted provider/context and why it declined. Fallback after a device is already active is a separate device-loss/recreation operation and must not silently reuse resources from the old owner.

### Priority 1: browser-native WebGPU transport

Do not require browser providers to manufacture C ABI pointers. Add an async browser surface/provider binding that lets a backend own a JavaScript `navigator.gpu` device and an opaque command transport while Uno continues to own the canvas identity, frame scheduling, size/DPI, native-element overlay, and application lifetime. The full design is in the dedicated browser section below.

### Priority 2: performance-oriented recording data

The base drawing verbs are appropriately simple, but several optional hints would allow high-performance backends to avoid rediscovering information:

- add optional tight bounds to `SaveLayer`, plus filter/effect outset and whether backdrop is required; full-target temporary textures should not be necessary for a small layer;
- attach logical bounds, damage bounds, content revision, backdrop/time dependence, and cacheability to retained records;
- provide immutable descriptor/resource identity so equal gradients, filters, geometry, and glyph runs can be cached without hashing mutable arrays every frame;
- add batch-oriented optional feature interfaces using spans or immutable command packets for repeated primitives, glyphs, and images;
- expose presentation load/store/preserve intent and damage rather than forcing each backend to infer it;
- expose a backend-neutral completion token/fence service so performance tools and resource retirement do not call implementation-specific device polling;
- keep `IGeometry.StreamSegments` as the interoperable fallback, but allow an owner-compatible typed geometry feature to avoid repeated neutral streaming;
- add an optional glyph-run feature that preserves font/glyph identity and positions so atlas backends do not have to reconstruct a run from merged outline objects.

These must remain optional accelerators. The semantic drawing contract and its conformance tests stay authoritative.

## Browser integration without native graphics dependencies

### What ProGPU already proves

`ProGPU.Browser` is a managed .NET/Wasm WebGPU provider over browser JavaScript. The ordinary ProGPU retained renderer targets its existing typed `IWebGpuApi` interface. Desktop installs a native API implementation; the browser installs `BrowserWebGpuApi`. Renderer, scene, text/font, atlas, geometry, effect, image, and WGSL code do not branch into a browser-specific renderer.

The browser provider does not load or link wgpu-native, a Dawn native library, Emdawnwebgpu, a WebGPU C shim, Skia, or another native graphics engine. The deployable application still contains the normal .NET WebAssembly runtime, but the graphics dependency is the browser's own `navigator.gpu` implementation.

Its architecture is:

```text
Uno/ProGPU managed visual tree and text
                |
                v
       ProGPU retained renderer
                |
                v
     IWebGpuApi (typed managed seam)
                |
                v
 BrowserWebGpuApi command encoder
     one coarse packet per frame
                |
       .NETJSImport boundary
                |
                v
 JavaScript packet decoder/resource table
                |
                v
 navigator.gpu device/queue/canvas context
```

Important implementation properties:

- packets use a 16-byte `PGPU` header, a protocol version, byte length, command count, eight-byte command headers, and eight-byte alignment;
- commands and upload payloads are little-endian binary, not JSON;
- the managed encoder reuses unmanaged Wasm linear memory;
- JavaScript reads the current linear-memory view, so steady-state drawing does not marshal one object or invoke JavaScript once per draw;
- WebGPU objects are represented by 20-bit table indices plus 12-bit generations, so stale released handles are rejected;
- WGSL source is transported directly and compiled by `GPUDevice.createShaderModule`;
- the JavaScript side owns `GPUAdapter`, `GPUDevice`, `GPUQueue`, `GPUCanvasContext`, `configure`, and `getCurrentTexture`;
- all packets produced for a managed frame are coalesced into one task, preserving current-texture validity across internal submissions;
- main-thread, `OffscreenCanvas` worker, and cross-origin-isolated shared-memory worker modes are available;
- mapped-buffer completion and queue completion stay asynchronous through `mapAsync` and `queue.onSubmittedWorkDone`;
- device loss reports diagnostics and reconstructs the application from retained CPU-side state;
- capability selection is feature-based, including portable versus optional-feature profiles, rather than user-agent-based.

This is the right browser model for ProGPU because the renderer already owns a stable WebGPU operation seam. The packet protocol substitutes transport for direct native calls; it does not substitute a second renderer.

### What Uno currently does

Uno's new browser host already has several necessary pieces:

- `BrowserRenderer` initializes graphics asynchronously, skips/re-arms frames while device creation is pending, and installs a per-window drawing factory;
- the app-registered provider still selects the renderer, while the host creates the requested graphics context;
- frame scheduling uses the browser render loop and the composition target remains responsible for native-element clipping;
- the browser context owns canvas acquisition/presentation and presents when control returns to the event loop;
- `SnapshotAsync` respects asynchronous browser buffer mapping.

The current WebGPU context, however, is built around the native WebGPU C ABI even in the browser:

1. JavaScript calls `navigator.gpu.requestAdapter()` and `requestDevice()`.
2. The device is imported into Emdawnwebgpu's C handle table.
3. Managed drawing invokes generated `wgpu*` P/Invoke bindings.
4. `wgpu-wasm.targets` downloads and links the pinned Emdawnwebgpu port and native compatibility stubs into the Wasm application.
5. `IWebGpuDeviceContext` exposes the resulting instance/device/queue pointers as `nint`, and `IWebGpuRenderTarget` exposes a raw color-view pointer.

This is a real browser-WebGPU implementation and it avoids a native desktop dynamic library, but it is not the dependency-free JavaScript transport used by `ProGPU.Browser`. It still requires a native Wasm/C bridge, an exact WebGPU header ABI, build-time Emscripten linking, C stubs for unavailable symbols, and handle-table import patches.

Passing those Emdawn pointers to ProGPU would also force ProGPU to adopt Uno's pinned C ABI or add another adapter. It would not reuse ProGPU's already-qualified browser transport and would make browser deployment depend on native relinking.

### Contract mismatch

The core mismatch is not in `IDrawingSession`. The drawing operations and neutral content seams work in a browser. The mismatch is at context and target ownership:

- `GraphicsContextKind.WebGpu` is hard-mapped to `IGraphicsProvider<IWebGpuDeviceContext>`;
- that context assumes raw native-style handles exist before `CreateGraphics` runs;
- `IGraphicsProvider<T>.CreateGraphics` is synchronous, while direct `navigator.gpu` creation is asynchronous;
- `IWebGpuRenderTarget.ColorView` assumes a transferable pointer-like texture view;
- `GraphicsRegistry.CanPresent` requires `IDrawingFactory<IWebGpuRenderTarget>` for every WebGPU provider;
- the host currently owns/configures the WebGPU device/surface, while ProGPU's JavaScript transport needs its provider module to own the browser device/resource table;
- neither interface can describe a main-thread versus worker execution domain.

Inventing fake `nint` values is not a solution. ProGPU uses opaque pointer-shaped tokens internally only behind its own `IWebGpuApi`, where no browser code dereferences them and generation checks are enforced. Uno's public contract cannot assume another backend uses that exact token scheme.

### Recommended two-phase browser binding

Separate the platform surface from the GPU API device. Add a browser canvas context that is cheap and synchronous to create, then let an async provider bind a drawing factory/device to it:

```csharp
public interface IBrowserCanvasContext : IGraphicsContext
{
	string CanvasId { get; }
	float DevicePixelRatio { get; }
	BrowserFrameScheduler Scheduler { get; }
	BrowserExecutionCapabilities Execution { get; }
}

public interface IAsyncGraphicsProvider<in TContext> : IGraphicsProvider
	where TContext : IGraphicsContext
{
	ValueTask<IGraphicsBinding?> CreateGraphicsAsync(
		TContext context,
		CancellationToken cancellationToken = default);
}

public interface IGraphicsBinding : IAsyncDisposable
{
	IDrawingFactory DrawingFactory { get; }
	IGraphicsDeviceLifetime DeviceLifetime { get; }
	IRenderTargetProvider Targets { get; }
}
```

For a direct ProGPU browser provider:

1. Uno creates `IBrowserCanvasContext` with the canvas ID, size/DPI notifications, frame scheduler, and worker capabilities. It does not create a GPU device.
2. `ProGpuBrowserGraphicsProvider.CreateGraphicsAsync` loads its packaged JavaScript module and requests `navigator.gpu` asynchronously.
3. The provider creates `BrowserWebGpuApi`, ProGPU `WgpuContext`, compositor, drawing factory, and its JavaScript resource table as one device generation.
4. The binding exposes a logical per-frame target carrying size, damage, and a frame generation—not a texture-view pointer.
5. `BeginPresent` records/replays through ProGPU. Disposing the present session seals one frame packet. JavaScript obtains `context.getCurrentTexture()` only while decoding that frame and lets browser presentation occur implicitly.
6. Uno retains ownership of `requestAnimationFrame`, XAML invalidation, native-element overlay/clipping, and window/application lifetime.
7. The provider owns `GPUAdapter`/`GPUDevice`/`GPUQueue`/`GPUCanvasContext` configuration and recreates them after loss. It reports the new device generation to Uno.

The binding is two-phase because direct browser device creation cannot safely be hidden in today's synchronous `CreateGraphics` call. This also removes the need for the host to create a renderer-specific GPU device before knowing which provider accepted the surface.

### Backward-compatible feature shape

Native hosts and the current Emdawn path need not be removed. Introduce a general WebGPU context with feature discovery:

```csharp
public interface IWebGpuContext : IGraphicsContext, IGraphicsFeatureProvider
{
	WebGpuContextProfile Profile { get; }
}

public interface INativeWebGpuInteropFeature
{
	bool TryLeaseDevice(out WebGpuDeviceLease lease);
}

public interface IBrowserWebGpuHostFeature
{
	string CanvasId { get; }
	BrowserFrameScheduler Scheduler { get; }
	BrowserExecutionCapabilities Execution { get; }
}
```

Then:

- native built-in WebGPU and native ProGPU request `INativeWebGpuInteropFeature`;
- the existing Uno browser implementation may continue to request the native Emdawn feature;
- direct ProGPU browser requests `IBrowserWebGpuHostFeature` and creates its own JS transport;
- a future built-in Uno direct-JS backend can use its own transport without exposing ProGPU protocol types;
- `IWebGpuDeviceContext` remains as a compatibility interface until providers migrate.

Uno should standardize the canvas/frame/device-lifetime contract, not ProGPU's WebGPU opcode set. Each backend may package its own JavaScript module and protocol. This keeps the public Uno API small and avoids freezing a second WebGPU API surface that must track every browser WebGPU revision.

### Frame-target contract

Replace the assumption that all WebGPU targets expose a raw color view with a capability-based target:

```csharp
public interface IWebGpuFrameTarget : IRenderTarget, IGraphicsFeatureProvider
{
	ulong FrameGeneration { get; }
	RectInt32 Damage { get; }
	GraphicsLoadIntent LoadIntent { get; }
}
```

A native target offers a scoped `INativeWebGpuTextureViewFeature`. A direct-browser target offers a logical `IBrowserCanvasFrameFeature`; the backend's JavaScript module acquires the current texture during packet execution. Both expire with `IPresentSession.Dispose`. A target cannot be stored in a texture cache.

This design also resolves a browser correctness rule: `GPUCanvasContext.getCurrentTexture()` must not be acquired early and retained across asynchronous work or animation frames.

### Package and application shape

The intended application remains simple:

```csharp
builder.UseProGpuDrawingBackend();
```

On browser-Wasm, the package selects `ProGpuBrowserGraphicsProvider` and contributes its JavaScript module as a static web asset. On native targets, it selects the existing raw-handle provider. No application script registration, native WebGPU package, Emscripten port, or manual `DllImport` configuration should be required.

The browser publish should contain:

- Uno and ProGPU managed assemblies/AOT modules;
- ProGPU's JavaScript host/decoder module;
- shared WGSL assets, fonts, and application assets;
- no wgpu-native binary, Dawn native library, Emdawn port library, WebGPU C stubs, or ProGPU native rendering library.

Trimming and NativeAOT safety require static registration/source-generated metadata. Backend selection must not depend on reflection or dynamic assembly loading.

### Worker and scheduling rules

The browser provider advertises three support levels:

| Mode | Requirement | Behavior |
|---|---|---|
| Main thread | `navigator.gpu` and canvas | Decode and submit the coarse frame packet on the UI thread. |
| Worker | `OffscreenCanvas` transfer | Transfer the canvas once; decode/submit packets on a dedicated worker. |
| Isolated worker | Cross-origin isolation and shared memory | Use shared linear memory/transport with the lowest copy overhead. |

`Auto` should select the best advertised mode and record the reason for downgrade. No user-agent checks are allowed. Uno must coordinate canvas transfer exactly once; after transfer, the main thread must not configure or acquire textures from that canvas.

Only one queued render task may own a browser current texture at a time. Resize/DPI changes become generation-tagged messages. A packet produced for an old surface/device generation is rejected before touching JavaScript resources.

### Browser device loss and readback

`GPUDevice.lost` invalidates the entire provider generation. CPU-side Uno visuals and ProGPU semantic scene data survive; pipelines, buffers, textures, atlases, retained bundles, pending frame targets, and mapped readbacks do not. The binding stops presenting, recreates its JavaScript device/context asynchronously, increments generation, recompiles retained GPU state, and asks Uno for a full redraw.

Readback remains explicitly asynchronous:

- copy the bounded texture region to a map-readable buffer;
- await `mapAsync` in JavaScript/worker;
- return bytes through a coarse transfer;
- honor cancellation and device-loss generation;
- never busy-wait the browser event loop;
- never use CPU readback as the presentation path.

### Browser qualification matrix

The direct-JS Uno/ProGPU lane is complete only after all of these gates pass:

1. Release and .NET Wasm AOT publish with no native WebGPU/renderer artifacts or unresolved native WebGPU imports.
2. Real Chromium `navigator.gpu` execution on a hardware adapter; software adapters are labeled and excluded from hardware performance claims.
3. Main-thread, worker, and cross-origin-isolated worker rendering, with documented automatic downgrade.
4. The same 15 semantic/pixel scenarios used by the desktop matrix, including all blend modes and difference clips.
5. Resize, DPR change, background/foreground, canvas transfer, navigation/reload, and device-loss reconstruction.
6. Async `RenderTargetBitmap`/snapshot readback without blocking the event loop.
7. Input, IME, accessibility/native-element overlay, and native clip synchronization remain owned by Uno and work above the WebGPU canvas.
8. Diagnostics report browser, adapter/profile, execution mode, protocol version, frame/dispatch/byte counts, fallbacks, and device generation.
9. Performance captures separate managed scene/packet encoding, Wasm-to-JS dispatch, JavaScript decode, queue submit, and `onSubmittedWorkDone` completion.
10. Steady state demonstrates one coarse frame dispatch, bounded packet/resource-table growth, no per-draw JS interop, and no shader/pipeline recreation for stable scenes.

The managed/native parity rule still applies. Renderer, scene, text, cache, lifetime, and shader optimizations applicable to ProGPU's native C++ implementation must land and be gated there as well. A browser-transport-only optimization may be non-applicable to the native C++ Emdawn lane, but that decision must be documented with the concrete ownership/transport reason; “browser-only” by itself is not sufficient.

### Browser recommendation

Keep Uno's current Emdawn path as the built-in backend's compatibility implementation while adding the two-phase browser binding and logical frame target. Implement ProGPU browser integration through `ProGPU.Browser`'s direct JavaScript transport. Once qualified, Uno's built-in WebGPU backend can independently decide whether to retain Emdawn or adopt its own direct-JS transport. The drawing contracts should support both and should not make either transport normative.

## API lease design

### Problem

`IDrawingSession.NativeSurface` returns a type-erased live object with no explicit expiration. `IWebGpuRenderTarget.ColorView` is a raw borrowed handle whose lifetime is described only in prose. A consumer can retain either beyond the frame, call it from the wrong thread, or use it after device recreation.

### Proposed scoped feature lease

Use a callback or `ref struct` lease that cannot be retained accidentally:

```csharp
public interface IDrawingSessionFeatures
{
	bool TryLease<TFeature>(out DrawingFeatureLease<TFeature> lease)
		where TFeature : class;
}

public readonly ref struct DrawingFeatureLease<TFeature>
	where TFeature : class
{
	public TFeature Feature { get; }
	public GraphicsResourceOwner Owner { get; }
	public ulong DeviceGeneration { get; }
	public LeaseScope Scope { get; }
	public void Dispose();
}
```

Example feature faces could be `ISkiaCanvasFeature`, `IWebGpuRenderPassFeature`, or a backend-defined diagnostic feature. Merely acquiring a feature never transfers ownership of the device, queue, canvas, command encoder, or target.

For public APIs where a `ref struct` is too restrictive, use a callback:

```csharp
bool TryUseFeature<TFeature>(Action<TFeature, DrawingLeaseInfo> use)
	where TFeature : class;
```

The callback is synchronous and the feature becomes invalid when it returns.

### WebGPU lease descriptor

A WebGPU device lease should include:

```csharp
public sealed record WebGpuInteropInfo(
	WebGpuAbi Abi,
	Version ApiRevision,
	BorrowedHandle Instance,
	BorrowedHandle Adapter,
	BorrowedHandle Device,
	BorrowedHandle Queue,
	uint ColorFormat,
	uint SampleCount,
	WebGpuFeatureSet Features,
	WebGpuLimits Limits,
	GraphicsResourceOwner Owner,
	ulong DeviceGeneration,
	GraphicsThreadDomain ThreadDomain);
```

The per-frame target lease additionally declares:

- resolve texture/view and whether a multisample attachment is already supplied;
- texture dimensions, format, sample count, color space, and alpha mode;
- load/store/preserve policy;
- acquisition and expiration scope (`BeginPresent` through `IPresentSession.Dispose`);
- a completion service owned by the host/context;
- explicit `Borrowed` ownership for every handle.

Backends must translate descriptors across ABI revisions instead of pointer-casting structurally similar native descriptors. The host retains all release operations for borrowed handles.

### Device loss and completion

Add a generation-aware device-lifetime feature:

```csharp
public interface IGraphicsDeviceLifetime
{
	ulong Generation { get; }
	CancellationToken Lost { get; }
	ValueTask<GraphicsCompletion> WaitForCompletionAsync(
		GraphicsSubmission submission,
		CancellationToken cancellationToken = default);
}
```

The host reports device loss once; the backend atomically invalidates all resources from that generation. Completion waiting belongs to this feature rather than a raw `wgpuDevicePoll` escape.

## User-defined shaders

Custom shaders should be an optional factory feature. Do not expose backend-native source through `IShader` alone, and do not compile shaders inside a draw call.

```csharp
public interface ICustomShaderFactory
{
	ValueTask<ICustomShader> CompileAsync(
		CustomShaderDescriptor descriptor,
		CancellationToken cancellationToken = default);
}

public interface ICustomShader : IShader
{
	CustomShaderReflection Reflection { get; }
}
```

An immutable `CustomShaderDescriptor` should define:

- language and profile: initially WGSL, with optional SkSL/MSL/SPIR-V/backend packages exposed only when capabilities advertise them;
- stage and entry point;
- source/module hash and stable cache identity;
- typed uniform, texture, sampler, storage, and input layout;
- premultiplied-alpha and color-space contract;
- coordinate system, sampling rules, derivative availability, and bounds behavior;
- declared maximum sample radius/outset for damage tracking;
- deterministic/time-dependent flag;
- trusted/untrusted policy and validation limits.

Compilation is asynchronous and occurs before frame recording. The backend owns pipeline caching and may persist a driver-safe binary cache keyed by device/driver identity. A compiled shader is a disposable, owner/generation-bound resource. Using it with another device fails predictably.

Fallback must be explicit:

- `RequireNative`: fail if the requested language/profile is unavailable;
- `AllowPortableTranslation`: translate from a supported portable representation;
- `AllowCpuFallback`: only when the application opts in and the target contract permits readback/software work;
- `SkipWithDiagnostic`: only for diagnostic tooling, never as the default rendering behavior.

Browser deployments require source validation, resource limits, and no unrestricted native shader binaries. NativeAOT requires reflection-free descriptor metadata and generated parameter layouts.

## User-defined visual-tree effects

`EffectNode` is currently a public abstract record with a set of built-in nodes. A user can derive a record, but backends have no registration protocol to compile it, no capability identifier, and no required bounds/damage semantics. Backend switch statements therefore cannot safely realize an arbitrary derived node.

Add an effect extension registry obtained through `IGraphicsFeatureProvider`:

```csharp
public interface IEffectExtensionFactory
{
	string StableEffectId { get; }
	Version ContractVersion { get; }
	EffectSupport QuerySupport(EffectExtensionDescriptor descriptor);
	ValueTask<IEffectProgram> CompileAsync(
		EffectExtensionDescriptor descriptor,
		CancellationToken cancellationToken = default);
}
```

The neutral visual tree stores a custom effect node containing only:

- stable effect ID and contract version;
- immutable parameter block and resource inputs;
- ordered child/source inputs;
- source versus backdrop requirements;
- color/alpha-space behavior;
- sampling radius and output/damage outset;
- isolation and intermediate-surface requirements;
- time/input dependence and cache key;
- declared fallback policy.

`IEffectProgram` may describe a single filter, a multipass render graph, or a compute-plus-composite graph. This is important: a user-defined visual effect is not always representable as the existing `IEffectFilter` passed to one `SaveLayer`. The backend needs authority to schedule bounded intermediate surfaces while Uno retains ownership of visual ordering, damage, and presentation.

Application-facing APIs can expose a composition effect/attached visual effect without exposing native handles. Advanced native access remains available only through a scoped feature lease. The conformance suite must cover input ordering, backdrop capture, nested isolation, damage expansion, device loss, parameter animation, and fallback diagnostics.

## Cross-framework integration lessons

| Integration | Useful lesson for Uno |
|---|---|
| ProGPU WinUI framework | The best performance comes when the visual tree exposes stable retained identity, damage, and effect bounds rather than rebuilding immediate commands. Shader/effect objects must be device-generation resources. |
| Avalonia ProGPU backend | A native-surface/custom-render hook is useful for local drawing but insufficient for replacing text, images, effects, and compositor behavior. Platform context lifetime and frame leases must be first-class. |
| C++ UI integration | Managed-to-native parity and a compact immutable command/resource protocol avoid allocating wrapper objects per draw. ABI and ownership must be explicit even when both sides call the API “WebGPU.” |
| LibreWPF/WPF integration | WPF-style retained visuals provide strong invalidation identity, but device-specific resources hidden in brushes/effects can outlive the render target. Owner/generation-aware caches and deterministic teardown are essential. |

Uno's current contracts are closer to a complete replaceable renderer than a single custom-draw callback because geometry, fonts, images, SVG, recording, and presentation are independently injectable. The remaining work is to make ownership, capabilities, leases, and retained metadata just as explicit.

## Validation and rollout plan

### Phase 1: make existing semantics trustworthy

- fix built-in WebGPU blend and difference-clip defects;
- introduce shared pixel goldens/tolerances for all 15 scenarios;
- add strict unsupported/fallback diagnostics for every backend;
- wire the benchmark result to common diagnostics instead of ProGPU-only counters;
- rerun this exact three-process Latin-square matrix.

Exit gate: all three backends produce qualified pixels for every operation they claim as supported.

### Phase 2: close lifecycle holes

- migrate composition hot paths from `DrawingFactory.Current` to the active session/owner;
- add owner/generation metadata and foreign-resource checks;
- make shader/color-filter cleanup deterministic;
- audit and test context/backend disposal on every host;
- add multi-window, close/reopen, device-loss, and pending-readback tests.

Exit gate: no resource from one owner/generation can be consumed by another, and repeated window/device teardown releases all resources.

### Phase 3: add extensibility without destabilizing the base ABI

- add capability and feature-provider interfaces;
- introduce scoped native/API leases;
- add the async browser canvas/provider binding and logical WebGPU frame target;
- qualify a direct `navigator.gpu` ProGPU browser lane with no native graphics dependency;
- add custom shader compilation and reflection contracts;
- add custom visual-effect compilation/render-graph contracts;
- expose public ordered backend fallback registration.

Exit gate: a sample package can provide one custom WGSL effect with animated parameters, survive resize/device recreation, report its fallback behavior, and render through both built-in WebGPU and ProGPU without retaining a frame handle.

### Phase 4: performance contracts and gates

- add tight layer/effect bounds and retained metadata;
- add optional batch/glyph/geometry accelerator features;
- measure allocations, uploads, bind-group/pipeline creation, pass counts, memory peaks, and completion latency;
- run short PR gates plus longer stable-hardware/nightly gates;
- require managed C# and native C++ applicability review and parity evidence for every ProGPU optimization.

Exit gate: correctness-qualified performance has stable baselines and regression thresholds; no optimization is accepted from CPU-submit timing alone.

## Validation evidence

**Code review assessment:** The typed negotiation, neutral content seams, and opaque retained-record design are sound foundations. Source inspection confirms the built-in WebGPU blend-mode collapse and difference-clip scissor error, global factory use in device-bound composition paths, missing shader/color-filter disposal, and underspecified raw-handle leases. ProGPU's native adapter preserves host ownership and uses its retained GPU stack without changing the other backend implementations. ProGPU's separate managed browser provider demonstrates a direct, coarse-command `navigator.gpu` transport with no native graphics library; Uno's current browser WebGPU path instead imports a JavaScript-created device into a linked Emdawn C handle table. The proposed async browser binding is an architectural recommendation and has not yet been implemented or runtime-qualified in Uno.

**Compile validation:** `Uno.UI.Composition.Backend.Benchmarks.csproj` built in Release with `--no-restore -m:1`: 0 warnings, 0 errors.

**Runtime validation:** 135 fresh-process GPU benchmark executions completed: 15 scenarios × 3 backends × 3 repetitions. All v4 JSON files passed the repository schema; all reported the expected GPU/API/adapter identity and explicit GPU completion. Final pixels were read back for all runs, with first-repetition cross-backend comparisons. ProGPU qualified in all 15 scenarios; built-in WebGPU failed blend layers, the full blend corpus, and clips.

## Decision

Keep the new abstraction and the ProGPU backend. Do not redesign it around Skia, a single native canvas, or a native WebGPU ABI. Before treating it as a stable public third-party backend API, add owner/generation-aware resource lifetimes, scoped leases, common capabilities/diagnostics, deterministic shader/filter disposal, public ordered fallback, an async browser canvas/provider binding, logical frame targets, and a backend-neutral pixel conformance suite.

For the measured macOS/Metal workload, ProGPU is the preferred high-performance backend. Skia remains the correctness and compatibility fallback. The built-in WebGPU backend needs correctness fixes before its complex-scene performance can be compared without qualification.
