# Analysis of the pluggable drawing work

## Baseline

This implementation branches from commit
`c4b1cd24d2c5ba5ac0a472e499f81d0ec22de2f9`, the analyzed head of public Uno
PR [#24153](https://github.com/unoplatform/uno/pull/24153) on 2026-08-22. The
change is broad (roughly 507 files at that head) because it extracts drawing
contracts, moves Skia behind them, adds the new WebGPU implementation, and
threads backend-neutral content services through composition and controls.
The ProGPU work is additive and does not alter those PR-owned contracts or its
Skia/WebGPU backend sources.

## Contract surface introduced by the PR

### Negotiation and ownership

- `GraphicsRegistry` selects a provider against host-created context kinds.
- `IGraphicsProvider<TContext>` receives a typed device context and creates one
  device-bound factory.
- `IDrawingFactory<TTarget>` accepts only its typed render target. This makes
  device/target incompatibility a registration error rather than a late cast.
- Context and target faces exist for software, GL, Vulkan, Metal, and WebGPU.
  The first ProGPU lane intentionally implements WebGPU only.

### Record, replay, and present

- `ICommandRecorder` is an `IDrawingSession` whose terminal `Finish` returns an
  opaque backend-owned `IRenderRecord`.
- `IRenderRecord.Replay` preserves backend retention; Uno does not inspect or
  normalize the display list.
- `IPresentSession` scopes the borrowed host target. Disposing it completes
  backend submission, after which the host presents.
- `IDrawingFactory.RenderOffscreen`, `CreateTexture`, and `SnapshotAsync`
  separate GPU residency from explicit CPU readback.

### Drawing vocabulary

`IDrawingSession` provides transforms, save/restore, clips, isolated layer
forms, clear, solid/gradient primitives, paths, shadow, line/stroke, image,
nine-slice, color filtering, and effect-backdrop operations. It intentionally
does not prescribe a shared native command layout. `NativeSurface` is an
optional type-erased escape hatch.

### Backend-neutral content

- `IGeometryFactory`, `IPathBuilder`, `IPrimitiveGeometryBuilder`, and
  `IGeometry` cover construction, bounds/hit-testing, transformation, boolean
  operations, trim, stroke widening, and segment interchange.
- `IFontProvider`/`IFont` cover matching, fallback, shaping, metrics, advances,
  and neutral monochrome/color/bitmap glyph output.
- `IImageEncoderDecoder` defines CPU image interchange while `ITexture` remains
  backend/device-specific.
- `ISvgRenderer` and `ILottieRenderer` are independently replaceable document
  services.
- Neutral `EffectNode` trees permit fusion when supported and a framework
  recipe when not.

## Why ProGPU fits without PR changes

The decisive seam is `IWebGpuDeviceContext` plus `IWebGpuRenderTarget`:

1. Uno already owns surface creation, adapter/device/queue lifetime and target
   acquisition.
2. ProGPU supports initialization from external native device/queue handles.
3. Both renderers can therefore share one WebGPU device domain and ProGPU can
   render directly to the frame's borrowed `ColorView`.
4. Uno remains responsible for scheduling and present; ProGPU remains
   responsible for scene compilation, pipelines, textures, vector caches and
   glyph atlases.

Opaque device, queue and texture-view handles are shareable. Managed descriptor
structures are not: the PR uses its modern generated wgpu-native ABI while the
stable ProGPU contract uses Silk.NET.WebGPU 2.23 types. `UnoModernWebGpuApi`
therefore translates every descriptor and enum field into Uno's ABI rather
than reinterpreting memory. The actual runtime caught one important example:
the older binding-layout `Undefined` value maps to modern `BindingNotUsed`, not
to a same-number cast.

## Integration lessons applied

### Avalonia backend

The Avalonia implementation demonstrates device-domain validation, external
target leases, synchronized submission, retained resource caches, complete
font-service replacement, and explicit fallback accounting. Uno's typed
provider/target contract is smaller and cleaner at the boundary, while
Avalonia offers deeper compositor feature discovery. This backend adopts the
device-domain and retention rules without importing Avalonia abstractions.

### ProGPU WinUI-compatible framework

That framework owns both UI semantics and ProGPU scene publication, so it can
retain stable semantic IDs earlier than Uno's canvas recording boundary. The
Uno adapter recovers the most important benefits by retaining `GpuPicture`,
ProGPU path objects, textures and typed glyph runs. It cannot bypass Uno's
visual traversal without changing the PR contract, and deliberately does not.

### C++ UI integration

The reusable lessons are immutable scene revisions, stable resource identity,
explicit unsupported-operation counters, mutation outside the timed boundary,
submit-plus-GPU-completion timing, correctness gates, raw samples, and
alternating process order. Public documentation uses this generic name so the
specification has no dependency on non-public artifacts.

### WPF-compatible integration

The WPF-compatible path reinforces separation between dispatcher-owned UI
objects, retained render resources and device generations. It also shows why
native escape hatches must be narrowly scoped and why compatibility projections
cannot be allowed to collide with the host framework's Windows contracts.

## Discovered integration issues

| Issue | Broken invariant | Resolution | Classification |
|---|---|---|---|
| old and modern WebGPU enums/descriptor layouts differ | ABI structures must be translated, not pointer-cast | exhaustive `UnoModernWebGpuApi`, including explicit binding-layout mapping | root-cause fix |
| ProGPU compatibility projection duplicates host `Windows.*` types | exactly one host contract assembly may be globally visible | optional ProGPU contract property plus non-global compiler alias during transition | root-cause fix + defensive hardening |
| Uno border paths cast backend geometry to host `IGeometrySource2D` | every geometry returned to composition must satisfy the host marker | `ProGpuGeometry` implements Uno's marker; smoke asserts it | root-cause fix |
| transparent clear under source-over preserves destination | `Clear` replaces every destination channel | issue the full-target fill under ProGPU `Src` blend scope | root-cause fix |

## API assessment

The PR is sufficient for an external, same-device GPU backend and a complete
replacement of geometry/font/image/SVG services. No framework code needs to
know ProGPU. The main current constraints are capability rather than access:
isolated layer semantics must be expressible by the backend's scene engine,
the font contract's neutral glyph elements need a typed optimization to retain
atlas performance, and device-loss handoff remains a coordination requirement
between the host and backend factory.

Compared with Avalonia, Uno's strongest feature is explicit typed context and
target negotiation; compared with a semantic C++ UI or ProGPU's own
WinUI-compatible framework, its canvas boundary loses some early semantic
identity; compared with WPF's retained channel model, it is substantially
easier to implement externally but puts more generation/lifetime policy in the
backend. `IRenderRecord` and independent content seams are what make the trade
acceptable for ProGPU.
