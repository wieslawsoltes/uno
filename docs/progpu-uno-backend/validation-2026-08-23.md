# Validation record — 2026-08-23

## Revisions and environment

- Uno branch baseline: `6848a67e49`.
- Uno work branch: `feat/progpu-drawing-backend`; no Uno pull request opened.
- ProGPU gitlink: merged `main` commit
  `64271d7fd2ca8a059e80d9af46e3de003f8409f5`.
- ProGPU dependency changes: public PRs #125 through #133, all merged.
- Host: macOS 26.6 arm64, Apple M3 Pro, .NET SDK 10.0.201,
  runtime 10.0.5, wgpu-native/Metal.

## Root-cause changes

| Invariant | Classification | Correction |
|---|---|---|
| an exact rounded border must not require an arbitrary texture mask | root-cause fix | preserve rounded metadata, recognize uniform inset differences, and evaluate analytic ring coverage in every affected shader |
| a matching outer rounded clip must not duplicate ring coverage work | root-cause fix | reduce the nested matching pair to the ring analytic mask |
| a contained rectangular hole in a rounded clip must not allocate an offscreen mask | root-cause fix | compose the two contours into one even-odd analytic mask and retain the original outer clip when the nested scope is restored |
| unchanged nested pictures must retain compilation identity when their parent record changes | root-cause fix | cache immutable picture pages by full compilation context and replay their arrays/draw calls |
| caching must cost less than recompilation | root-cause fix | admit after reuse and reject pictures below a configurable minimum command count |
| a leading replacement clear must not become a redundant full-target draw | root-cause fix | carry the leading clear as record metadata and apply it as the attachment clear while retaining ordered nested-record replacement semantics |
| retained solid pages must not scan an empty brush map per vertex | root-cause fix | bulk-copy inline-color vertices and rebase index arrays through contiguous spans |
| compatible retained-page draws must not expand or copy the full compositor draw-call value | root-cause fix | compare the compact retained projection first and mutate the accumulated call in place through its list span |
| an identity-only retained picture wrapper must not allocate a second command stream or falsely invalidate the presentation visual | root-cause fix | clone the child picture's immutable command storage, preserve independent resource leases, and compare retained storage identity in the host visual |
| duplicate gradient stops must make an exact offset select the later stop | root-cause fix | use the previous stop only for strictly smaller offsets in vector and hatch shaders |
| solid ellipse strokes must retain their analytic curve | root-cause fix | preserve ellipse radii and emit exact even-odd rounded-ring geometry instead of a 22.5-degree flattened outline |
| queue cleanup must bound residency without serializing every small burst | root-cause fix | expose the conservative drain bound and select a 64-submission window while retaining per-frame non-blocking polling |
| HostBackdrop must sample already-rendered content without sampling the borrowed swapchain view | root-cause fix | conditionally render backdrop frames into a bindable same-device texture, split and ping-pong at ordered backdrop commands, then GPU-blit to the borrowed view |
| an effect-backdrop command must occupy the same transformed coordinates as its clip and border | root-cause fix | record the session's current matrix on the backdrop command instead of identity; a translated pixel fixture covers the formerly missing region |
| an effect layer must rasterize the content it actually recorded, including non-zero and transformed coordinates | root-cause fix | accumulate conservative transformed primitive bounds, set `EffectContentBounds`, and propagate nested effect output bounds to the parent layer |
| a shadow-only framework filter must not composite its source twice | root-cause fix | add an invalidating `DrawSource` effect property whose default remains source-plus-shadow, and opt the adapter into shadow-only composition followed by explicit source replay |
| detached effect resources must not disable a later effect-free scene cache | root-cause fix | retire unused effect textures before compiled-scene cache admission and validate zero remaining effect bytes before the next cache hit |
| disposing a backend that borrowed the host WebGPU context must not release that device | root-cause fix | keep device ownership with the host and validate another native allocation after factory disposal |
| Uno blur sigma and ProGPU backdrop radius must have a measured conversion | measurement correction | compare identical pixel fixtures at 1.8×, 2.0×, 2.2×, and 3.344× and select the lowest-error 2.0× mapping |
| benchmark pixels must represent one frame | measurement correction | explicitly clear every frame and read back the final target after timing |
| GPU wait must not be mislabeled as renderer CPU time | measurement correction | publish CPU submit, GPU completion, blocking total, batch throughput, and ProGPU internal stages separately |
| a successful explicit completion wait must advance deferred-submission accounting | root-cause fix | mark the submitted work as drained after the native wait so later cleanup does not repeat the same blocking drain |
| an unchanged scene must not imply that an arbitrary presentation target still contains it | root-cause fix | reuse populated output only when retained picture storage, transform, target/view identity, size, clear color, and texture-content generation all match; backdrop and scene-dump frames fail closed |
| an already-completed queue must not pay a blocking device-poll quantum | root-cause fix | register an `AllowSpontaneous` queue-completion callback and make non-blocking device progress until the future resolves; retain the blocking API only as a fallback |
| a direct stroke must reach the renderer as its authored centerline | root-cause fix | defer stroke-fill materialization, map the complete style to a native ProGPU pen, and lazily widen only for fill-region consumers such as clips, bounds, hit testing, trims, streams, and foreign backends |
| a color-filter layer must transform the already-composited subtree exactly once | root-cause fix | record one nested picture, cache it as a ProGPU visual source surface, and apply the 4x5 matrix through the GPU image-effect shader at layer restore |
| a blend-mode layer must combine with the destination after its subtree is composited | root-cause fix | retain one source-only effect surface, commit earlier destination draws before changing blend state, apply the requested mode exactly once, and restore the prior state |
| a parameterless layer must isolate a clipped transparent clear from the destination | root-cause fix | route `SaveLayer()` through a retained source-over blend effect so clear and content are recorded on one source surface before restoration composites it once |

