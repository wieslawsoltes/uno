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

var backendOptions = new ProGpuBackendOptions
{
	FailOnUnsupportedOperation = true,
	Compositor = ProGPU.Scene.CompositorOptions.Default with
	{
		PrimarySampleCount = 1,
		PrecompileBasePipelines = true,
		EnableGpuHitTesting = false,
	},
};
var provider = new ProGpuGraphicsProvider(backendOptions);
RunProviderContextContractSmoke(provider);
await RunBorrowedDeviceOwnershipSmoke(device, provider);
using var factory = (ProGpuDrawingFactory)provider.CreateGraphics(device);
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
await RunHostBackdropSmoke(device, factory);
await RunTransformedHostBackdropSmoke(factory);
await RunEffectLayerBoundsSmoke(factory);
await RunNestedEffectLayerBoundsSmoke(factory);
await RunEffectPrimitiveBoundsSmoke(factory, geometryFactory);
await RunColorMatrixLayerSmoke(factory);
await RunBlendModeLayerSmoke(factory);
await RunUnfilteredLayerSmoke(factory);
RunStablePresentCacheSmoke(device, factory);
await RunNestedRecordClearSmoke(factory);
await RunDefaultTrimSmoke(factory, geometryFactory);
await RunAnalyticEllipseStrokeSmoke(factory, geometryFactory);
await RunNativeArcStrokeSmoke(factory, geometryFactory);
await RunRoundedDifferenceSmoke(factory, geometryFactory);
await RunReplayScaleSmoke(factory);
await RunTransformCompositionSmoke(factory);
await RunStateStackSmoke(factory);
await RunTransformedStrokeSmoke(factory, geometryFactory);
await RunRoundedGeometrySmoke(factory);
await RunRoundedDifferenceClipSmoke(factory);
await RunRoundedRectangularDifferenceClipSmoke(factory);
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

static void RunProviderContextContractSmoke(ProGpuGraphicsProvider provider)
{
	if (provider.PreferredContexts.Count != 1 ||
		provider.PreferredContexts[0] != GraphicsContextKind.WebGpu ||
		(object)provider is not IGraphicsProvider<IWebGpuDeviceContext> ||
		(object)provider is IGraphicsProvider<IGraphicsContext> ||
		(object)provider is IGraphicsProvider<IMetalDeviceContext> ||
		(object)provider is IGraphicsProvider<IGLDeviceContext>)
	{
		throw new InvalidOperationException(
			"The ProGPU provider must negotiate exactly the typed WebGPU device context.");
	}
}

static async Task RunColorMatrixLayerSmoke(ProGpuDrawingFactory factory)
{
	var swapRedAndBlue = factory.CreateColorMatrixColorFilter(
	[
		0, 0, 1, 0, 0,
		0, 1, 0, 0, 0,
		1, 0, 0, 0, 0,
		0, 0, 0, 1, 0,
	]);
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(255, 10, 20, 30));
		drawing.DrawRect(new Rect(4, 4, 12, 12), Color.FromArgb(255, 20, 220, 30));
		drawing.SaveLayer(swapRedAndBlue);
		drawing.DrawRect(new Rect(20, 16, 28, 32), Color.FromArgb(255, 240, 20, 10));
		drawing.SaveLayer(swapRedAndBlue);
		drawing.DrawRect(new Rect(28, 24, 12, 16), Color.FromArgb(255, 240, 20, 10));
		drawing.Restore();
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var outside = Pixel(pixels, 8, 8);
	var transformed = Pixel(pixels, 24, 32);
	var nested = Pixel(pixels, 32, 32);
	if (outside[1] < 200 || outside[0] > 50 || outside[2] > 50 ||
		transformed[0] < 220 || transformed[1] > 50 || transformed[2] > 50 ||
		transformed[3] < 245 ||
		nested[2] < 220 || nested[0] > 50 || nested[1] > 50 || nested[3] < 245)
	{
		throw new InvalidOperationException(
			$"Color-matrix layer lost isolation, nesting, or channel mapping: outside={Convert.ToHexString(outside)}, transformed={Convert.ToHexString(transformed)}, nested={Convert.ToHexString(nested)}.");
	}
}

