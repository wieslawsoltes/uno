extern alias unofoundation;
extern alias unouwp;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using N = Uno.WebGpu.Native;
using Color = unouwp::Windows.UI.Color;
using FontStretch = unouwp::Windows.UI.Text.FontStretch;
using FontStyle = unouwp::Windows.UI.Text.FontStyle;
using FontWeight = unouwp::Windows.UI.Text.FontWeight;
using Rect = unofoundation::Windows.Foundation.Rect;

var initType = typeof(N.WGPU).Assembly.GetType("Uno.UI.Composition.WebGpu.WebGpuInitDevice", throwOnError: true)!;
using var deviceOwner = (IDisposable)Activator.CreateInstance(
	initType,
	BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
	binder: null,
	args: [N.WGPUTextureFormat.BGRA8Unorm],
	culture: null)!;
var device = (IWebGpuDeviceContext)deviceOwner;

using var factory = (ProGpuDrawingFactory)new ProGpuGraphicsProvider(new ProGpuBackendOptions
{
	FailOnUnsupportedOperation = true,
	Compositor = ProGPU.Scene.CompositorOptions.Default with
	{
		PrimarySampleCount = 1,
		PrecompileBasePipelines = true,
		EnableGpuHitTesting = false,
	},
}).CreateGraphics(device);
var geometryFactory = new ProGpuGeometryFactory();
{
	var geometryBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	geometryBuilder.AddRectangle(new Rect(0, 0, 1, 1));
	using var geometry = geometryBuilder.Build();
	if (geometry is not unouwp::Windows.Graphics.IGeometrySource2D)
	{
		throw new InvalidOperationException("Backend geometries must satisfy Uno's CompositionPath host contract.");
	}
}
var font = new ProGpuFontProvider().GetDefaultFont(new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, 18);
var shaped = font.Shape("ProGPU".AsSpan(), TextDirection.LeftToRight);
var positions = new Vector2[shaped.Count];
var pen = 0f;
for (var i = 0; i < shaped.Count; i++)
{
	positions[i] = new Vector2(8 + pen, 52) + shaped.Offsets[i];
	pen += shaped.Advances[i];
}
var glyphElements = new List<GlyphRunElement>();
font.BuildGlyphRun(geometryFactory, shaped.Glyphs, positions, 0, glyphElements);

using var shadow = factory.CreateDropShadowFilter(1, 1, 1, 1, Color.FromArgb(180, 0, 0, 0));
using var texture = factory.RenderOffscreen(64, 64, drawing =>
{
	drawing.Clear(Color.FromArgb(0, 0, 0, 0));
	drawing.DrawRect(new Rect(8, 8, 48, 48), Color.FromArgb(255, 240, 30, 20));
	drawing.DrawRoundedRect(new Rect(16, 16, 32, 32), new System.Numerics.Vector4(8), Color.FromArgb(255, 20, 80, 220));
	drawing.SaveLayer(shadow);
	drawing.DrawRect(new Rect(48, 48, 4, 4), Color.FromArgb(255, 255, 255, 255));
	drawing.Restore();
	foreach (var element in glyphElements)
	{
		if (element is GlyphOutline outline) drawing.DrawPath(outline.Outline, Color.FromArgb(255, 255, 255, 255), true);
	}
});
var image = await factory.SnapshotAsync(texture);
var pixels = new byte[64 * 64 * 4];
image.CopyPixels(pixels);

static ReadOnlySpan<byte> Pixel(byte[] pixels, int x, int y) => pixels.AsSpan((y * 64 + x) * 4, 4);

var outside = Pixel(pixels, 2, 2).ToArray();
var red = Pixel(pixels, 10, 10).ToArray();
var blue = Pixel(pixels, 32, 32).ToArray();
if (outside[3] > 4 || red[2] < 200 || red[3] < 240 || blue[0] < 180 || blue[3] < 240)
{
	throw new InvalidOperationException($"Unexpected pixels: outside={Convert.ToHexString(outside)}, red={Convert.ToHexString(red)}, blue={Convert.ToHexString(blue)}");
}