Arbitrary/non-uniform geometry, masks, hit testing, unsupported draw commands,
volatile variants, and stale atlas generations fail closed to normal
compilation. Cache capacity, variants, and age are bounded.

## Compile validation

The runtime test, benchmark, and their complete dependency graphs built in
Release with zero warnings and zero errors using serial MSBuild:

```bash
cd src
dotnet build \
  Uno.UI.Composition.ProGpu.RuntimeTests/Uno.UI.Composition.ProGpu.RuntimeTests.csproj \
  -c Release --no-restore -m:1
dotnet build \
  Uno.UI.Composition.Backend.Benchmarks/Uno.UI.Composition.Backend.Benchmarks.csproj \
  -c Release --no-restore -m:1
```

An earlier parallel attempt was not code evidence: duplicate Uno project
builds raced while rewriting `Uno.dll` and `Uno.UI.pdb`. The serial rerun
resolved the environmental file-lock race without source changes.

## Dependency tests

The current blend-effect branch passes the complete locally available suites:

```text
ProGPU.Tests:          3,712 passed, 0 failed, 0 skipped
ProGPU.Tests.Headless:   240 passed, 0 failed, 0 skipped
```

`ShapingContractsTests` is excluded from the first command because that lane
depends on separately provisioned upstream source contracts; the dedicated CI
text-contract job supplies that coverage. The 29-test focused effect and visual
change-version run also passes. It covers
translated effect placement, shadow-only output, detached resource retirement,
cache invalidation, GPU color-matrix channel transforms, isolated Multiply
overlap, and live matrix/blend mutation. The regression verifies final pixels after changing one nested
picture while unchanged siblings reuse compiled pages. The adaptive-admission
variant uses four-command pictures and validates that the minimum-command gate
does not disable profitable subtree reuse. The real-device smoke additionally
asserts that a cleared cached presentation emits one content draw/four vertices,
and that replaying a cleared record after existing content still replaces the
earlier pixels in order.

The retained draw-call follow-up adds a draw-count assertion to the nested
picture mutation fixture and strengthens the compact-page source contract.
The identity-picture follow-up adds five tests covering flattening, retained
resource lifetime after the source clone is disposed, additional parent
resources, transformed wrappers, and explicit completion-window reset. The
retained-output follow-up adds texture mutation/version fixtures. The final
full 3,712-test and 240-test suites passed. One allocation-sensitive MotionMark
test failed once in an earlier local full run and once in the merged PR's
macOS CI job; it passed in the complete local rerun, in isolation, and in five
consecutive post-merge reruns. The unrelated CI retry is tracked separately and
is not counted as positive evidence for this revision.

The final merged dependency run is green across all 26 jobs: managed builds and
tests on macOS, Linux, and Windows; retained compositor, text, image-parity, and
native Dawn contracts; C++ compiler/browser/native-renderer lanes; portable and
mobile packaging; and native package consumers on all six OS/architecture
combinations.

## Runtime and visual validation