static async Task RunBlendModeLayerSmoke(ProGpuDrawingFactory factory)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(255, 128, 128, 128));
		drawing.SaveLayer(BlendMode.Multiply);
		drawing.DrawRect(new Rect(12, 16, 28, 24), Color.FromArgb(255, 255, 0, 0));
		drawing.DrawRect(new Rect(28, 16, 24, 24), Color.FromArgb(255, 0, 255, 0));
		drawing.Restore();
		drawing.DrawRect(new Rect(54, 16, 8, 24), Color.FromArgb(255, 0, 0, 255));
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var outside = Pixel(pixels, 4, 4);
	var redOnly = Pixel(pixels, 20, 24);
	var overlap = Pixel(pixels, 32, 24);
	var followingContent = Pixel(pixels, 58, 24);
	if (outside[0] is < 120 or > 136 || outside[1] is < 120 or > 136 || outside[2] is < 120 or > 136 ||
		redOnly[2] is < 120 or > 136 || redOnly[0] > 12 || redOnly[1] > 12 || redOnly[3] < 245 ||
		overlap[1] is < 120 or > 136 || overlap[0] > 12 || overlap[2] > 12 || overlap[3] < 245 ||
		followingContent[0] < 245 || followingContent[1] > 12 || followingContent[2] > 12 || followingContent[3] < 245)
	{
		throw new InvalidOperationException(
			$"Blend-mode layer was not isolated/restored before compositing: outside={Convert.ToHexString(outside)}, redOnly={Convert.ToHexString(redOnly)}, overlap={Convert.ToHexString(overlap)}, following={Convert.ToHexString(followingContent)}.");
	}
}

static async Task RunUnfilteredLayerSmoke(ProGpuDrawingFactory factory)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(255, 128, 128, 128));
		drawing.SaveLayer();
		drawing.ClipRect(new Rect(8, 12, 44, 32), antialias: true);
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawRect(new Rect(12, 16, 28, 24), Color.FromArgb(255, 255, 0, 0));
		drawing.DrawRect(new Rect(28, 16, 24, 24), Color.FromArgb(255, 0, 255, 0));
		drawing.Restore();
		drawing.DrawRect(new Rect(54, 16, 8, 24), Color.FromArgb(255, 0, 0, 255));
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var outside = Pixel(pixels, 4, 4);
	var isolationGap = Pixel(pixels, 10, 14);
	var redOnly = Pixel(pixels, 20, 24);
	var overlap = Pixel(pixels, 32, 24);
	var followingContent = Pixel(pixels, 58, 24);
	if (outside[0] is < 120 or > 136 || outside[1] is < 120 or > 136 || outside[2] is < 120 or > 136 || outside[3] < 245 ||
		isolationGap[0] is < 120 or > 136 || isolationGap[1] is < 120 or > 136 || isolationGap[2] is < 120 or > 136 || isolationGap[3] < 245 ||
		redOnly[2] < 245 || redOnly[0] > 12 || redOnly[1] > 12 || redOnly[3] < 245 ||
		overlap[1] < 245 || overlap[0] > 12 || overlap[2] > 12 || overlap[3] < 245 ||
		followingContent[0] < 245 || followingContent[1] > 12 || followingContent[2] > 12 || followingContent[3] < 245)
	{
		throw new InvalidOperationException(
			$"Unfiltered layer was not isolated/restored before compositing: outside={Convert.ToHexString(outside)}, isolationGap={Convert.ToHexString(isolationGap)}, redOnly={Convert.ToHexString(redOnly)}, overlap={Convert.ToHexString(overlap)}, following={Convert.ToHexString(followingContent)}.");
	}
}

static async Task RunBorrowedDeviceOwnershipSmoke(
	IWebGpuDeviceContext device,
	ProGpuGraphicsProvider provider)
{
	using (var borrowedFactory = (ProGpuDrawingFactory)provider.CreateGraphics(device))
	using (var texture = borrowedFactory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(255, 25, 50, 100));
		drawing.DrawRect(new Rect(16, 16, 32, 32), Color.FromArgb(255, 220, 40, 30));
	}))
	{
		var pixels = new byte[64 * 64 * 4];
		(await borrowedFactory.SnapshotAsync(texture)).CopyPixels(pixels);
		if (Pixel(pixels, 32, 32)[2] < 180 || Pixel(pixels, 4, 4)[0] < 80)
		{
			throw new InvalidOperationException("The disposable borrowed-device factory did not render before release.");
		}
	}

	RunPostFactoryDisposeDeviceSmoke(device);
}