RunPresentSmoke(device, factory);
RunStablePresentCacheSmoke(device, factory);
await RunNestedRecordClearSmoke(factory);
await RunDefaultTrimSmoke(factory, geometryFactory);
await RunRoundedDifferenceSmoke(factory, geometryFactory);
await RunReplayScaleSmoke(factory);
await RunTransformCompositionSmoke(factory);
await RunStateStackSmoke(factory);
await RunTransformedStrokeSmoke(factory, geometryFactory);
await RunRoundedGeometrySmoke(factory);
await RunRoundedDifferenceClipSmoke(factory);
await RunNestedRoundedBorderSmoke(factory);
await RunTranslatedGeometryClipSmoke(factory);
RunPrimaryTranslatedGeometryClipSmoke(factory);
RunPrimaryClippedGlyphSmoke(factory, geometryFactory, font);
await RunCombinedGeometryFillSmoke(factory, geometryFactory);

if (ProGpuDiagnostics.UnsupportedOperationCount != 0)
{
	throw new InvalidOperationException($"The smoke scene used {ProGpuDiagnostics.UnsupportedOperationCount} unsupported operations.");
}

Console.WriteLine($"ProGPU runtime smoke passed; center={Convert.ToHexString(blue)}, frame={ProGpuDiagnostics.LastFrame?.FrameNumber ?? 0}.");
foreach (var element in glyphElements) if (element is GlyphOutline outline) outline.Outline.Dispose();

static unsafe void RunPresentSmoke(IWebGpuDeviceContext device, ProGpuDrawingFactory factory)
{
	var descriptor = new N.WGPUTextureDescriptor
	{
		Size = new N.WGPUExtent3D { Width = 64, Height = 64, DepthOrArrayLayers = 1 },
		Format = N.WGPUTextureFormat.BGRA8Unorm,
		MipLevelCount = 1,
		SampleCount = 1,
		Dimension = N.WGPUTextureDimension._2D,
		Usage = N.WGPUTextureUsage.RenderAttachment | N.WGPUTextureUsage.CopySrc,
	};
	var nativeTexture = N.WGPU.wgpuDeviceCreateTexture(device.Device, &descriptor);
	var nativeView = N.WGPU.wgpuTextureCreateView(nativeTexture, null);
	using var target = new SmokeTarget(nativeView, 64, 64);
	var recorder = factory.CreateRecording();
	recorder.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 15, 25, 35));
	using var record = recorder.Finish();
	using (var present = factory.BeginPresent(target))
	{
		record.Replay(present);
		present.DrawLine(new Vector2(0, 0), new Vector2(63, 63), Color.FromArgb(255, 250, 250, 250), 2, true);
	}
	_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
	N.WGPU.wgpuTextureViewRelease(nativeView);
	N.WGPU.wgpuTextureDestroy(nativeTexture);
	N.WGPU.wgpuTextureRelease(nativeTexture);
}

static unsafe void RunStablePresentCacheSmoke(IWebGpuDeviceContext device, ProGpuDrawingFactory factory)
{
	var descriptor = new N.WGPUTextureDescriptor
	{
		Size = new N.WGPUExtent3D { Width = 64, Height = 64, DepthOrArrayLayers = 1 },
		Format = N.WGPUTextureFormat.BGRA8Unorm,
		MipLevelCount = 1,
		SampleCount = 1,
		Dimension = N.WGPUTextureDimension._2D,
		Usage = N.WGPUTextureUsage.RenderAttachment | N.WGPUTextureUsage.CopySrc,
	};
	var nativeTexture = N.WGPU.wgpuDeviceCreateTexture(device.Device, &descriptor);
	var nativeView = N.WGPU.wgpuTextureCreateView(nativeTexture, null);
	using var target = new SmokeTarget(nativeView, 64, 64);
	var recorder = factory.CreateRecording();
	recorder.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 20, 60, 120));
	using var record = recorder.Finish();
	for (var frame = 0; frame < 2; frame++)
	{
		using var present = factory.BeginPresent(target);
		present.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(present);
	}
	_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
	var metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { SceneCacheHit: true, DrawCallCount: 1, VectorVertexCount: 4 })
	{
		throw new InvalidOperationException(
			$"A stable retained presentation did not use one cached content draw: hit={metrics?.SceneCacheHit}, reason={metrics?.SceneCacheMissReason ?? "none"}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	N.WGPU.wgpuTextureViewRelease(nativeView);
	N.WGPU.wgpuTextureDestroy(nativeTexture);
	N.WGPU.wgpuTextureRelease(nativeTexture);
}

static async Task RunNestedRecordClearSmoke(ProGpuDrawingFactory factory)
{
	var recorder = factory.CreateRecording();
	recorder.Clear(Color.FromArgb(255, 210, 30, 20));
	recorder.DrawRect(new Rect(20, 20, 24, 24), Color.FromArgb(255, 20, 70, 220));
	using var record = recorder.Finish();
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		// A leading clear can become the attachment clear, but the same record
		// replayed after existing content must retain replacement semantics.
		drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 20, 180, 40));
		record.Replay(drawing);
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var outside = Pixel(pixels, 4, 4);
	var center = Pixel(pixels, 32, 32);
	if (outside[2] < 180 || outside[1] > 80 || center[0] < 180 || center[2] > 80)
	{
		throw new InvalidOperationException(
			$"A nested retained clear lost replacement ordering: outside={Convert.ToHexString(outside)}, center={Convert.ToHexString(center)}.");
	}
}