The focused real-device executable covers typed context negotiation,
borrowed-device ownership, offscreen rendering, native glyph recording,
effects, GPU readback, retained replay, completion, geometry host-marker
compatibility, and zero unsupported operations. It also verifies HostBackdrop
blur/capture, foreground ordering, translated and nested effect-layer bounds,
shadow-only source omission, gradient/non-uniform-rounded/border/path/stroke/
line/image/color-filtered-image/nine-slice content-bound propagation, isolated
and nested color-matrix layers, unfiltered clipped-clear isolation, isolated
Multiply-layer overlap,
rounded-outer/rectangular-hole clip restoration,
and the conditional present/blit route. Its final
Release run reports:

```text
[webgpu] init device — msaa=2x fmtFeatures=True colorFormat=BGRA8Unorm
ProGPU runtime smoke passed; center=DC5014FF, frame=12.
```

SamplesApp was built with
`UnoDrawingBackendProGpu=true`; startup confirms provider negotiation with:

```text
Graphics backend 'ProGpuGraphicsProvider' won negotiation on context kind 'WebGpu'.
```

The same final binary was launched once with `UNO_PROGPU=1` and once without
it. The gallery title and status surface reported `ProGPU · WebGPU · macOS`
for the first run and `Skia · Metal · macOS` for the second. A locally built
`libUnoNativeMac.dylib` had no `LC_RPATH`; the runtime invariant was restored
without source changes by pointing `DYLD_LIBRARY_PATH` at the already copied
`runtimes/osx/native` directory. `--no-launch-profile` avoids the unrelated
commented launch-settings parse warning.

This check caught a launch-configuration trap during validation: setting
`UNO_PROGPU=1` on a binary compiled with the flag disabled still launches the
normal Skia backend. Only the negotiation log is accepted as proof.

The expanded geometry smoke preserves an elliptical `ArcTo` centerline through
a solid native stroke, exercises a dashed arc with distinct square, triangle,
and round caps, and forces filled-region hit testing plus a trimmed-stroke
fallback. At 256×256, the rendered analytic centerline stays within 1.25 pixels
of the ideal ellipse. The path-markup gallery page then caught and verified the
same behavior end to end: the old faceted circular arc had a 37-pixel top
plateau, while corrected ProGPU and Skia captures both measure 16 pixels.
Circular, elliptical, and rotated arcs are smooth in the matched contact sheet.

The final retained-cache smoke renders two stable presentations, performs an
explicit factory completion wait, renders the same scene again, and performs a
second wait. The third presentation remains a scene-cache hit with one content
draw and four vector vertices. This covers both host invalidation identity and
the public completion-accounting path on a real device.

Matched live catalog captures verify the complete shell rather than the blank
frame in the original failure report. The text sample content crop measures
0.1559 RGB MAE against Skia. The static rounded-border sample measures 0.4444
RGB MAE. After duplicate-stop and analytic ellipse-stroke corrections, the
gradient sample measures 0.9457 RGB MAE over the scene; the 100×100 stroked
ellipse has 0.8326 RGB MAE and no pixel above a 32-level channel difference.
The shader path is exercised for vector, text, texture, retained glyph, image
effect, backdrop, and compatible effect output. This is macOS/Metal evidence;
Windows, Linux, browser, injected device loss, trimming/AOT, leak, and
long-duration multi-page sweep remain open.

A fresh matched `Border_CornerRadius` capture from the merged gitlink confirms
the live sample geometry remains visually aligned. The first five rounded
shapes measure 0.409735 RGB MAE, with 0.5288% of pixels above a 32-level
channel difference; the cyan ellipse fixture measures 0.055579 RGB MAE and
0.1198% above 32. Differences remain concentrated on antialiased edges. Raw
captures are under
`src/artifacts/performance/2026-08-23-final-merge-gallery/`.

The live AutoSuggestBox popup produced an opt-in scene dump at frame 34 with
an Acrylic/HostBackdrop command (`blur=60`, `luminosity alpha=0.847`) and an
offscreen-render cache-miss reason, proving the GPU capture route was active.
The dump identified an identity backdrop transform next to clips at
`[2,0,0,2,8,210]`; after correction the popup covers its complete transformed
height. A matched 988×768 capture measures 0.4985 RGB MAE over the full window,
2.3644 over the popup region, and 2.2757 over its formerly broken lower region
against Skia. The corrected diagnostic frame reports 2.669 ms total and
1.146 ms render-pass CPU. These are live parity diagnostics, not a standalone
effects throughput benchmark.

## Benchmark validation

- All 112 primary ProGPU/Skia processes completed: eight fresh processes for
  each backend across seven scenarios. The first six scenarios use 100
  blocking samples plus nine 60-frame batches. Effects uses 40 blocking
  samples plus three 20-frame batches, for 100 measured frames per process.
