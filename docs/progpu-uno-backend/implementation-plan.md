# Implementation and validation plan

## Baselines

- Uno base: immutable head of PR #24153.
- ProGPU dependency: merged `main` commit
  `d9a85cba9ccb10dd5a65d83273b66f0f9a9a8444`, including public dependency
  changes #125 through #135.
- Primary validation platform: macOS arm64, .NET 10, Dawn/Metal.
- Secondary compile/runtime lanes: Windows Dawn/D3D12, Linux
  wgpu-native/Vulkan, and browser WebGPU when the host callback ABI is proven.

Every validation report records commit IDs, dirty state, SDK/runtime versions,
OS, CPU/GPU, power state, adapter/backend, display scale, window/target size,
build configuration, exact commands, and raw artifact paths.

## Current execution status

| Phase | Status on 2026-08-23 | Evidence / remaining work |
|---|---|---|
| 0 — analysis | complete | architecture, capability, ownership, comparison, and benchmark documents |
| 1 — dependency/device | implemented on macOS | pinned submodule, modern ABI adapter, external-device initialization, borrowed lifetime |
| 2 — drawing core | working vertical slice | real-device smoke, SamplesApp present, analytic clip-hole and broad effect-bound corpus; arbitrary effect DAG isolation remains |
| 3 — content stack | implemented vertical slice | ProGPU geometry/font/shaping/direct glyph path plus neutral managed image/SVG adapters; full corpus remains |
| 4 — application correctness | partially qualified | SamplesApp loads and presents 1,413-sample catalog; systematic sample/pixel sweep remains |
| 5 — benchmarks | software and macOS GPU pairs qualified | v3 stage timing, target readback, fifteen scenarios including analytic strokes, gradient materials, isolated color-matrix layers, unfiltered source-over isolation, destination-in composition masks, single-mode blend layers, a 27-mode blend corpus, and anisotropic/additive shadows; eight alternating ProGPU/software-Skia process pairs for the primary seven, focused follow-ups, and three alternating ProGPU/Metal versus Skia/Metal pairs across all fifteen scenarios with explicit GPU completion; the Uno built-in WebGPU promotion, startup/scrolling/memory scenarios, and cross-framework ports remain |
| 6 — hardening | open | Windows/Linux/browser, device loss, AOT/trimming, leak and long-running stress |

## Mandatory managed/native optimization gate

The ProGPU managed C# renderer and native C++ renderer are treated as two
implementations of one rendering and performance contract. Every managed
rendering optimization used by this backend must receive an explicit C++
applicability audit before its dependency change is accepted:

- an applicable optimization lands in both implementations with equivalent
  behavior, quality, complexity, resource identity/lifetime, retention,
  uploads, allocations, fallback, and failure semantics;
- a one-sided change must name the concrete ownership or execution boundary
  that makes the other implementation non-applicable; language or API shape
  alone is not sufficient;
- shared behavior receives matched managed/native regressions and equivalent
  Release workloads, including stable-frame allocation/upload and pixel gates;
- generated wire layouts, public C records, shaders, fixtures, and expected
  output remain synchronized;
- an unexplained managed-only rendering optimization blocks dependency
  advancement and backend qualification.

The current WebGPU lifetime correction passes this gate. Managed Silk-native
contexts and non-Dawn C++ engines share equivalent process-wide synchronization
domains. The managed persistent-texture dictionary publishes native bind
groups outside its monitor; the native engine has no corresponding dictionary
because it owns fixed retained slots, so that sub-change is explicitly
non-applicable while the shared resource-lifetime invariant remains covered by
the native dispatch scope.

The dependency optimization families consumed by this branch have the
following native audit:

| Managed optimization family | Native C++ applicability/equivalent | Gate |
|---|---|---|
| optional WinRT geometry contracts | managed build/reference closure only; no renderer algorithm or wire change | documented non-applicable |
| analytic mask isolation, rounded rings, and contained rectangular holes | native semantic mask chains retain analytic rounded-rectangle masks and canonical mask shaders | native differential/mask gates |
| retained picture eligibility, page admission, bulk append, and compact draw merging | native semantic scenes use generation-keyed immutable snapshots, retained GPU pages, and bounded in-place analytic/path/glyph draw merging | stable replay, zero-upload/allocation, differential gates |
| duplicate gradient-stop exact-offset selection | canonical production shader behavior is shared by managed and native pipelines | shader/resource and pixel gates |
| bounded queue drain and explicit completion accounting | native submission tokens and provider-specific queue completion retain the same bounded lifetime contract | native provider/completion gates |
| GPU HostBackdrop capture and destination-safe blend semantics | native backdrop execution owns ordered capture/resolve and the same blend-mode contracts | native backdrop/effect differentials |
| detached effect retirement and retained output/content generations | native fixed retained slots, scene/resource generations, and effect texture generations provide the equivalent lifetime/identity model | stable reuse, mutation, and teardown gates |
| color-matrix, isolated blend, and shadow-only visual effects | native semantic image/effect records, effect chains, and group-blend pipelines already implement the complete GPU-resident operations | native/managed effect differentials |
| anisotropic shadows and zero-axis work elimination | native ABI already carries `sigma_x`/`sigma_y`; C++ two-pass execution is axis-specific and shares the production shaders; managed work closed the prior gap | managed/native shadow and residency gates |
| process-wide WebGPU lifetime serialization | applicable to both; managed Silk-native contexts and native non-Dawn dispatches now own equivalent process scopes | threaded, stress, CTest, browser/Dawn isolation, and performance gates |