static async Task RunDefaultTrimSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	var builder = geometryFactory.CreatePathBuilder();
	builder.MoveTo(new Vector2(12, 12));
	builder.LineTo(new Vector2(52, 12));
	builder.LineTo(new Vector2(52, 52));
	builder.LineTo(new Vector2(12, 52));
	builder.Close();
	using var source = builder.Build();
	using var untrimmed = source.GetFilledGeometry(default, default);
	if (untrimmed.IsEmpty || untrimmed.SegmentCount != source.SegmentCount)
	{
		throw new InvalidOperationException(
			$"The default Uno trim pair removed path content: source={source.SegmentCount}, untrimmed={untrimmed.SegmentCount}.");
	}

	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawPath(untrimmed, Color.FromArgb(255, 220, 40, 30), true);
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 32, 32)[2] < 180 || Pixel(pixels, 4, 4)[3] > 4)
	{
		throw new InvalidOperationException(
			$"An untrimmed path did not preserve its fill: center={Convert.ToHexString(Pixel(pixels, 32, 32))}, outside={Convert.ToHexString(Pixel(pixels, 4, 4))}.");
	}

	using var stroke = source.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 3,
		StartCap = StrokeCap.Butt,
		EndCap = StrokeCap.Butt,
		DashCap = StrokeCap.Butt,
		LineJoin = StrokeJoin.Round,
		MiterLimit = 10,
	});
	if (stroke.IsEmpty)
	{
		throw new InvalidOperationException("The default Uno trim pair removed a widened stroke.");
	}

	using var strokeTexture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawPath(stroke, Color.FromArgb(255, 30, 80, 220), true);
	});
	var strokePixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(strokeTexture)).CopyPixels(strokePixels);
	if (Pixel(strokePixels, 12, 32)[0] < 180 || Pixel(strokePixels, 32, 32)[3] > 4)
	{
		throw new InvalidOperationException(
			$"An untrimmed widened stroke was lost or filled: edge={Convert.ToHexString(Pixel(strokePixels, 12, 32))}, center={Convert.ToHexString(Pixel(strokePixels, 32, 32))}.");
	}
}

static async Task RunRoundedDifferenceSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	var leftBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	leftBuilder.AddRoundedRectangle(new Rect(2, 3, 40, 30), 6, 6);
	using var left = leftBuilder.Build();
	var rightBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	rightBuilder.AddRoundedRectangle(new Rect(2, 3, 40, 30), 6, 6);
	using var right = rightBuilder.Build();
	using var difference = left.Combine(right, GeometryCombineMode.Difference);
	if (!difference.IsEmpty || difference.SegmentCount != 0)
	{
		throw new InvalidOperationException(
			$"Identical rounded rectangles produced a non-empty Difference geometry: segments={difference.SegmentCount}, bounds={difference.Bounds}.");
	}

	var leftPathBuilder = geometryFactory.CreatePathBuilder();
	leftPathBuilder.MoveTo(new Vector2(4, 4));
	leftPathBuilder.CubicTo(new Vector2(8, 1), new Vector2(36, 1), new Vector2(40, 4));
	leftPathBuilder.LineTo(new Vector2(40, 28));
	leftPathBuilder.LineTo(new Vector2(4, 28));
	leftPathBuilder.Close();
	using var leftPath = leftPathBuilder.Build();
	var rightPathBuilder = geometryFactory.CreatePathBuilder();
	rightPathBuilder.MoveTo(new Vector2(4, 4));
	rightPathBuilder.CubicTo(new Vector2(8, 1), new Vector2(36, 1), new Vector2(40, 4));
	rightPathBuilder.LineTo(new Vector2(40, 28));
	rightPathBuilder.LineTo(new Vector2(4, 28));
	rightPathBuilder.Close();
	using var rightPath = rightPathBuilder.Build();
	using var pathDifference = leftPath.Combine(rightPath, GeometryCombineMode.Difference);
	if (!pathDifference.IsEmpty)
	{
		throw new InvalidOperationException("Structurally identical paths produced a non-empty Difference geometry.");
	}

	var outerBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	outerBuilder.AddRoundedRectangle(new Rect(8, 8, 48, 48), 8, 8);
	using var outer = outerBuilder.Build();
	var innerBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	innerBuilder.AddRoundedRectangle(new Rect(12, 12, 40, 40), 4, 4);
	using var inner = innerBuilder.Build();
	using var ring = outer.Combine(inner, GeometryCombineMode.Difference);
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawPath(ring, Color.FromArgb(255, 220, 40, 30), true);
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 10, 32)[2] < 180 || Pixel(pixels, 32, 32)[3] > 4 || Pixel(pixels, 4, 4)[3] > 4)
	{
		throw new InvalidOperationException(
			$"The direct rounded-ring path lost its edge or hole: edge={Convert.ToHexString(Pixel(pixels, 10, 32))}, center={Convert.ToHexString(Pixel(pixels, 32, 32))}, outside={Convert.ToHexString(Pixel(pixels, 4, 4))}.");
	}
}