- Every artifact contains semantic and final-target SHA-256 values.
- Cached and sparse ProGPU readbacks are byte-identical to Skia.
- ProGPU reported zero unsupported operations.
- CPU frame submit and bounded-batch throughput are faster than Skia in every
  process of all seven workloads.
- Conservative bounded-batch speedups range from 1.35× for sparse mutation to
  98.40× for effects. Paired-median speedups range from 1.38× to 101.59×.
- Every ProGPU final-target hash matches its corresponding pre-optimization
  artifact where semantics did not change. The effects hash intentionally
  changed when shadow-only source replay and sigma calibration were corrected,
  then remained stable across all eight final processes.
- ProGPU text/path raster differences are smaller than Uno WebGPU differences
  against the same Skia reference.
- The effects fixture measures 3.411 RGB MAE, 19.4972% of pixels above eight
  levels, and 0.5625% above 32; visual inspection confirms correct placement,
  ordering, opacity, and shadow direction with a remaining fixed-tap versus
  Gaussian blur-footprint difference.
- A final-gitlink smoke retained clip hash `2711C481...`, effects semantic hash
  `143132D5...`, and ProGPU/Skia effects pixel hashes `AAF267FF...` /
  `EE3C6A61...`; the clips frame used zero mask passes/textures and both final
  scenarios reported zero unsupported operations.
- The `f51cad0f` retained-update follow-up improves sparse CPU submit from
  0.204890 to 0.191991 ms/frame and sampled retained-page append share from
  4.09% to 0.22%. Eight sparse pairs remain byte-identical to Skia and retain a
  1.37× slowest-pair bounded-total lead. A refreshed confirmation matrix keeps
  ProGPU faster in all seven scenarios with unchanged qualified pixel hashes.
- A rebuild from the exact merged gitlink completed with zero warnings and
  errors. The real-device smoke again reported `center=DC5014FF, frame=7`; a
  standard-shape final sparse run measured 0.261213 ms for ProGPU versus
  0.375557 ms for Skia and was byte-identical.
- The retained-presentation follow-up used eight alternating, binary-isolated
  cached-frame pairs. Relative to `f51cad0f`, the paired medians improve CPU
  frame by 26.0%, record by 47.1%, submit by 19.4%, scene compile by 37.1%,
  batched CPU/frame by 62.1%, and completed batched frame by 12.5%. All eight
  pairs preserve the exact pixel hash and report zero unsupported operations.
- Against Skia, the final cached path is 7.07× faster at CPU submit, 14.17×
  faster at batched CPU/frame, and 2.82× faster at completed batched throughput.
  A forced completion after each tiny submission remains 4.40× slower because
  approximately 1.52 ms is native Metal/WebGPU completion wait; this is kept as
  an explicit residual gap rather than folded into a renderer CPU claim.
- Every final ProGPU hash for cached, sparse, text, paths, images, clips, and
  effects equals its previously inspected and qualified ProGPU artifact. Cached
  and sparse remain byte-identical to Skia.
- A post-merge cached confirmation built from gitlink `77a28482` reports
  0.0474 ms CPU frame, 0.134823 ms completed batched frame, the qualified
  `15DECAB...` pixel hash, and zero unsupported operations.
- The retained-output/queue-future matrix built from merged ProGPU `63561c7e`
  keeps all seven forced-redraw CPU and completed-batch boundaries faster than
  Skia. Five of seven blocking totals also win; cached and sparse remain behind
  only at the per-frame GPU completion boundary and are reported as residual
  synchronization gaps.
- Three fresh forced-redraw stroke pairs cover 1,000 analytic arc/Bézier
  strokes with four solid/dashed style combinations. ProGPU is 14.95× faster
  at median blocking total and 25.70× faster at completed-batch throughput;
  all three runs retain stable backend hashes, equal semantic hashes, zero
  unsupported operations, and zero mask passes. The raw side-by-side measures
  0.9928 SSIM.
- Three fresh forced-redraw materials pairs cover 768 linear/radial gradient
  cells with focal, anisotropic, spread, local-matrix, translucent, and
  duplicate-stop variants. ProGPU is 6.68× faster at median blocking total and
  16.33× faster at completed-batch throughput; it uses two draws, zero masks,
  stable hashes, and zero unsupported operations. The inspected contact sheet
  retains every material variant and measures 0.9670 SSIM.