static unsafe void RunPostFactoryDisposeDeviceSmoke(IWebGpuDeviceContext device)
{
	var descriptor = new N.WGPUTextureDescriptor
	{
		Size = new N.WGPUExtent3D { Width = 4, Height = 4, DepthOrArrayLayers = 1 },
		Format = N.WGPUTextureFormat.BGRA8Unorm,
		MipLevelCount = 1,
		SampleCount = 1,
		Dimension = N.WGPUTextureDimension._2D,
		Usage = N.WGPUTextureUsage.RenderAttachment | N.WGPUTextureUsage.CopySrc,
	};
	var nativeTexture = N.WGPU.wgpuDeviceCreateTexture(device.Device, &descriptor);
	if (nativeTexture == 0)
	{
		throw new InvalidOperationException(
			"Disposing ProGPU released the Uno-owned WebGPU device instead of only its borrowed lifetime.");
	}

	_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
	N.WGPU.wgpuTextureDestroy(nativeTexture);
	N.WGPU.wgpuTextureRelease(nativeTexture);
}

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

static async Task RunHostBackdropSmoke(
	IWebGpuDeviceContext device,
	ProGpuDrawingFactory factory)
{
	using var effect = factory.CreateEffectFilter(
		new BlurEffectNode(new SourceInput(), 6f, true),
		new Rect(8, 8, 48, 48)) ??
		throw new InvalidOperationException("The ProGPU backend rejected a host-backdrop blur graph.");
	using var record = CreateHostBackdropRecord(factory, effect);
	using var texture = factory.RenderOffscreen(64, 64, record.Replay);
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var outside = Pixel(pixels, 4, 32);
	var blurredBoundary = Pixel(pixels, 32, 16);
	var foreground = Pixel(pixels, 31, 31);
	if (outside[2] < 247 || outside[0] > 8 ||
		blurredBoundary[2] is < 24 or > 224 ||
		blurredBoundary[0] is < 24 or > 224 ||
		foreground[1] < 247 || foreground[0] > 8 || foreground[2] > 8)
	{
		throw new InvalidOperationException(
			$"Host backdrop capture lost blur or ordering: outside={Convert.ToHexString(outside)}, blurred={Convert.ToHexString(blurredBoundary)}, foreground={Convert.ToHexString(foreground)}.");
	}

	RunHostBackdropPresentSmoke(device, factory, record);
}

static unsafe void RunHostBackdropPresentSmoke(
	IWebGpuDeviceContext device,
	ProGpuDrawingFactory factory,
	IRenderRecord record)
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
	using (var present = factory.BeginPresent(target))
	{
		record.Replay(present);
	}
	_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
	N.WGPU.wgpuTextureViewRelease(nativeView);
	N.WGPU.wgpuTextureDestroy(nativeTexture);
	N.WGPU.wgpuTextureRelease(nativeTexture);
}

static IRenderRecord CreateHostBackdropRecord(
	ProGpuDrawingFactory factory,
	IEffectFilter effect)
{
	var recorder = factory.CreateRecording();
	recorder.DrawRect(new Rect(0, 0, 32, 64), Color.FromArgb(255, 255, 0, 0));
	recorder.DrawRect(new Rect(32, 0, 32, 64), Color.FromArgb(255, 0, 0, 255));
	var restoreCount = recorder.Save();
	recorder.ClipRect(new Rect(8, 8, 48, 48));
	recorder.DrawEffectBackdrop(effect, 1f);
	recorder.RestoreToCount(restoreCount);
	recorder.DrawRect(new Rect(28, 28, 8, 8), Color.FromArgb(255, 0, 255, 0));
	return recorder.Finish();
}

static async Task RunTransformedHostBackdropSmoke(
	ProGpuDrawingFactory factory)
{
	using var effect = factory.CreateEffectFilter(
		new BlurEffectNode(new SourceInput(), 6f, true),
		new Rect(0, 0, 48, 48)) ??
		throw new InvalidOperationException("The ProGPU backend rejected a transformed host-backdrop blur graph.");
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.DrawRect(new Rect(0, 0, 32, 64), Color.FromArgb(255, 255, 0, 0));
		drawing.DrawRect(new Rect(32, 0, 32, 64), Color.FromArgb(255, 0, 0, 255));
		var restoreCount = drawing.Save();
		drawing.Translate(8, 8);
		drawing.ClipRect(new Rect(0, 0, 48, 48));
		drawing.DrawEffectBackdrop(effect, 1f);
		drawing.RestoreToCount(restoreCount);
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var transformedBoundary = Pixel(pixels, 32, 52);
	if (transformedBoundary[2] is < 24 or > 224 ||
		transformedBoundary[0] is < 24 or > 224)
	{
		throw new InvalidOperationException(
			$"A transformed host backdrop lost its placement: boundary={Convert.ToHexString(transformedBoundary)}.");
	}
}