static async Task RunReplayScaleSmoke(ProGpuDrawingFactory factory)
{
	var recorder = factory.CreateRecording();
	recorder.DrawRect(new Rect(0, 0, 32, 32), Color.FromArgb(255, 220, 40, 30));
	using var record = recorder.Finish();

	using var oneX = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(drawing);
	});
	var oneXPixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(oneX)).CopyPixels(oneXPixels);
	if (Pixel(oneXPixels, 16, 16)[2] < 180 || Pixel(oneXPixels, 48, 48)[3] > 4)
	{
		throw new InvalidOperationException("A 1x retained-frame replay did not preserve logical coordinates.");
	}

	using var twoX = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		record.Replay(drawing);
		drawing.Restore();
	});
	var twoXPixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(twoX)).CopyPixels(twoXPixels);
	if (Pixel(twoXPixels, 48, 48)[2] < 180 || Pixel(twoXPixels, 48, 48)[3] < 240)
	{
		throw new InvalidOperationException(
			$"A 2x retained-frame replay ignored the presentation transform: pixel={Convert.ToHexString(Pixel(twoXPixels, 48, 48))}.");
	}
}

static async Task RunTransformCompositionSmoke(ProGpuDrawingFactory factory)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		drawing.Translate(8, 4);
		drawing.DrawRect(new Rect(0, 0, 8, 8), Color.FromArgb(255, 220, 40, 30));
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 28, 20)[2] < 180 || Pixel(pixels, 10, 6)[3] > 4)
	{
		throw new InvalidOperationException(
			$"Transform composition did not pre-concatenate like Skia: expected={Convert.ToHexString(Pixel(pixels, 28, 20))}, stale-order={Convert.ToHexString(Pixel(pixels, 10, 6))}.");
	}
}

static async Task RunStateStackSmoke(ProGpuDrawingFactory factory)
{
	var stateRecorder = factory.CreateRecording();
	var initialSaveCount = stateRecorder.SaveCount;
	var restoreCount = stateRecorder.Save();
	stateRecorder.Translate(16, 0);
	stateRecorder.ClipRect(new Rect(0, 0, 16, 16));
	if (restoreCount != initialSaveCount || stateRecorder.SaveCount != initialSaveCount + 1)
	{
		throw new InvalidOperationException(
			$"Save/clip stack contract is broken: initial={initialSaveCount}, returned={restoreCount}, current={stateRecorder.SaveCount}.");
	}
	stateRecorder.RestoreToCount(restoreCount);
	if (stateRecorder.SaveCount != initialSaveCount || stateRecorder.TotalMatrix != Matrix4x4.Identity)
	{
		throw new InvalidOperationException("RestoreToCount did not restore the pre-save transform and clip state.");
	}
	using (stateRecorder.Finish())
	{
	}

	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		var save = drawing.Save();
		drawing.Translate(16, 0);
		drawing.ClipRect(new Rect(0, 0, 16, 16));
		drawing.DrawRect(new Rect(0, 0, 32, 16), Color.FromArgb(255, 220, 40, 30));
		drawing.RestoreToCount(save);
		drawing.DrawRect(new Rect(0, 16, 16, 16), Color.FromArgb(255, 30, 80, 220));
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 8, 24)[0] < 180 || Pixel(pixels, 24, 24)[3] > 4)
	{
		throw new InvalidOperationException(
			$"A clipped sibling leaked its transform: expected={Convert.ToHexString(Pixel(pixels, 8, 24))}, leaked={Convert.ToHexString(Pixel(pixels, 24, 24))}.");
	}
}