- Three fresh forced-redraw color-matrix-layer pairs cover one isolated layer
  with 1,536 overlapping primitives, RGB mixing, and a non-identity alpha row.
  ProGPU is 6.14× faster at median blocking total and 33.87× faster at
  completed-batch throughput; all runs preserve the semantic workload, stable
  backend hashes, zero unsupported operations, and zero mask passes. The
  inspected contact sheet measures 0.999721 SSIM. The real-device runtime smoke
  separately proves unchanged content outside the layer, single-layer channel
  mapping, nested double-matrix restoration, and balanced save/restore scopes.
- The built-in WebGPU lane completes a 40-sample forced color-matrix-layer
  smoke with the same semantic hash, zero unsupported operations, a 3.7880 ms
  blocking median, and a 2.84402 ms completed-batch median. It remains
  compatibility evidence rather than a balanced qualification distribution.
- Three fresh forced-redraw blend-layer pairs cover one isolated Multiply layer
  with 1,536 opaque overlapping primitives over gray. ProGPU is 5.20× faster
  at median blocking total and 23.03× faster at completed-batch throughput;
  all runs preserve the semantic workload, stable backend hashes, zero
  unsupported operations, and zero mask passes. The inspected contact sheet
  measures 0.999077 RGB SSIM, and the real-device smoke rejects the former
  per-primitive blend semantics through its overlap pixel while a following
  blue draw proves that restore returns to source-over.
- A post-merge rebuild from gitlink `64271d7f` completed with zero warnings or
  errors. The real-device smoke again passed at frame 12; a short blend-layer
  pair preserved semantic hash `9537804D...716D9`, ProGPU pixel hash
  `2DA4C129...194D`, Skia pixel hash `D91EDA15...C56E`, and zero unsupported
  operations.
- The built-in WebGPU blend-layer smoke completes but measures only 13.47 dB
  mean per-channel RGB PSNR and 0.494672 RGB SSIM against Skia because that
  lane currently treats Multiply as source-over.
  Its timing is excluded from performance claims; this is direct evidence that
  semantic hashes and unsupported-operation counters are necessary but not
  sufficient correctness gates.
- Three fresh forced-redraw unfiltered-isolation-layer pairs cover a clipped
  transparent clear and 1,536 opaque overlapping primitives over gray. ProGPU
  is 5.14× faster at median blocking total and 23.47× faster at completed-batch
  throughput; all runs retain semantic hash `AD6F2629...BD67`, stable backend
  hashes, zero unsupported operations, exact alpha, three ProGPU draws, and no
  mask passes. The inspected output measures 50.65 dB mean per-channel RGB
  PSNR and 0.999293 RGB SSIM against Skia. The real-device smoke separately
  proves that an undrawn clipped gap retains the destination and that drawing
  after restore returns to source-over.
- The failed-before diagnostic read the isolation gap as transparent, emitted
  1,539 draws, and measured 10.58 dB PSNR / 0.417786 RGB SSIM against Skia.
  The built-in WebGPU compatibility smoke preserves the fixed semantics at
  41.14 dB PSNR / 0.993500 RGB SSIM; its short timing distribution is not used
  as a qualification claim.
- A final source rebuild from gitlink `64271d7f` completed the runtime and
  benchmark dependency graphs with zero warnings or errors. The real-device
  smoke passed at frame 12, and a fresh short ProGPU/Skia isolation pair
  preserved semantic hash `AD6F2629...BD67`, pixel hashes
  `DC65A307...9996` / `CDAFE4F7...CF9D`, three ProGPU draws, zero masks, and
  zero unsupported operations.
- Uno's built-in WebGPU lane completes 40-sample forced-redraw smokes for the
  stroke and material scenarios with zero unsupported operations. Its blocking medians are
  17.1597 ms for strokes and 8.6350 ms for materials; balanced eight-process
  promotion remains part of the cross-backend qualification work.

See [performance-results-2026-08-23.md](performance-results-2026-08-23.md) for
the exact values and interpretation boundaries.

## Remaining qualification work

1. Promote the earlier Uno WebGPU context lane to eight balanced fresh-process
   triplets; the primary ProGPU/Skia lane now has eight pairs.
2. Add WebGPU timestamps; managed CPU and Metal traces have been captured, but
   their instrumentation overhead excludes them from comparative timing
   tables.
3. Add scrolling, control-density, first-present, startup, allocation,
   settled-memory, and leak scenarios.
4. Run a systematic SamplesApp page/pixel sweep and a long-duration resize,
   DPI, occlusion, minimize/restore, and device-loss sequence.
5. Run Windows/D3D12, Linux/Vulkan, browser, trimming, and NativeAOT lanes.
6. Complete anisotropic/additive shadows, clip-wide transparent-source blend
   modes, and arbitrary color-filter/effect DAG layer isolation.