static async Task RunEffectLayerBoundsSmoke(ProGpuDrawingFactory factory)
{
	using var shadow = factory.CreateDropShadowFilter(
		8,
		0,
		0,
		0,
		Color.FromArgb(255, 220, 30, 20));
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.SaveLayer(shadow);
		drawing.Translate(16, 16);
		drawing.DrawRect(new Rect(4, 4, 16, 12), Color.FromArgb(255, 20, 70, 220));
		drawing.Restore();
		// Uno's non-analytic fallback replays the source after the shadow-only layer.
		drawing.DrawRect(new Rect(20, 20, 16, 12), Color.FromArgb(255, 20, 70, 220));
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var content = Pixel(pixels, 24, 26);
	var shadowOnly = Pixel(pixels, 40, 26);
	var staleOrigin = Pixel(pixels, 8, 6);
	if (content[0] < 180 || content[2] > 80 || content[3] < 240 ||
		shadowOnly[2] < 180 || shadowOnly[0] > 80 || shadowOnly[3] < 240 ||
		staleOrigin[3] > 4)
	{
		throw new InvalidOperationException(
			$"An effect layer lost its non-zero content bounds: content={Convert.ToHexString(content)}, shadow={Convert.ToHexString(shadowOnly)}, staleOrigin={Convert.ToHexString(staleOrigin)}.");
	}
}

static async Task RunNestedEffectLayerBoundsSmoke(ProGpuDrawingFactory factory)
{
	using var outerShadow = factory.CreateDropShadowFilter(
		8,
		0,
		0,
		0,
		Color.FromArgb(255, 220, 30, 20));
	using var innerShadow = factory.CreateDropShadowFilter(
		4,
		0,
		0,
		0,
		Color.FromArgb(255, 255, 255, 255));
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.SaveLayer(outerShadow);
		drawing.SaveLayer(innerShadow);
		drawing.DrawRect(new Rect(12, 20, 12, 8), Color.FromArgb(255, 255, 255, 255));
		drawing.Restore();
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var nestedShadow = Pixel(pixels, 30, 24);
	var omittedSource = Pixel(pixels, 16, 24);
	var staleOrigin = Pixel(pixels, 4, 4);
	if (nestedShadow[2] < 180 || nestedShadow[0] > 80 || nestedShadow[3] < 240 ||
		omittedSource[3] > 4 || staleOrigin[3] > 4)
	{
		throw new InvalidOperationException(
			$"Nested shadow-only layers lost their propagated output bounds: shadow={Convert.ToHexString(nestedShadow)}, source={Convert.ToHexString(omittedSource)}, staleOrigin={Convert.ToHexString(staleOrigin)}.");
	}
}