This table is an acceptance ledger, not permission to assume future parity.
Every later optimization adds or updates a row with source and runtime evidence.

## Phase 0 — analysis and contracts

- Freeze the PR head and create an additive feature branch.
- Inventory every public drawing/content seam and every call site.
- Compare the existing ProGPU Avalonia, ProGPU WinUI, C++ UI, and
  WPF-compatible integrations.
- Name ownership, thread, device-loss, and no-readback invariants.
- Define conformance and benchmark gates before implementation.

Exit: this documentation is reviewed against the source and contains no
private repository identifiers or normative private links.

## Phase 1 — dependency and device bridge

- Add ProGPU `main` as a pinned git submodule.
- Add `Uno.UI.Composition.ProGpu` as a Skia-free net10 project.
- Reference ProGPU Backend/Vector/Text/Scene projects and Uno's WebGPU Init
  project.
- Implement exhaustive modern ABI descriptor/enum translation.
- Implement borrowed-device lifetime and device-generation diagnostics.
- Add provider registration and a one-call host-builder extension.

Exit: the factory initializes on Uno's WebGPU device; ownership tests prove it
does not release host handles.

## Phase 2 — retained drawing core

- Implement command recorder, render record, drawing session, present session,
  texture, shaders, filters, offscreen rendering, and snapshot readback.
- Map transforms, clips, layers, primitives, paths, gradients, images,
  nine-slice, shadows, and effects.
- Preserve ProGPU resource/cache identities across stable replays.
- Add unsupported-operation and per-frame telemetry.

Exit: a minimal app renders GPU-only and the core drawing runtime corpus passes.

## Phase 3 — complete content stack

- Implement native ProGPU geometry builders and immutable geometry operations.
- Implement font catalog, style/variable matching, shaping, metrics, fallback,
  monochrome outlines, vector color layers, and bitmap color glyphs.
- Add the typed direct-glyph fast path while retaining neutral interoperability.
- Implement portable image codec and retained SVG document adapters.

Exit: the backend runs with all five ProGPU seams registered and without Skia
assemblies or platform text APIs.

## Phase 4 — application integration and correctness

- Add a compile-time SamplesApp backend flag and runtime selection variable.
- Add focused runtime samples for transforms, nested clips/layers, paths,
  gradients, images, effects, complex text, fallback, color fonts, SVG, and
  device-loss recovery.
- Add targeted tests for the triggering behavior and an adjacent regression
  scenario for each discovered bug.
- Run a representative SamplesApp sweep and record unsupported operations.

Exit: zero unsupported operations in the qualification corpus and accepted
pixel/metric tolerances.

## Phase 5 — standard benchmark suite

- Add a versioned renderer scorecard workload and JSON schema.
- Add cold startup, first present, fully cached frame, sparse mutation, forced
  text miss, image upload, path stress, effects stress, scrolling, and
  control-density scenarios.
- Run Skia, Uno WebGPU, and ProGPU through identical Uno application state.
- On macOS, qualify Skia's real Metal `GRContext` and ProGPU's wgpu-native
  Metal path against retained BGRA8 GPU textures with ordered completion
  fences; keep software Skia as a separately labeled diagnostic lane.
- Run equivalent semantic workloads in ProGPU WinUI and ProGPU Avalonia for
  integration-overhead context.
- Use the C++ UI gate methodology for paired process ordering, completion,
  parity, statistics, and artifacts.
- Capture `dotnet-trace`/`dotnet-counters` plus Time Profiler, Allocations/VM
  Tracker, and Metal System Trace on macOS.

Exit: schema-valid raw results, pixels, environment manifest, profiler traces,
and an evidence report separating diagnosis from claims.

## Phase 6 — hardening

- Run Release builds, focused unit tests, Skia Desktop runtime tests, leak
  tests, NativeAOT/trimming, and multi-platform compile lanes.
- Audit allocations and locks in the hot path.
- Validate repeated resize, DPI changes, occlusion, minimize/restore, rapid
  disposal, snapshots during animation, and injected device loss.
- Review public API and packaging; keep implementation types internal except
  explicit provider/options/diagnostics surfaces.

Exit: capability matrix complete, known limitations documented, and exact
maintainer commands provided for any platform validation unavailable locally.

## Change classification

Every implementation change is classified during review:

- `root-cause fix`: repairs an ownership, lifetime, resource-identity,
  post-mutation coherence, or ABI translation invariant;
- `defensive hardening`: validates input or reports an already-invalid state
  after the invariant is correct;
- `feature`: adds a previously unimplemented drawing/content contract;
- `measurement`: changes only benchmark instrumentation or analysis.

Guard-only changes are never described as complete root-cause fixes.