static async Task RunTransformedStrokeSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	var builder = geometryFactory.CreatePrimitiveGeometryBuilder();
	builder.AddRectangle(new Rect(4, 4, 24, 24));
	using var rectangle = builder.Build();
	var recorder = factory.CreateRecording();
	recorder.StrokePath(rectangle, Color.FromArgb(255, 220, 40, 30), 2, true);
	using var record = recorder.Finish();
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		record.Replay(drawing);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 32, 8)[2] < 140 || Pixel(pixels, 32, 32)[3] > 4)
	{
		throw new InvalidOperationException(
			$"A transformed retained stroke was lost or filled: edge={Convert.ToHexString(Pixel(pixels, 32, 8))}, center={Convert.ToHexString(Pixel(pixels, 32, 32))}.");
	}
}

static async Task RunRoundedGeometrySmoke(ProGpuDrawingFactory factory)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawRoundedRectBorder(
			new Rect(4, 4, 24, 24), new Vector4(4),
			new Rect(8, 8, 16, 16), new Vector4(2),
			Color.FromArgb(255, 220, 40, 30), true);
		drawing.Save();
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(36, 4, 24, 24),
				TopLeft = new Vector2(4),
				TopRight = new Vector2(4),
				BottomRight = new Vector2(4),
				BottomLeft = new Vector2(4),
			},
			ClipOperation.Intersect, true);
		drawing.DrawRect(new Rect(32, 0, 32, 32), Color.FromArgb(255, 30, 80, 220), true);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 6, 16)[2] < 140 || Pixel(pixels, 16, 16)[3] > 4 ||
		Pixel(pixels, 48, 16)[0] < 140 || Pixel(pixels, 33, 1)[3] > 4)
	{
		throw new InvalidOperationException(
			$"Rounded border/clip parity failed: border={Convert.ToHexString(Pixel(pixels, 6, 16))}, inner={Convert.ToHexString(Pixel(pixels, 16, 16))}, clip={Convert.ToHexString(Pixel(pixels, 48, 16))}, outside={Convert.ToHexString(Pixel(pixels, 33, 1))}.");
	}
}

static async Task RunNestedRoundedBorderSmoke(ProGpuDrawingFactory factory)
{
	var recorder = factory.CreateRecording();
	recorder.DrawRoundedRectBorder(
		new Rect(4, 4, 24, 24), new Vector4(4),
		new Rect(8, 8, 16, 16), new Vector4(2),
		Color.FromArgb(255, 220, 40, 30), true);
	using var record = recorder.Finish();
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		record.Replay(drawing);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 12, 32)[2] < 140 || Pixel(pixels, 32, 32)[3] > 4)
	{
		throw new InvalidOperationException(
			$"A nested transformed rounded border was lost: border={Convert.ToHexString(Pixel(pixels, 12, 32))}, inner={Convert.ToHexString(Pixel(pixels, 32, 32))}.");
	}
}

static async Task RunRoundedDifferenceClipSmoke(ProGpuDrawingFactory factory)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(4, 4, 56, 56),
				TopLeft = new Vector2(10),
				TopRight = new Vector2(10),
				BottomRight = new Vector2(10),
				BottomLeft = new Vector2(10),
			},
			ClipOperation.Intersect, true);
		drawing.Save();
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(8, 8, 48, 48),
				TopLeft = new Vector2(6),
				TopRight = new Vector2(6),
				BottomRight = new Vector2(6),
				BottomLeft = new Vector2(6),
			},
			ClipOperation.Difference, true);
		drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 220, 40, 30), true);
		drawing.Restore();
		// Popping only the Difference scope must restore the original outer
		// rounded clip; this also exercises the adapter's adjacent-ring
		// coalescing lifetime rather than only its final pixels.
		drawing.DrawRect(new Rect(24, 24, 16, 16), Color.FromArgb(255, 30, 80, 220), true);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 6, 32)[2] < 140 || Pixel(pixels, 32, 32)[0] < 140 || Pixel(pixels, 4, 4)[3] > 16)
	{
		throw new InvalidOperationException(
			$"A rounded Difference clip lost its ring semantics: edge={Convert.ToHexString(Pixel(pixels, 6, 32))}, center={Convert.ToHexString(Pixel(pixels, 32, 32))}, corner={Convert.ToHexString(Pixel(pixels, 4, 4))}.");
	}
}