static async Task RunEffectPrimitiveBoundsSmoke(
	ProGpuDrawingFactory factory,
	ProGpuGeometryFactory geometryFactory)
{
	using var shadow = factory.CreateDropShadowFilter(
		8,
		0,
		0,
		0,
		Color.FromArgb(255, 220, 30, 20));
	var builder = geometryFactory.CreatePrimitiveGeometryBuilder();
	builder.AddRectangle(new Rect(12, 20, 12, 8));
	using var geometry = builder.Build();
	var shader = factory.CreateLinearGradientShader(
		new Vector2(12, 20),
		new Vector2(24, 28),
		[Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 180, 220, 255)],
		[0f, 1f],
		GradientTileMode.Clamp,
		Matrix3x2.Identity);
	var imagePixels = new byte[12 * 8 * 4];
	Array.Fill(imagePixels, (byte)255);
	using var image = factory.CreateTexture(12, 8, imagePixels);
	var tint = factory.CreateBlendModeColorFilter(
		Color.FromArgb(255, 255, 255, 255),
		BlendMode.SrcIn);

	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"gradient rectangle",
		drawing => drawing.DrawRect(new Rect(12, 20, 12, 8), shader, true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"non-uniform rounded rectangle",
		drawing => drawing.DrawRoundedRect(
			new Rect(12, 20, 12, 8),
			new Vector4(1, 2, 3, 2),
			Color.FromArgb(255, 255, 255, 255),
			true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"rounded border",
		drawing => drawing.DrawRoundedRectBorder(
			new Rect(12, 20, 12, 8),
			new Vector4(2, 3, 2, 3),
			new Rect(14, 22, 8, 4),
			new Vector4(1),
			Color.FromArgb(255, 255, 255, 255),
			true),
		21,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"path fill",
		drawing => drawing.DrawPath(geometry, Color.FromArgb(255, 255, 255, 255), true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"path stroke",
		drawing => drawing.StrokePath(geometry, Color.FromArgb(255, 255, 255, 255), 4, true),
		20,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"line",
		drawing => drawing.DrawLine(
			new Vector2(12, 24),
			new Vector2(24, 24),
			Color.FromArgb(255, 255, 255, 255),
			4,
			true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"image",
		drawing => drawing.DrawImage(image, 12, 20, ImageSampling.Linear, 0.8f, true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"color-filtered image",
		drawing => drawing.DrawImage(image, 12, 20, ImageSampling.Linear, tint, true),
		26,
		24);
	await AssertEffectPrimitiveBounds(
		factory,
		shadow,
		"nine-slice image",
		drawing => drawing.DrawImageNineSlice(
			image,
			new Rect(4, 2, 4, 4),
			new Rect(12, 20, 12, 8),
			centerHollow: false,
			antialias: true),
		26,
		24);
}

static async Task AssertEffectPrimitiveBounds(
	ProGpuDrawingFactory factory,
	IEffectFilter shadow,
	string operation,
	Action<IDrawingSession> draw,
	int sampleX,
	int sampleY)
{
	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.SaveLayer(shadow);
		draw(drawing);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var shadowPixel = Pixel(pixels, sampleX, sampleY);
	var staleOrigin = Pixel(pixels, 4, 4);
	if (shadowPixel[2] < 120 || shadowPixel[0] > 100 || shadowPixel[3] < 160 ||
		staleOrigin[3] > 4)
	{
		throw new InvalidOperationException(
			$"The {operation} effect layer lost its content bounds: shadow={Convert.ToHexString(shadowPixel)}, staleOrigin={Convert.ToHexString(staleOrigin)}.");
	}
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
	using var replacementTarget = new SmokeTarget(nativeView, 64, 64);
	var context = (WgpuContext)typeof(ProGpuDrawingFactory)
		.GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
		.GetValue(factory)!;
	using var mutationTexture = new GpuTexture(
		context,
		1,
		1,
		TextureFormat.Rgba8Unorm,
		TextureUsage.TextureBinding | TextureUsage.CopyDst,
		"Uno retained target mutation gate");
	var recorder = factory.CreateRecording();
	recorder.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 20, 60, 120));
	using var record = recorder.Finish();
	for (var frame = 0; frame < 2; frame++)
	{
		using var present = factory.BeginPresent(target);
		present.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(present);
	}
	factory.WaitForGpuCompletion();
	using (var present = factory.BeginPresent(target))
	{
		present.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(present);
	}
	factory.WaitForGpuCompletion();
	var metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: true, DrawCallCount: 0, VectorVertexCount: 0 })
	{
		throw new InvalidOperationException(
			$"A stable retained presentation did not reuse its populated target: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	mutationTexture.MarkContentsDirty();
	using (var present = factory.BeginPresent(target))
	{
		present.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(present);
	}
	metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: false, DrawCallCount: 1, VectorVertexCount: 4 })
	{
		throw new InvalidOperationException(
			$"A texture mutation did not invalidate retained target reuse: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	mutationTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
	using (var present = factory.BeginPresent(target))
	{
		present.Clear(Color.FromArgb(0, 0, 0, 0));
		record.Replay(present);
	}
	metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: false, DrawCallCount: 1, VectorVertexCount: 4 })
	{
		throw new InvalidOperationException(
			$"An alpha-mode mutation did not invalidate retained target reuse: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	using (var present = factory.BeginPresent(target))
	{
		present.Clear(Color.FromArgb(255, 4, 8, 12));
		record.Replay(present);
	}
	metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: false, DrawCallCount: 1, VectorVertexCount: 4 })
	{
		throw new InvalidOperationException(
			$"A clear-color change did not invalidate retained target reuse: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	using (var present = factory.BeginPresent(replacementTarget))
	{
		present.Clear(Color.FromArgb(255, 4, 8, 12));
		record.Replay(present);
	}
	metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: false, DrawCallCount: 1, VectorVertexCount: 4 })
	{
		throw new InvalidOperationException(
			$"A replacement target did not invalidate retained target reuse: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
	}
	using (var present = factory.BeginPresent(replacementTarget))
	{
		present.Clear(Color.FromArgb(255, 4, 8, 12));
		record.Replay(present);
	}
	metrics = ProGpuDiagnostics.LastFrame;
	if (metrics is not { TargetContentReused: true, DrawCallCount: 0, VectorVertexCount: 0 })
	{
		throw new InvalidOperationException(
			$"A stable replacement target was not reused: reused={metrics?.TargetContentReused}, draws={metrics?.DrawCallCount}, vertices={metrics?.VectorVertexCount}.");
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

static async Task RunAnalyticEllipseStrokeSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	var builder = geometryFactory.CreatePrimitiveGeometryBuilder();
	builder.AddEllipse(new Vector2(32, 32), 24, 20);
	using var ellipse = builder.Build();
	using var stroke = ellipse.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 16,
		StartCap = StrokeCap.Butt,
		EndCap = StrokeCap.Butt,
		DashCap = StrokeCap.Butt,
		LineJoin = StrokeJoin.Round,
		MiterLimit = 10,
	});
	if (stroke.SegmentCount != 16)
	{
		throw new InvalidOperationException(
			$"A solid ellipse stroke was flattened instead of preserving its analytic ring: segments={stroke.SegmentCount}.");
	}

	using var texture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.ClipPath(stroke, antialias: true);
		drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 30, 80, 220), true);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	if (Pixel(pixels, 32, 12)[0] < 180 || Pixel(pixels, 32, 32)[3] > 4 || Pixel(pixels, 4, 4)[3] > 4)
	{
		throw new InvalidOperationException(
			$"An analytic ellipse stroke lost its ring or hole: edge={Convert.ToHexString(Pixel(pixels, 32, 12))}, center={Convert.ToHexString(Pixel(pixels, 32, 32))}, outside={Convert.ToHexString(Pixel(pixels, 4, 4))}.");
	}

	var rectangleBuilder = geometryFactory.CreatePrimitiveGeometryBuilder();
	rectangleBuilder.AddRectangle(new Rect(16, 16, 32, 32));
	using var rectangle = rectangleBuilder.Build();
	using var miterStroke = rectangle.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 16,
		LineJoin = StrokeJoin.Miter,
		MiterLimit = 10,
	});
	using var rectangleTexture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Save();
		drawing.ClipPath(miterStroke, antialias: true);
		drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 30, 80, 220), true);
		drawing.Restore();
	});
	var rectanglePixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(rectangleTexture)).CopyPixels(rectanglePixels);
	if (Pixel(rectanglePixels, 8, 8)[3] < 180 || Pixel(rectanglePixels, 32, 32)[3] > 4)
	{
		throw new InvalidOperationException(
			$"The ellipse fast path changed sharp miter-join semantics: corner={Convert.ToHexString(Pixel(rectanglePixels, 8, 8))}, center={Convert.ToHexString(Pixel(rectanglePixels, 32, 32))}.");
	}
}

