# ProGPU integration update for the latest drawing API

This record covers migration to Uno Platform
[PR #24153](https://github.com/unoplatform/uno/pull/24153) at commit
[`e82519bc235faabdf2e9d78598573a7f3e28dba4`](https://github.com/unoplatform/uno/commit/e82519bc235faabdf2e9d78598573a7f3e28dba4),
reviewed on 2026-09-22. Work is on `feat/progpu-drawing-backend-latest`; the
original `feat/progpu-drawing-backend` remains available with its previous
qualification evidence. No pull request is opened for this branch.

ProGPU remains pinned to
`93ca8d1170a8911cf5b4f94b6c380663cea48f9f`. The update adapts Uno-facing contracts,
ownership, samples, and qualification fixtures. It does not modify ProGPU's
managed or C++ renderer engines.

## Contract changes and implementation decisions

| Area | Current contract | ProGPU integration |
| --- | --- | --- |
| Drawing resource ownership | `IDrawingResource` has creator disposal and paired `AddRef`/`Release`; `DrawingResource` supplies the common implementation. | Geometry, textures, shaders, filters, and glyph-run geometry adopt the reference-counted lifetime. Recordings retain resources they capture. |
| Antialiasing | Public drawing and clip verbs no longer accept an antialias flag. | Raster quality remains the renderer's responsibility; callers emit semantic operations. |
| MSAA | `IWebGpuDeviceContext.SampleCount` is removed. | `ProGpuBackendOptions.Compositor.PrimarySampleCount` controls ProGPU, defaults to 1, and accepts 1 or 4. The host target remains single-sampled. |
| Image sampling | Per-call `ImageSampling` is removed. | ProGPU selects its image sampling path internally. |
| Image tint | `CreateTintColorFilter(Color)` replaces an open-ended blend-mode color-filter request. | The adapter implements the specified source-in tint and keeps color-matrix filters separate. |
| Mask layers | `SaveLayerMask()` is the explicit destination-in operation. | The adapter records an isolated mask effect; general blend modes use the effect graph. |
| General blending | `BlendEffectNode` describes the background, foreground, and mode. | Color/texture background plus source-content graphs work through `SaveLayer`. Applying those graphs to a live backdrop is explicitly unsupported; see the limitation below. |
| Repeated geometry | Offset `DrawPath` and `DrawPaths(ReadOnlySpan<PathInstance>, Color)` preserve shared geometry identity. | The adapter implements offsets without a copied transformed geometry and consumes the batch through existing ProGPU commands. This update does not claim a new merged GPU draw implementation. |
| Image tiling | `DrawImageTiled` carries independent `None`, `Clamp`, `Wrap`, or `Mirror` extension modes. | One quad invokes an adapter-owned WGSL shader through ProGPU's existing shader extension. Bilinear taps independently extend each axis; recording size is independent of tile count. |
| Font metrics | `IFont.CapHeight` is explicit. | ProGPU reads the font metric, then uses the `H` outline when available, with a size-based fallback. |
| Glyph reuse | `GlyphOutlineRef` is a font-cache-owned outline plus placement; `GlyphRunCache` caches short shaped runs. | Callers consume placed outlines without disposing the cache-owned geometry. The ProGPU font adapter uses the upstream shaped-run cache and keeps its existing glyph-atlas-compatible run geometry. |
| Lottie | `ILottieRenderer.Load` receives the registered geometry factory. | Registration includes `ManagedLottieRenderer`, which draws its supported shape-layer subset through neutral sessions backed by ProGPU. |
| Browser WebGPU | Context and target expose `JsDevice` and `JsColorView`. | A separate direct browser provider is required. The native provider rejects browser execution. |

`DrawingCapabilities.NativeStroking` is a promise to honor every supported
join with the contract's flat-cap, undashed stroke semantics. Merely exposing
`StrokePath` is not proof that this capability should be globally enabled.
Existing widening paths continue to cover the richer cap/dash cases.

## Resource ownership and retained recordings

The key invariant is that creator disposal cannot invalidate a resource still
captured by an immutable recording or an asynchronous readback. Creation owns
one reference. `Dispose()` drops that reference once; a borrower takes
`AddRef()` and returns it with `Release()`. Final native/GPU cleanup happens
only after the last reference is returned. Repeated disposal by the creator
must not release a recording's reference.

The adapter uses `ProGpuDrawingResourceLease` to bridge this lifetime into
existing ProGPU retained-picture ownership. A recording retains each captured
resource once per underlying drawing context. This distinction matters for
nested effect recordings: retaining a texture in the parent alone does not
establish the child picture's independent lifetime. A finished picture keeps
the transferred leases until its retained storage is released. The concrete
ProGPU recorder can dispose an unfinished recording, but the public
`ICommandRecorder` interface is not disposable: neutral callers finish the
recording and dispose the resulting `IRenderRecord`.

Effect filters also retain texture inputs from their graph. A caller may
dispose an input's creation reference after building the filter, while the
filter and any recordings using it keep the texture alive. `SnapshotAsync`
takes a texture lease before scheduling work, so caller disposal immediately
after requesting the snapshot cannot destroy the texture before readback.
The returned `IImage` is itself a drawing resource and the recipient must
dispose its creation reference.

Fonts have a different contract: `IFont` is provider-cached, with no per-use
disposal. `GlyphOutlineRef.Outline` belongs to the font cache and must not be
creator-disposed by text consumers. A created `GlyphOutline` or a vector color
layer still transfers caller-owned geometry. The adapter's native glyph-run
geometry lazily constructs a neutral outline for foreign sessions and frees
that fallback with its own final reference.

The native instance, adapter, device, queue, and presentation view remain
host-owned. ProGPU's borrowed lifetime object never releases them. Latest Uno
native window contexts share a process-wide device; closing one window must
not invalidate another window's drawing resources. GPU submission retirement
inside ProGPU remains separate from the CPU-side reference-count contract:
holding a drawing reference does not itself prove a GPU submission completed.

## Semantic drawing and effect migration

Framework layout and composition now supply operations whose meaning is
independent of a particular rasterizer. Removing caller antialias and sample
selection makes that ownership clearer. The adapter configures its own
compositor and allocates its own multisampled intermediate targets if
requested, resolving into the host's single-sample target.

Tint, alpha masks, and general blending have distinct contracts. A tint
preserves source coverage while applying a color. A mask layer performs
destination-in composition. A general blend belongs to an effect graph with
explicit inputs, for example:

```csharp
using var filter = factory.CreateEffectFilter(
    new BlendEffectNode(
        new ColorInput(background),
        new SourceInput(),
        BlendMode.Multiply),
    bounds) ?? throw new InvalidOperationException("Blend graph unsupported.");

session.SaveLayer(filter);
// Draw the foreground content.
session.Restore();
```

This graph's `SourceInput` means the content drawn into the effect layer.
When an effect is used through `DrawEffectBackdrop`, the supplied source must
instead be the existing backdrop. The attempted nested-layer implementation
failed that requirement: an opaque red backdrop multiplied by `(128,255,64)`
at half opacity produced BGRA `2080BFFF` instead of `0000C0FF`. Its inner
capture saw an isolated transparent surface rather than the live parent.
That incorrect path was removed. Generic blend filters currently support
source layers only; `DrawEffectBackdrop.BlendGraph` fails explicitly with
the default strict options. The negative test verifies one diagnostic and
unchanged drawing state and pixels. This is a documented capability gap,
not a completed live-blending implementation. Existing blur/acrylic backdrop
material paths remain separate.

The migrated benchmark prepares blend filters before warmup and timing. The
full corpus still enumerates every current `BlendMode`; it now describes each
case as a graph over an explicit background instead of calling a removed
generic layer-blend overload. Transparent graph output is tested inside an
isolated tile, without separately painting a background that would hide
transparent results. The mask scenario uses `SaveLayerMask()` directly.

`DrawPaths` preserves repeated-instance semantics, allowing future renderer
batching while keeping geometry identity and placement separate. The current
adapter translates the instances into existing commands. Tiling initially
expanded every tile to a texture patch, which could allocate millions of
entries for a one-pixel texture. The final adapter uses a constant-size shader
command instead. Recording a 3840×2160 repeat of a one-pixel texture allocated
4,760 bytes in the qualification process. All 16 axis combinations, fractional
final tiles, transforms, opacity, and premultiplied alpha have pixel checks.

## Text, fonts, images, and animation registration

`UseProGpuDrawingBackend()` registers the graphics provider, geometry factory,
font provider, image encoder/decoder, SVG renderer, and managed Lottie renderer
as a coherent stack. Geometry and font work continue through ProGPU. The
image and SVG adapters reuse Uno's managed content engines and emit into the
selected backend. CPU image decoding is a content operation; presentation
remains GPU-only.

The shaped-run memo is scoped to a font instance and separates direction and
ligature policy. Returned arrays are immutable shared results. The upstream
helper limits cached input length and entry count; this is not an unbounded
cache of application strings. Cap height is a font metric, not an alias for
ascent: using the actual metric avoids changing baseline/layout behavior for
fonts with different ascender and capital-height proportions.

`ManagedLottieRenderer` supplies the managed engine's documented shape-layer
subset. Its frames draw through the neutral geometry and drawing contracts,
so this registration does not introduce a Skottie surface or a second graphics
backend. It also does not claim complete Lottie feature coverage.

## Native ABI and browser integration

The native WebGPU binding declarations and pinned wgpu-native release remain
unchanged at `v29.0.1.1`. `UnoModernWebGpuApi` continues translating ProGPU's
Silk descriptors field by field into Uno's modern native ABI; descriptor
memory is never reinterpreted across versions. Opaque native device/queue
handles can be borrowed because both sides use the same native library and
object domain.

Desktop native provisioning now names the artifact after the P/Invoke import:
`libwebgpu.dylib`, `libwebgpu.so`, or `webgpu.dll`. The runtime's default loader
can resolve these names. The sample must import native provisioning when
ProGPU is enabled even if the built-in WebGPU drawing backend is disabled;
the host init assembly alone intentionally carries no native assets.

The browser contract is materially more useful for a direct implementation:
`IWebGpuDeviceContext.JsDevice` carries a JavaScript `GPUDevice`, and
`IWebGpuRenderTarget.JsColorView` carries the frame's `GPUTextureView`. Native
contexts return null for these optional properties. A backend can use the JS
objects directly without requiring that its own renderer first import them
into a C WebGPU handle table.

However, a JS-object seam alone does not make the current application free of
native browser graphics dependencies. Latest Uno's browser host still creates
and presents through its emdawn bridge, including the offscreen presentation
blit. The native ProGPU adapter still uses native WebGPU calls. This branch
has no direct browser provider and explicitly rejects that execution path.
ProGPU's independent browser implementation uses its own browser object/token
registry; its opaque tokens must never be passed to Uno's emdawn table or the
native descriptor adapter.

A complete direct browser integration would need to:

1. Adopt the supplied JS device into ProGPU's browser dispatcher with clear
   borrowed ownership, capabilities, and device-loss handling.
2. Register the borrowed JS target view for each frame, including resize and
   disposal boundaries, and remove temporary token registrations promptly.
3. Submit to that device's queue while preserving Uno's presentation order;
   implement asynchronous readback without blocking the browser event loop.
4. Provide a direct-JS host presentation path if the entire browser dependency
   closure must exclude emdawn/native graphics artifacts, then adjust package
   and build selection accordingly.
5. Verify loading, screenshots, fonts, device loss, target resize, multi-window
   ownership, and package contents with no unintended native renderer assets.

Those are follow-up design and qualification requirements, not capabilities
claimed by this migration.

## Project and sample migration

The old `SamplesApp.Skia.Generic` head is replaced by
`src/SamplesApp/SamplesApp/SamplesApp.csproj`. The desktop target is
`net10.0-desktop` for this qualification lane. The head preserves the explicit
`UnoTargetFrameworkOverride=net10.0-desktop`, while shared projects strip the
head-only platform moniker and select `net10.0`. The smoke and benchmark
projects use the repository's framework properties and must select their
`net10.0` target when run.

All integration project references now use
`Uno.WinRT/Uno.WinRT.Reference.csproj`, preserving the `unouwp` source alias.
`ProGPUUseWinRTContracts=false` keeps ProGPU's optional compatibility
projection out of the global contract namespace. The sample also retains the
incremental-build alias safeguard for a stale compatibility reference.
The sample ProGPU define and project reference are enabled only for desktop.
See [README.md](README.md) for complete restore, build, and launch commands.

## Managed and native C++ applicability

The mandatory parity rule remains: a ProGPU managed rendering optimization
must be audited for native C++ applicability, and equivalent work must land in
both engines with matched behavior, quality, resource lifetime, and
performance validation when applicable.

This change modifies the Uno C# adapter and its consumers. The reference-count
leases implement Uno's `IDrawingResource` lifecycle; the shaped-run memo
adopts Uno's `GlyphRunCache` around the `IFont` adapter; operation signatures
and registrations implement Uno's managed extension interfaces. The native
ProGPU renderer does not implement those interfaces. No ProGPU engine code
or submodule commit changes. The new WGSL tiling program belongs to this
adapter and consumes the existing managed compositor's shader extension.
The pinned native scene compiler does not lower that custom shader extension;
equivalent native C++ qualification is therefore still open. The allocation
improvement must not be described as verified in both engines. Completing
native lowering and matched pixel/allocation gates remains required before
claiming full managed/native parity. Future engine changes remain subject to
the same rule.

## Validation and performance evidence

The benchmark result JSON retains schema v4 because its fields are unchanged.
The semantic fixture-hash input advances to v3 to identify backend-owned
antialias/sampling policy and the explicit effect-graph blend fixtures. The
August results have different fixture and renderer revisions and must not be
used as current performance ratios.

Checkpoint validation on macOS 26.6, Apple M3 Pro, .NET 10.0.201 SDK:

- **Compile:** desktop Gallery `net10.0-desktop` succeeded with 16 native SDK
  warnings and zero errors. The final GPU smoke harness, benchmark harness,
  and standalone unit-test project each built with zero warnings/errors.
- **Runtime:** final ProGPU/Metal smoke passed, including ownership, original
  rendering regressions, new drawing/content contracts, bounded tiling, and
  a separate explicit-unsupported backdrop probe. Positive scenarios had no
  unsupported operations; the deliberate negative probe adds exactly one.
- **Runtime:** Gallery startup confirmed `ProGpuGraphicsProvider` won the
  `WebGpu` context; both `Given_ManagedLottie` tests passed, zero skipped.
- **Unit suite:** the broad solution could not build in this environment
  (.NET 11/Windows-only projects and missing restore assets). The standalone
  project built. Its first run failed ICU initialization; using Gallery's
  native ICU directory gave 4,078 passed, 118 failed, 23 skipped out of 4,219.
  Those remaining failures have not been investigated or baseline-compared.
- **Performance smoke:** nine fresh-process runs covered `cached`, `text`,
  and `images` on ProGPU/Metal, Skia/Metal and built-in WebGPU/Metal. Each had
  two warmups, five samples, two five-frame batches, forced redraw, GPU mode,
  and zero unsupported operations. Cached-scene hashes matched all three;
  text hashes differed across renderers, and image hashes matched ProGPU to
  WebGPU but differed from Skia. This is execution/readback evidence, not
  complete pixel qualification or a statistically adequate speedup claim.

The benchmark deliberately supplies identical ProGPU-generated font/geometry
inputs to all backends; it measures drawing, not each framework's full text
stack. Handling of `GlyphOutlineRef` is forward-compatible, but this fixture
currently produces a merged glyph outline. Future whole-stack comparisons
must use separately registered content engines and explicitly label the scope.

Verification commands are in README. Unit execution requires the repository's
Microsoft.Testing.Platform syntax: `dotnet test --project ...`, not the older
positional project argument. In this checkout its process-local macOS ICU
workaround is `DYLD_LIBRARY_PATH` pointing to the Gallery output's
`runtimes/osx/native` directory; no source workaround was applied.

## Paused checkpoint and remaining work

The user requested committing the current work and pausing. No Uno or ProGPU
PR was opened during this migration. This checkpoint is not full conformance:

The eight review lenses were applied before the checkpoint: architecture,
contract, skepticism, performance, quality, security, operability, and scope
questions. They identified the tiled-allocation issue (fixed and measured),
coverage gaps (added), and the live-backdrop/C++ limitations retained below.
Security and operability found no additional migration-specific blocker.
The result is suitable for a paused checkpoint, not a production-conformance
or merge-readiness approval.

1. Implement correct live generic backdrop blending, including translucent
   destinations, masks, opacity, transformed bounds and retained replay.
2. Complete native C++ custom-shader lowering and matched parity gates.
3. Implement and qualify the direct-browser provider; current native guard
   intentionally rejects browser use.
4. Investigate the unit-suite failures and run the complete current-revision
   pixel/performance comparison before publishing new comparative claims.

Required runtime coverage includes creator disposal before replay, nested
record lifetime, texture readback after caller disposal, masks, tint and
color-matrix filters, nonzero-origin blend bounds, geometry placement,
per-axis tile extension, font cap height and cache policy, managed Lottie,
backend-owned MSAA, and safe rejection of unsupported browser execution.
Any gate that cannot run must remain explicitly identified rather than being
inferred from a successful compilation.