static async Task RunTranslatedGeometryClipSmoke(ProGpuDrawingFactory factory)
{
	var childRecorder = factory.CreateRecording();
	childRecorder.Save();
	childRecorder.ClipRoundRect(
		new RoundRectangle
		{
			Rect = new Rect(1, 1, 22, 22),
			TopLeft = new Vector2(3),
			TopRight = new Vector2(3),
			BottomRight = new Vector2(3),
			BottomLeft = new Vector2(3),
		},
		ClipOperation.Intersect,
		true);
	childRecorder.DrawRect(new Rect(2, 2, 20, 20), Color.FromArgb(255, 220, 40, 30));
	childRecorder.Restore();
	using var child = childRecorder.Finish();

	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		drawing.Translate(8, 4);
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(0, 0, 24, 24),
				TopLeft = new Vector2(4),
				TopRight = new Vector2(4),
				BottomRight = new Vector2(4),
				BottomLeft = new Vector2(4),
			},
			ClipOperation.Intersect,
			true);
		child.Replay(drawing);
		drawing.DrawRect(new Rect(4, 4, 4, 4), Color.FromArgb(255, 30, 80, 220));
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 32, 32)[2] < 140 || Pixel(pixels, 26, 18)[0] < 140 || Pixel(pixels, 10, 6)[3] > 4)
	{
		throw new InvalidOperationException(
			$"A translated geometry clip masked its retained child incorrectly: child={Convert.ToHexString(Pixel(pixels, 32, 32))}, sibling={Convert.ToHexString(Pixel(pixels, 26, 18))}, outside={Convert.ToHexString(Pixel(pixels, 10, 6))}.");
	}
}

static unsafe void RunPrimaryTranslatedGeometryClipSmoke(ProGpuDrawingFactory factory)
{
	var context = (WgpuContext)typeof(ProGpuDrawingFactory)
		.GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
		.GetValue(factory)!;
	using var targetTexture = new GpuTexture(
		context,
		64,
		64,
		TextureFormat.Bgra8Unorm,
		TextureUsage.RenderAttachment | TextureUsage.CopySrc,
		"Uno primary clip regression");
	using var target = new SmokeTarget((nint)targetTexture.ViewPtr, 64, 64);

	var childRecorder = factory.CreateRecording();
	childRecorder.Save();
	childRecorder.ClipRoundRect(
		new RoundRectangle
		{
			Rect = new Rect(1, 1, 22, 22),
			TopLeft = new Vector2(3),
			TopRight = new Vector2(3),
			BottomRight = new Vector2(3),
			BottomLeft = new Vector2(3),
		},
		ClipOperation.Intersect,
		true);
	childRecorder.DrawRect(new Rect(2, 2, 20, 20), Color.FromArgb(255, 220, 40, 30));
	childRecorder.Restore();
	using var child = childRecorder.Finish();

	using (var drawing = factory.BeginPresent(target))
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		drawing.Translate(8.5f, 4);
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(0, 0, 24, 24),
				TopLeft = new Vector2(4),
				TopRight = new Vector2(4),
				BottomRight = new Vector2(4),
				BottomLeft = new Vector2(4),
			},
			ClipOperation.Intersect,
			true);
		child.Replay(drawing);
		drawing.DrawRect(new Rect(4, 4, 4, 4), Color.FromArgb(255, 30, 80, 220));
		drawing.Restore();
	}

	var pixels = new byte[64 * 64 * 4];
	using var readback = new GpuTextureReadbackBuffer(context);
	fixed (byte* destination = pixels)
	{
		if (!readback.TryReadTextureRows(targetTexture, 64, 64, destination, 64 * 4))
		{
			throw new InvalidOperationException($"Primary target readback failed ({readback.LastMapStatus}).");
		}
	}
	if (Pixel(pixels, 32, 32)[2] < 140 || Pixel(pixels, 26, 18)[0] < 140 || Pixel(pixels, 10, 6)[3] > 4)
	{
		throw new InvalidOperationException(
			$"The primary renderer masked a translated retained child incorrectly: child={Convert.ToHexString(Pixel(pixels, 32, 32))}, sibling={Convert.ToHexString(Pixel(pixels, 26, 18))}, outside={Convert.ToHexString(Pixel(pixels, 10, 6))}.");
	}
}

static unsafe void RunPrimaryClippedGlyphSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory, IFont font)
{
	var shaped = font.Shape("Apply".AsSpan(), TextDirection.LeftToRight);
	var positions = new Vector2[shaped.Count];
	var pen = 0f;
	for (var index = 0; index < shaped.Count; index++)
	{
		positions[index] = new Vector2(2 + pen, 19) + shaped.Offsets[index];
		pen += shaped.Advances[index];
	}
	var elements = new List<GlyphRunElement>();
	font.BuildGlyphRun(geometryFactory, shaped.Glyphs, positions, 0, elements);

	var childRecorder = factory.CreateRecording();
	foreach (var element in elements)
	{
		if (element is GlyphOutline outline)
		{
			childRecorder.DrawPath(outline.Outline, Color.FromArgb(255, 220, 40, 30), true);
		}
	}
	using var child = childRecorder.Finish();

	var context = (WgpuContext)typeof(ProGpuDrawingFactory)
		.GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
		.GetValue(factory)!;
	using var targetTexture = new GpuTexture(
		context,
		128,
		64,
		TextureFormat.Bgra8Unorm,
		TextureUsage.RenderAttachment | TextureUsage.CopySrc,
		"Uno primary clipped glyph regression");
	using var target = new SmokeTarget((nint)targetTexture.ViewPtr, 128, 64);
	using (var drawing = factory.BeginPresent(target))
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.Scale(2, 2);
		drawing.Translate(8.5f, 4);
		drawing.ClipRoundRect(
			new RoundRectangle
			{
				Rect = new Rect(0, 0, 52, 24),
				TopLeft = new Vector2(4.5f),
				TopRight = new Vector2(4.5f),
				BottomRight = new Vector2(4.5f),
				BottomLeft = new Vector2(4.5f),
			},
			ClipOperation.Intersect,
			true);
		child.Replay(drawing);
		drawing.Restore();
	}

	var pixels = new byte[128 * 64 * 4];
	using var readback = new GpuTextureReadbackBuffer(context);
	fixed (byte* destination = pixels)
	{
		if (!readback.TryReadTextureRows(targetTexture, 128, 64, destination, 128 * 4))
		{
			throw new InvalidOperationException($"Primary glyph target readback failed ({readback.LastMapStatus}).");
		}
	}
	var coveredPixels = 0;
	for (var pixelIndex = 0; pixelIndex < pixels.Length; pixelIndex += 4)
	{
		if (pixels[pixelIndex + 2] > 80 && pixels[pixelIndex + 3] > 20)
		{
			coveredPixels++;
		}
	}
	foreach (var element in elements)
	{
		if (element is GlyphOutline outline)
		{
			outline.Outline.Dispose();
		}
	}
	if (coveredPixels < 40)
	{
		throw new InvalidOperationException($"A clipped retained glyph run disappeared on the primary renderer: coveredPixels={coveredPixels}.");
	}
}

static async Task RunCombinedGeometryFillSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	var outerBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	outerBuilder.AddRoundedRectangle(new Rect(4, 4, 24, 24), 4, 4);
	using var outer = outerBuilder.Build();
	var innerBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	innerBuilder.AddRoundedRectangle(new Rect(8, 8, 16, 16), 2, 2);
	using var inner = innerBuilder.Build();
	using var ring = outer.Combine(inner, GeometryCombineMode.Difference);
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawPath(ring, Color.FromArgb(255, 220, 40, 30), true);
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 6, 16)[2] < 140 || Pixel(pixels, 16, 16)[3] > 4)
	{
		throw new InvalidOperationException(
			$"A combined geometry fill was lost: ring={Convert.ToHexString(Pixel(pixels, 6, 16))}, hole={Convert.ToHexString(Pixel(pixels, 16, 16))}.");
	}
}

sealed class SmokeTarget(nint view, int width, int height) : IWebGpuRenderTarget
{
	public nint ColorView { get; } = view;
	public int Width { get; } = width;
	public int Height { get; } = height;
	public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Bgra8888;
	public void Dispose() { }
}