static async Task RunNativeArcStrokeSmoke(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
{
	const int size = 256;
	var builder = geometryFactory.CreatePathBuilder();
	builder.MoveTo(new Vector2(32, 160));
	builder.ArcTo(new Vector2(96, 80), 0, false, true, new Vector2(224, 160));
	using var source = builder.Build();
	using var stroke = source.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 8,
		StartCap = StrokeCap.Round,
		EndCap = StrokeCap.Round,
		DashCap = StrokeCap.Round,
		LineJoin = StrokeJoin.Round,
		MiterLimit = 10,
	});

	// Filled-region operations must retain IGeometry semantics even though a direct
	// draw can preserve the analytic centerline and defer stroke expansion to ProGPU.
	if (!stroke.FillContains(new Vector2(128, 80)) || stroke.FillContains(new Vector2(128, 160)))
	{
		throw new InvalidOperationException("A deferred native arc stroke did not preserve filled-region hit testing.");
	}

	using var texture = factory.RenderOffscreen(size, size, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.DrawPath(stroke, Color.FromArgb(255, 30, 80, 220), true);
	});
	var pixels = new byte[size * size * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);

	var maximumCenterlineError = 0f;
	var measuredColumns = 0;
	for (var x = 44; x <= 212; x++)
	{
		var first = -1;
		var last = -1;
		for (var y = 64; y <= 164; y++)
		{
			if (pixels[(y * size + x) * 4 + 3] >= 128)
			{
				first = first < 0 ? y : first;
				last = y;
			}
		}
		if (first < 0)
		{
			continue;
		}

		var normalizedX = (x - 128f) / 96f;
		var expectedY = 160f - 80f * MathF.Sqrt(MathF.Max(0, 1f - normalizedX * normalizedX));
		var actualY = (first + last) * 0.5f;
		maximumCenterlineError = MathF.Max(maximumCenterlineError, MathF.Abs(actualY - expectedY));
		measuredColumns++;
	}

	if (measuredColumns < 160 || maximumCenterlineError > 1.25f)
	{
		throw new InvalidOperationException(
			$"An analytic arc stroke was flattened or lost: columns={measuredColumns}, maxCenterlineError={maximumCenterlineError:F3}px.");
	}

	using var dashedStroke = source.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 6,
		StartCap = StrokeCap.Square,
		EndCap = StrokeCap.Triangle,
		DashCap = StrokeCap.Round,
		LineJoin = StrokeJoin.MiterOrBevel,
		MiterLimit = 4,
		DashArray = [2, 1, 0.5f, 1],
		DashOffset = 0.25f,
	});
	using var dashedTexture = factory.RenderOffscreen(64, 64, drawing =>
	{
		drawing.Clear(Color.FromArgb(0, 0, 0, 0));
		drawing.Scale(0.25f, 0.25f);
		drawing.DrawPath(dashedStroke, Color.FromArgb(255, 220, 80, 30), true);
	});
	var dashedPixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(dashedTexture)).CopyPixels(dashedPixels);
	var covered = 0;
	for (var index = 3; index < dashedPixels.Length; index += 4)
	{
		if (dashedPixels[index] >= 32)
		{
			covered++;
		}
	}
	if (covered < 80 || covered > 700)
	{
		throw new InvalidOperationException($"A native dashed arc stroke produced invalid coverage: pixels={covered}.");
	}

	using var trimmedStroke = source.GetStrokeFillGeometry(new StrokeStyle
	{
		Thickness = 8,
		LineJoin = StrokeJoin.Round,
		MiterLimit = 10,
		TrimStart = 0.2f,
		TrimEnd = 0.8f,
	});
	if (trimmedStroke.SegmentCount <= source.SegmentCount)
	{
		throw new InvalidOperationException(
			$"A trimmed stroke bypassed the filled-geometry fallback: source={source.SegmentCount}, stroke={trimmedStroke.SegmentCount}.");
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

static async Task RunRoundedRectangularDifferenceClipSmoke(ProGpuDrawingFactory factory)
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
			ClipOperation.Intersect,
			true);
		drawing.Save();
		drawing.ClipRect(new Rect(12, 12, 40, 40), ClipOperation.Difference, true);
		drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 220, 40, 30), true);
		drawing.Restore();
		// Restoring the rectangular hole must leave the original rounded outer
		// clip active rather than retaining the coalesced even-odd ring.
		drawing.DrawRect(new Rect(24, 24, 16, 16), Color.FromArgb(255, 30, 80, 220), true);
		drawing.Restore();
	});
	var pixels = new byte[64 * 64 * 4];
	(await factory.SnapshotAsync(texture)).CopyPixels(pixels);
	var ring = Pixel(pixels, 8, 32);
	var restoredCenter = Pixel(pixels, 32, 32);
	var clippedCorner = Pixel(pixels, 4, 4);
	if (ring[2] < 140 || restoredCenter[0] < 140 || clippedCorner[3] > 16)
	{
		throw new InvalidOperationException(
			$"A rounded/rectangular Difference clip lost its restored outer scope: ring={Convert.ToHexString(ring)}, center={Convert.ToHexString(restoredCenter)}, corner={Convert.ToHexString(clippedCorner)}.");
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
