extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Uno.UI.Composition.Drawing;
using N = Uno.WebGpu.Native;
using UColor = unouwp::Windows.UI.Color;
using URect = unofoundation::Windows.Foundation.Rect;
using PRect = ProGPU.Scene.Rect;

namespace Uno.UI.Composition.ProGpu;

public sealed class ProGpuGraphicsProvider : IGraphicsProvider<IWebGpuDeviceContext>
{
	private static readonly GraphicsContextKind[] s_contexts = [GraphicsContextKind.WebGpu];
	private readonly ProGpuBackendOptions _options;

	public ProGpuGraphicsProvider(ProGpuBackendOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));
	public IReadOnlyList<GraphicsContextKind> PreferredContexts => s_contexts;
	public IDrawingFactory CreateGraphics(IWebGpuDeviceContext context) => new ProGpuDrawingFactory(context, _options);
}

public sealed unsafe class ProGpuDrawingFactory : IDrawingFactory<IWebGpuRenderTarget>, IDisposable
{
	// Calibrate Uno's explicit sigma contract to ProGPU's fixed-tap backdrop
	// footprint while retaining the single-pass sampling path.
	private const float BackdropBlurRadiusPerSigma = 2f;
	private readonly WgpuContext _context = new();
	private readonly Compositor _compositor;
	private readonly PictureVisual _presentVisual = new();
	private readonly ProGpuBackendOptions _options;
	private readonly TextureFormat _format;
	private readonly Vector4 _defaultClearColor;
	private GpuTexture? _hostBackdropTarget;
	private IWebGpuRenderTarget? _presentedTarget;
	private nint _presentedColorView;
	private int _presentedWidth;
	private int _presentedHeight;
	private Vector4 _presentedClearColor;
	private long _presentedTextureContentVersion;
	private long _frameNumber;
	private bool _disposed;

	internal ProGpuDrawingFactory(IWebGpuDeviceContext device, ProGpuBackendOptions options)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(options);
		if (OperatingSystem.IsBrowser())
		{
			throw new PlatformNotSupportedException("The native ProGPU provider requires native WebGPU handles. Browser GPUDevice and GPUTextureView objects require a direct browser provider.");
		}
		if (device.Device == 0 || device.Queue == 0)
		{
			throw new ArgumentException("A live native WebGPU device and queue are required.", nameof(device));
		}
		if (options.Compositor.PrimarySampleCount is not (1 or 4))
		{
			throw new ArgumentOutOfRangeException(nameof(options), "ProGPU primary sample count must be 1 or 4.");
		}
		_options = options;
		_format = ToSilkFormat(device.ColorFormat);
		_context.MaximumDeferredQueueSubmissions =
			options.MaximumDeferredQueueSubmissions;
		_context.InitializeExternalNativeDevice(
			new UnoModernWebGpuApi(),
			new UnoBorrowedWebGpuLifetime(device.Device, device.Queue),
			(Device*)device.Device,
			(Queue*)device.Queue,
			_format);
		ProGpuDiagnostics.PublishGraphicsIdentity(GetGraphicsIdentity(device.Adapter));
		_compositor = new Compositor(_context, _format, options.Compositor);
		_defaultClearColor = _compositor.ClearColor;
		_ = ProGpuDiagnostics.NextDeviceGeneration();
	}

	private static ProGpuGraphicsIdentity GetGraphicsIdentity(nint adapter)
	{
		var info = new N.WGPUAdapterInfo();
		if (adapter == 0 || N.WGPU.wgpuAdapterGetInfo(adapter, &info) != N.WGPUStatus.Success)
		{
			return new ProGpuGraphicsIdentity("WebGPU", null, null);
		}

		try
		{
			var api = info.BackendType == N.WGPUBackendType.Undefined
				? "WebGPU"
				: $"WebGPU ({info.BackendType})";
			return new ProGpuGraphicsIdentity(
				api,
				DecodeString(info.Device) ?? DecodeString(info.Description),
				info.AdapterType.ToString());
		}
		finally
		{
			N.WGPU.wgpuAdapterInfoFreeMembers(info);
		}
	}

	private static string? DecodeString(N.WGPUStringView value) =>
		value.Data == 0 || value.Length == 0
			? null
			: Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)value.Data, checked((int)value.Length)));

	internal WgpuContext Context => _context;
	internal ProGpuBackendOptions Options => _options;

	public ICommandRecorder CreateRecording()
	{
		ThrowIfDisposed();
		return new ProGpuCommandRecorder(this);
	}

	public IPresentSession BeginPresent(IWebGpuRenderTarget target)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(target);
		if (target.ColorView == 0) throw new ArgumentException("A live WebGPU color view is required.", nameof(target));
		return new ProGpuPresentSession(this, target, Interlocked.Increment(ref _frameNumber));
	}

	/// <summary>
	/// Waits for all work submitted through this factory and advances ProGPU's
	/// bounded resource-retirement window to the completed submission.
	/// </summary>
	public void WaitForGpuCompletion()
	{
		ThrowIfDisposed();
		_context.WaitIdle();
	}

	internal void Present(IWebGpuRenderTarget target, GpuPicture picture, Vector4? leadingClearColor, bool hasHostBackdrop, long frame, double recordMilliseconds)
	{
		var submit = Stopwatch.StartNew();
		var sceneDumpPath = ProGpuDiagnostics.TryDumpScene(picture, frame);
		var content = picture;
		var transform = Matrix4x4.Identity;
		var clearColor = leadingClearColor ?? _defaultClearColor;
		if (TryGetPresentationContent(picture, out var retainedContent, out var retainedTransform, out var explicitClear))
		{
			content = retainedContent;
			transform = retainedTransform;
			clearColor = explicitClear ?? clearColor;
		}
		_compositor.ClearColor = clearColor;
		var contentChanged = _presentVisual.Update(content, transform);
		var pixelWidth = Math.Max(1, target.Width);
		var pixelHeight = Math.Max(1, target.Height);
		var targetContentReused =
			!contentChanged &&
			!hasHostBackdrop &&
			sceneDumpPath is null &&
			ReferenceEquals(_presentedTarget, target) &&
			_presentedColorView == target.ColorView &&
			_presentedWidth == pixelWidth &&
			_presentedHeight == pixelHeight &&
			_presentedClearColor == clearColor &&
			_presentedTextureContentVersion == _context.TextureContentVersion;
		if (!targetContentReused && hasHostBackdrop)
		{
			_presentedTarget = null;
			EnsureHostBackdropTarget(pixelWidth, pixelHeight);
			_compositor.RenderOffscreen(
				_presentVisual,
				(uint)pixelWidth,
				(uint)pixelHeight,
				_hostBackdropTarget!,
				0f,
				1f,
				clearColor);
			GpuTextureBlitter.Blit(
				_hostBackdropTarget!,
				(TextureView*)target.ColorView,
				_format);
		}
		else if (!targetContentReused)
		{
			_compositor.RenderScene(
				_presentVisual,
				(uint)pixelWidth,
				(uint)pixelHeight,
				(TextureView*)target.ColorView);
			_presentedTarget = target;
			_presentedColorView = target.ColorView;
			_presentedWidth = pixelWidth;
			_presentedHeight = pixelHeight;
			_presentedClearColor = clearColor;
			_presentedTextureContentVersion = _context.TextureContentVersion;
		}
		if (sceneDumpPath is not null)
		{
			ProGpuDiagnostics.AppendCompositorMetrics(sceneDumpPath, _compositor.Metrics);
		}
		submit.Stop();
		var metrics = targetContentReused ? default : _compositor.Metrics;
		ProGpuDiagnostics.Publish(new ProGpuFrameMetrics(frame, recordMilliseconds, submit.Elapsed.TotalMilliseconds, picture.CommandCount, metrics.IncrementalSceneUploadBytes, checked((int)ProGpuDiagnostics.UnsupportedOperationCount))
		{
			CompositorFrameMilliseconds = metrics.FrameTimeMs,
			SceneCompileMilliseconds = metrics.VisualTreeCompileTimeMs,
			GpuUploadMilliseconds = metrics.GpuUploadTimeMs,
			RenderPassMilliseconds = metrics.RenderPassTimeMs,
			DrawCallCount = metrics.DrawCallsCount,
			VectorVertexCount = metrics.VectorVerticesCount,
			VectorIndexCount = metrics.VectorIndicesCount,
			TextVertexCount = metrics.TextVerticesCount,
			SceneUploadBatchCount = metrics.SceneUploadBatchCount,
			SceneUploadCopyCount = metrics.SceneUploadCopyCount,
			MaskRenderPassCount = metrics.MaskRenderPassCount,
			MaskRenderDrawCallCount = metrics.MaskRenderDrawCallCount,
			MaskTexturePeakDemand = metrics.MaskTexturePeakDemand,
			RetainedCompositionPictureCount = metrics.RetainedCompositionPictureCount,
			RetainedCompositionPictureHits = metrics.RetainedCompositionPictureHits,
			RetainedCompositionPictureMisses = metrics.RetainedCompositionPictureMisses,
			RetainedCompositionPictureCompilations = metrics.RetainedCompositionPictureCompilations,
			SceneCacheHit = metrics.SceneCacheHit,
			RenderBundleCacheHit = metrics.RenderBundleCacheHit,
			RenderBundleRecorded = metrics.RenderBundleRecorded,
			RenderBundleDrawCallCount = metrics.RenderBundleDrawCallCount,
			SceneCacheMissReason = metrics.SceneCacheMissReason,
			TargetContentReused = targetContentReused,
		});
	}

	private static bool TryGetPresentationContent(
		GpuPicture picture,
		out GpuPicture content,
		out Matrix4x4 transform,
		out Vector4? clearColor)
	{
		content = picture;
		transform = Matrix4x4.Identity;
		clearColor = null;

		if (picture.CommandCount == 1)
		{
			var draw = picture.GetCommand(0);
			if (draw.Type == RenderCommandType.DrawPicture && draw.Picture is { } child)
			{
				content = child;
				transform = draw.Transform == default ? Matrix4x4.Identity : draw.Transform;
				return true;
			}
		}

		if (picture.CommandCount == 4)
		{
			var push = picture.GetCommand(0);
			var clear = picture.GetCommand(1);
			var pop = picture.GetCommand(2);
			var draw = picture.GetCommand(3);
			if (push.Type == RenderCommandType.PushBlendMode && push.IntParam == (int)GpuBlendMode.Src &&
				clear.Type == RenderCommandType.DrawRect && clear.Brush is SolidColorBrush solid && clear.Pen is null &&
				pop.Type == RenderCommandType.PopBlendMode &&
				draw.Type == RenderCommandType.DrawPicture && draw.Picture is { } child)
			{
				content = child;
				transform = draw.Transform == default ? Matrix4x4.Identity : draw.Transform;
				clearColor = solid.Color;
				return true;
			}
		}

		return false;
	}

	private void EnsureHostBackdropTarget(int width, int height)
	{
		if (_hostBackdropTarget is { } target &&
			target.Width == (uint)width &&
			target.Height == (uint)height)
		{
			return;
		}

		_hostBackdropTarget?.Dispose();
		_hostBackdropTarget = NewTexture(width, height);
	}

	public ITexture RenderOffscreen(int pixelWidth, int pixelHeight, Action<IDrawingSession> render)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(render);
		if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
		using var recorder = new ProGpuCommandRecorder(this);
		render(recorder);
		using var record = (ProGpuRenderRecord)recorder.Finish();
		var texture = NewTexture(pixelWidth, pixelHeight);
		_compositor.RenderOffscreen(
			new PictureVisual(record.Picture),
			(uint)pixelWidth,
			(uint)pixelHeight,
			texture,
			0,
			1f,
			record.LeadingClearColor ?? Vector4.Zero);
		texture.NotifyExternalContentChanged();
		return new ProGpuTexture(texture);
	}

	public Task<IImage> SnapshotAsync(ITexture texture)
	{
		ThrowIfDisposed();
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, _context))
		{
			throw new ArgumentException("Texture was not produced by this ProGPU factory.", nameof(texture));
		}
		var lease = new ProGpuDrawingResourceLease(texture);
		return Task.Run<IImage>(() =>
		{
			using var retained = lease;
			var pixels = new byte[checked(native.PixelWidth * native.PixelHeight * 4)];
			using var readback = new GpuTextureReadbackBuffer(_context);
			fixed (byte* destination = pixels)
			{
				if (!readback.TryReadTextureRows(native.Texture, (uint)native.PixelWidth, (uint)native.PixelHeight, destination, (uint)native.PixelWidth * 4))
				{
					throw new InvalidOperationException($"ProGPU texture readback failed ({readback.LastMapStatus}).");
				}
			}
			if (_format is TextureFormat.Rgba8Unorm or TextureFormat.Rgba8UnormSrgb)
			{
				for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
			}
			return new ProGpuImageEncoderDecoder().CreateImage(native.PixelWidth, native.PixelHeight, pixels);
		});
	}

	public ITexture CreateTexture(IImage image)
	{
		ArgumentNullException.ThrowIfNull(image);
		var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
		image.CopyPixels(pixels);
		return CreateTexture(image.PixelWidth, image.PixelHeight, pixels);
	}

	public ITexture CreateTexture(int pixelWidth, int pixelHeight, ReadOnlySpan<byte> bgraPremul)
	{
		ThrowIfDisposed();
		if (pixelWidth <= 0 || pixelHeight <= 0 || bgraPremul.Length < checked(pixelWidth * pixelHeight * 4)) throw new ArgumentException("Invalid BGRA pixel payload.");
		var rgba = new byte[checked(pixelWidth * pixelHeight * 4)];
		bgraPremul[..rgba.Length].CopyTo(rgba);
		if (_format is TextureFormat.Rgba8Unorm or TextureFormat.Rgba8UnormSrgb)
		{
			for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
		}
		var texture = NewTexture(pixelWidth, pixelHeight);
		texture.WritePixels<byte>(rgba);
		return new ProGpuTexture(texture);
	}

	public IShader CreateLinearGradientShader(Vector2 start, Vector2 end, UColor[] colors, float[] colorPositions, GradientTileMode tileMode, Matrix3x2 localMatrix)
	{
		if (!TryGetBrushCoordinateTransform(localMatrix, out var coordinateTransform))
		{
			return EmptyShader();
		}

		return new ProGpuShader(new LinearGradientBrush(start, end, Stops(colors, colorPositions))
		{
			SpreadMethod = Spread(tileMode),
			CoordinateTransform = coordinateTransform,
		});
	}

	public IShader CreateRadialGradientShader(Vector2 center, Vector2 gradientOrigin, float radiusX, float radiusY, UColor[] colors, float[] colorPositions, GradientTileMode tileMode, Matrix3x2 localMatrix)
	{
		if (!TryGetBrushCoordinateTransform(localMatrix, out var coordinateTransform))
		{
			return EmptyShader();
		}

		return new ProGpuShader(new RadialGradientBrush(center, gradientOrigin, radiusX, radiusY, Stops(colors, colorPositions))
		{
			SpreadMethod = Spread(tileMode),
			CoordinateTransform = coordinateTransform,
		});
	}

	public IColorFilter CreateTintColorFilter(UColor color) => new ProGpuColorFilter(Color(color), null);
	public IColorFilter CreateColorMatrixColorFilter(float[] matrix) => new ProGpuColorFilter(default, (float[])matrix.Clone());
	public IEffectFilter CreateDropShadowFilter(float dx, float dy, float sigmaX, float sigmaY, UColor color) => new ProGpuEffectFilter(new DropShadowEffect(sigmaX, sigmaY) { Offset = new Vector2(dx, dy), Color = Color(color), DrawSource = false });

	public IEffectFilter? CreateEffectFilter(EffectNode tree, URect bounds)
	{
		ArgumentNullException.ThrowIfNull(tree);
		if (tree is BlendEffectNode { Foreground: SourceInput, Background: ColorInput or TextureInput } blend)
		{
			return new ProGpuEffectFilter(new ProGpuBlendEffect(blend.Background, blend.Mode, new PRect((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height)));
		}
		if (TryCompileBackdrop(tree, out var backdrop))
		{
			return new ProGpuEffectFilter(new ProGpuBackdropEffect(backdrop));
		}
		return tree switch
		{
			BlurEffectNode blur when blur.Source is SourceInput => new ProGpuEffectFilter(new BlurEffect(blur.Sigma)),
			_ => null,
		};
	}

	private static bool TryCompileBackdrop(EffectNode tree, out BackdropMaterialBrush material)
	{
		var sawBackdrop = false;
		var blur = 0f;
		var colors = new List<Vector4>();
		void Walk(EffectNode node)
		{
			switch (node)
			{
				case SourceInput:
					sawBackdrop = true;
					break;
				case BlurEffectNode blurNode:
					blur = MathF.Max(blur, blurNode.Sigma * BackdropBlurRadiusPerSigma);
					Walk(blurNode.Source);
					break;
				case ColorInput color:
					colors.Add(Color(color.Color));
					break;
				default:
					foreach (var child in node.Children) Walk(child);
					break;
			}
		}
		Walk(tree);
		material = new BackdropMaterialBrush
		{
			Kind = blur > 0 && colors.Count > 0 ? BackdropMaterialKind.Acrylic : blur > 0 ? BackdropMaterialKind.Blur : BackdropMaterialKind.Tint,
			Source = BackdropMaterialSource.HostBackdrop,
			BlurRadius = blur,
			TintColor = colors.Count > 0 ? colors[^1] : Vector4.Zero,
			LuminosityColor = colors.Count > 1 ? colors[^2] : Vector4.Zero,
			NoiseOpacity = colors.Count > 1 ? 0.02f : 0,
			Saturation = 1f,
		};
		return sawBackdrop && (blur > 0 || colors.Count > 0);
	}

	internal GpuTexture NewTexture(int width, int height) => new(_context, (uint)width, (uint)height, _format,
		TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.RenderAttachment,
		"Uno ProGPU texture", alphaMode: GpuTextureAlphaMode.Premultiplied);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_presentedTarget = null;
		_hostBackdropTarget?.Dispose();
		_hostBackdropTarget = null;
		_compositor.Dispose();
		_context.Dispose();
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
	private static TextureFormat ToSilkFormat(uint format)
	{
		var modern = (N.WGPUTextureFormat)format;
		if (Enum.TryParse<TextureFormat>(modern.ToString(), true, out var mapped) && mapped != TextureFormat.Undefined) return mapped;
		return TextureFormat.Rgba8Unorm;
	}
	private static GradientStop[] Stops(UColor[] colors, float[] positions)
	{
		ArgumentNullException.ThrowIfNull(colors);
		ArgumentNullException.ThrowIfNull(positions);
		if (colors.Length == 0 || colors.Length != positions.Length) throw new ArgumentException("Gradient colors and positions must have equal non-zero lengths.");
		var result = new GradientStop[colors.Length];
		for (var i = 0; i < result.Length; i++) result[i] = new GradientStop(Color(colors[i]), positions[i]);
		return result;
	}
	private static GradientSpreadMethod Spread(GradientTileMode mode) => mode switch { GradientTileMode.Repeat => GradientSpreadMethod.Repeat, GradientTileMode.Mirror => GradientSpreadMethod.Reflect, _ => GradientSpreadMethod.Pad };
	internal static Vector4 Color(UColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
	internal static Matrix4x4 ToMatrix(Matrix3x2 m) => new(m.M11, m.M12, 0, 0, m.M21, m.M22, 0, 0, 0, 0, 1, 0, m.M31, m.M32, 0, 1);
	private static ProGpuShader EmptyShader() => new(new SolidColorBrush(Vector4.Zero));
	private static bool TryGetBrushCoordinateTransform(Matrix3x2 localMatrix, out Matrix4x4 coordinateTransform)
	{
		if (!Matrix3x2.Invert(localMatrix, out var inverse) ||
			!float.IsFinite(inverse.M11) || !float.IsFinite(inverse.M12) ||
			!float.IsFinite(inverse.M21) || !float.IsFinite(inverse.M22) ||
			!float.IsFinite(inverse.M31) || !float.IsFinite(inverse.M32))
		{
			coordinateTransform = Matrix4x4.Identity;
			return false;
		}

		coordinateTransform = ToMatrix(inverse);
		return true;
	}

	private sealed class PictureVisual : Visual
	{
		private GpuPicture? _picture;
		private Matrix4x4 _transform = Matrix4x4.Identity;

		internal PictureVisual()
		{
		}

		internal PictureVisual(GpuPicture picture) => _picture = picture;

		internal bool Update(GpuPicture picture, Matrix4x4 transform)
		{
			if (_picture is { } current &&
				_transform.Equals(transform) &&
				(ReferenceEquals(current, picture) ||
					current.SharesRetainedCommandStorageWith(picture)))
			{
				// Keep the current ownership clone live for synchronous rendering,
				// but do not invalidate an unchanged retained command stream.
				_picture = picture;
				return false;
			}
			_picture = picture;
			_transform = transform;
			Invalidate();
			return true;
		}

		public override void OnRender(DrawingContext context)
		{
			if (_picture is { } picture)
			{
				if (_transform == Matrix4x4.Identity)
				{
					context.DrawPicture(picture);
				}
				else
				{
					context.DrawPictureTransformed(picture, _transform);
				}
			}
		}
	}
}

internal sealed class ProGpuShader(Brush brush) : DrawingResource, IShader
{
	internal Brush Brush { get; } = brush;
	protected override void Free() { }
}

internal sealed class ProGpuColorFilter(Vector4 color, float[]? matrix) : DrawingResource, IColorFilter
{
	internal Vector4 Color { get; } = color;
	internal float[]? Matrix { get; } = matrix;
	protected override void Free() { }
}

internal sealed class ProGpuEffectFilter : DrawingResource, IEffectFilter
{
	internal ProGpuEffectFilter(object value)
	{
		Value = value;
		if (value is ProGpuBlendEffect { Background: TextureInput texture })
		{
			texture.Texture.AddRef();
		}
	}

	internal object Value { get; }
	protected override void Free()
	{
		if (Value is ProGpuBlendEffect { Background: TextureInput texture })
		{
			texture.Texture.Release();
		}
	}
}

internal sealed record ProGpuBackdropEffect(BackdropMaterialBrush Material);
internal sealed record ProGpuBlendEffect(EffectNode Background, BlendMode Mode, PRect Bounds);

internal sealed class ProGpuTexture(GpuTexture texture) : DrawingResource, ITexture
{
	internal GpuTexture Texture { get; } = texture;
	public int PixelWidth => checked((int)Texture.Width);
	public int PixelHeight => checked((int)Texture.Height);
	protected override void Free() => Texture.Dispose();
}

internal sealed class ProGpuDrawingResourceLease : IDisposable
{
	private IDrawingResource? _resource;
	internal ProGpuDrawingResourceLease(IDrawingResource resource)
	{
		resource.AddRef();
		_resource = resource;
	}

	public void Dispose() => Interlocked.Exchange(ref _resource, null)?.Release();
}

internal sealed class ProGpuRenderRecord(GpuPicture picture, Vector4? leadingClearColor, bool hasHostBackdrop) : IRenderRecord
{
	private int _disposed;
	internal GpuPicture Picture { get; } = picture;
	internal Vector4? LeadingClearColor { get; } = leadingClearColor;
	internal bool HasHostBackdrop { get; } = hasHostBackdrop;
	public void Replay(IDrawingSession into)
	{
		if (_disposed != 0) throw new ObjectDisposedException(nameof(ProGpuRenderRecord));
		if (into is not ProGpuDrawingSession session) throw new ArgumentException("A ProGPU record can only be replayed into a ProGPU session.", nameof(into));
		if (LeadingClearColor is { } clearColor && !session.TryAdoptLeadingClear(clearColor))
		{
			session.RecordReplacementClear(clearColor);
		}
		if (HasHostBackdrop)
		{
			session.MarkHostBackdrop();
		}
		// Nested replay owns a cheap clone of the immutable command storage, so disposing
		// this record cannot leave the destination pointing at disposed picture leases.
		var retained = Picture.Clone();
		session.Context.RetainResource(retained);
		var transform = session.TotalMatrix;
		if (transform == Matrix4x4.Identity)
		{
			session.Context.DrawPicture(retained);
		}
		else
		{
			// Uno records retained frames in logical pixels, then applies the target's
			// rasterization scale (and any host orientation) to the present session.
			// Preserve that outer transform when nesting the retained picture.
			session.Context.DrawPictureTransformed(retained, transform);
		}
	}
	public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Picture.Dispose(); }
}

internal class ProGpuDrawingSession : IDrawingSession
{
	private readonly Stack<State> _states = new();
	private readonly Stack<ClipState> _clips = new();
	private Matrix4x4 _matrix = Matrix4x4.Identity;
	private PRect _clipBounds = new(-100_000, -100_000, 200_000, 200_000);
	private readonly GpuPictureRecorder _recorder = new();
	private RoundedClipCommand? _lastRoundedClip;
	private Vector4? _leadingClearColor;
	private bool _hasHostBackdrop;
	private PRect? _effectContentBounds;
	private int _effectLayerDepth;
	private bool _finished;
	private Dictionary<DrawingContext, HashSet<IDrawingResource>>? _retainedResources;

	protected ProGpuDrawingSession(ProGpuDrawingFactory factory)
	{
		Factory = factory;
		Context = _recorder.BeginRecording(new PRect(0, 0, 1, 1));
	}

	internal DrawingContext Context { get; private set; }
	internal Vector4? LeadingClearColor => _leadingClearColor;
	internal bool HasHostBackdrop => _hasHostBackdrop;
	internal void MarkHostBackdrop() => _hasHostBackdrop = true;
	public Matrix4x4 TotalMatrix => _matrix;
	public object NativeSurface => Context;
	public IDrawingFactory Factory { get; }
	public int SaveCount => _states.Count + 1;
	public void SetMatrix(in Matrix4x4 matrix) => _matrix = matrix;
	// IDrawingSession follows Skia's pre-concatenation contract. This order is
	// significant once a retained subtree combines its local translation with a
	// root DPI scale (or any other non-commuting transform).
	public void Concat(in Matrix4x4 matrix) => _matrix = matrix * _matrix;
	public void Translate(float dx, float dy) => _matrix = Matrix4x4.CreateTranslation(dx, dy, 0) * _matrix;
	public void Scale(float sx, float sy) => _matrix = Matrix4x4.CreateScale(sx, sy, 1) * _matrix;
	public int Save() => PushState(Scope.None);
	public void Restore()
	{
		if (_states.Count == 0) return;
		var state = _states.Pop();
		while (_clips.Count > state.ClipCount)
		{
			PopClip();
		}
		switch (state.Scope)
		{
			case Scope.Blend: Context.PopBlendMode(); break;
			case Scope.EffectLayer:
				var layer = state.Layer ?? throw new InvalidOperationException("Effect layer state was incomplete.");
				var picture = layer.Recorder.EndRecording();
				var contentBounds = CompositeEffectBounds(_effectContentBounds, layer.Effect, layer.ParentContentBounds);
				if (layer.Blend is { } blend)
				{
					picture = ComposeBlend(picture, blend, state.Matrix);
					contentBounds = TransformBounds(blend.Bounds, state.Matrix);
				}
				_effectContentBounds = layer.ParentContentBounds;
				_effectLayerDepth--;
				Context = layer.Parent;
				if (contentBounds is { IsEmpty: false } bounds)
				{
					Context.RetainResource(picture);
					Context.DrawVisual(new EffectPictureVisual(picture, layer.Effect, bounds));
					IncludeEffectBounds(bounds, layer.Effect, alreadyTransformed: true);
				}
				else
				{
					picture.Dispose();
				}
				break;
		}
		_matrix = state.Matrix;
		_clipBounds = state.ClipBounds;
	}
	public void RestoreToCount(int count) { if (count < 1 || count > SaveCount) throw new ArgumentOutOfRangeException(nameof(count)); while (SaveCount > count) Restore(); }
	public void SaveLayer() =>
		BeginEffectLayer(new BlendModeEffect(GpuBlendMode.SrcOver));
	public void SaveLayer(IColorFilter colorFilter)
	{
		if (colorFilter is ProGpuColorFilter native)
		{
			BeginEffectLayer(new ColorMatrixEffect(native.Matrix is { Length: >= 20 } matrix ? Matrix(matrix) : TintMatrix(native.Color)));
			RetainResource(colorFilter);
			return;
		}
		Unsupported(nameof(SaveLayer));
		Save();
	}
	public void SaveLayerMask() => BeginEffectLayer(new BlendModeEffect(GpuBlendMode.DstIn));
	public void SaveLayer(IEffectFilter filter)
	{
		if (filter is ProGpuEffectFilter { Value: ProGpuBlendEffect blend })
		{
			BeginEffectLayer(new BlendModeEffect(GpuBlendMode.SrcOver), blend);
			RetainResource(filter);
			return;
		}
		if (filter is not ProGpuEffectFilter { Value: EffectBase effect })
		{
			Unsupported(nameof(SaveLayer));
			Save();
			return;
		}

		BeginEffectLayer(effect);
		RetainResource(filter);
	}

	private void BeginEffectLayer(EffectBase effect, ProGpuBlendEffect? blend = null)
	{
		var parent = Context;
		var recorder = new GpuPictureRecorder();
		PushState(Scope.EffectLayer, new EffectLayer(parent, recorder, effect, _effectContentBounds, blend));
		_effectContentBounds = null;
		_effectLayerDepth++;
		Context = recorder.BeginRecording(new PRect(0, 0, 1, 1));
	}

	private GpuPicture ComposeBlend(GpuPicture source, ProGpuBlendEffect blend, Matrix4x4 transform)
	{
		var recorder = new GpuPictureRecorder();
		var context = recorder.BeginRecording(new PRect(0, 0, 1, 1));
		var bounds = TransformBounds(blend.Bounds, transform);
		context.PushClip(blend.Bounds, transform);
		if (blend.Background is ColorInput color)
		{
			context.DrawRectangle(Solid(color.Color), null, blend.Bounds, transform);
		}
		else if (blend.Background is TextureInput texture)
		{
			var parent = Context;
			var parentMatrix = _matrix;
			Context = context;
			_matrix = transform;
			try
			{
				DrawImageTiled(texture.Texture, new URect(blend.Bounds.X, blend.Bounds.Y, blend.Bounds.Width, blend.Bounds.Height), texture.ExtendX, texture.ExtendY);
			}
			finally
			{
				Context = parent;
				_matrix = parentMatrix;
			}
		}
		context.RetainResource(source);
		context.DrawVisual(new EffectPictureVisual(source, new BlendModeEffect(ToBlend(blend.Mode)), bounds));
		context.PopClip();
		return recorder.EndRecording();
	}

	public void ClipRect(in URect rect, ClipOperation operation = ClipOperation.Intersect)
	{
		if (operation == ClipOperation.Difference)
		{
			if (TryCoalesceRoundedRectangularDifference(rect))
			{
				return;
			}
			using var difference = DifferenceRect(rect);
			ClipPath(difference, operation);
			return;
		}
		_clipBounds = Intersect(_clipBounds, Rect(rect));
		Context.PushClip(Rect(rect), _matrix);
		_clips.Push(new ClipState(ClipScope.Rect));
	}
	public void ClipRoundRect(in RoundRectangle roundRect, ClipOperation operation = ClipOperation.Intersect)
	{
		if (operation == ClipOperation.Difference && TryCoalesceRoundedDifference(roundRect))
		{
			return;
		}
		using var geometry = RoundedGeometry(roundRect);
		ClipPath(geometry, operation);
		if (operation == ClipOperation.Intersect)
		{
			_lastRoundedClip = new RoundedClipCommand(
				Context,
				Context.Commands.Count - 1,
				roundRect,
				_matrix,
				_clips.Count);
		}
	}
	public void ClipPath(IGeometry geometry, ClipOperation operation = ClipOperation.Intersect)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		var path = ProGpuGeometryFactory.Import(geometry).Path;
		if (operation == ClipOperation.Difference)
		{
			var outer = ProGPU.Vector.PrimitivePathGeometry.CreateRectangle(
				_clipBounds.X,
				_clipBounds.Y,
				_clipBounds.Width,
				_clipBounds.Height);
			if (!path.IsCombined && path.Figures.Count == 1)
			{
				// Uno builds a gradient rounded border by intersecting the outer
				// contour and then excluding one contained inner contour. Encoding
				// that subtraction as two even-odd contours is exact for this case
				// and keeps ProGPU on its regular vector-mask path instead of the
				// more expensive deferred boolean-mask path.
				var excluded = path.CreateTransformed(Matrix4x4.Identity);
				outer.FillRule = FillRule.EvenOdd;
				foreach (var figure in excluded.Figures)
				{
					outer.Figures.Add(figure);
				}
				path = outer;
			}
			else
			{
				path = new PathGeometry { IsCombined = true, PathA = outer, PathB = path, Op = 0, FillRule = FillRule.Nonzero };
			}
		}
		if (operation == ClipOperation.Intersect) _clipBounds = Intersect(_clipBounds, Rect(geometry.Bounds));
		Context.PushGeometryClip(path, _matrix);
		_clips.Push(new ClipState(ClipScope.Geometry));
	}

	private bool TryCoalesceRoundedDifference(in RoundRectangle inner)
	{
		if (_lastRoundedClip is not { } previous ||
			!ReferenceEquals(previous.Context, Context) ||
			previous.CommandIndex != Context.Commands.Count - 1 ||
			previous.ClipCount != _clips.Count ||
			previous.Transform != _matrix ||
			!Contains(previous.RoundRect.Rect, inner.Rect))
		{
			return false;
		}
		if (previous.RoundRect == inner)
		{
			// The outer clip followed by an identical Difference is empty. Keep
			// the outer scope in place and represent only the nested empty scope
			// as a zero-area rectangular clip, avoiding a pointless mask texture.
			Context.PushClip(new PRect(0, 0, 0, 0), _matrix);
			_clips.Push(new ClipState(ClipScope.Rect));
			_lastRoundedClip = null;
			return true;
		}

		Context.Commands.RemoveAt(previous.CommandIndex);
		using var outerGeometry = RoundedGeometry(previous.RoundRect);
		using var innerGeometry = RoundedGeometry(inner);
		var outerPath = ProGpuGeometryFactory.Import(outerGeometry).Path;
		var innerPath = ProGpuGeometryFactory.Import(innerGeometry).Path;
		var ring = outerPath.CreateTransformed(Matrix4x4.Identity);
		ring.FillRule = FillRule.EvenOdd;
		var excluded = innerPath.CreateTransformed(Matrix4x4.Identity);
		foreach (var figure in excluded.Figures)
		{
			ring.Figures.Add(figure);
		}

		Context.PushGeometryClip(ring, _matrix);
		_clips.Push(new ClipState(ClipScope.CoalescedRoundedDifference, outerPath, previous.Transform));
		_lastRoundedClip = null;
		return true;
	}

	private bool TryCoalesceRoundedRectangularDifference(in URect inner)
	{
		if (_lastRoundedClip is not { } previous ||
			!ReferenceEquals(previous.Context, Context) ||
			previous.CommandIndex != Context.Commands.Count - 1 ||
			previous.ClipCount != _clips.Count ||
			previous.Transform != _matrix ||
			!Contains(previous.RoundRect.Rect, inner))
		{
			return false;
		}
		if (previous.RoundRect.Rect == inner)
		{
			Context.PushClip(new PRect(0, 0, 0, 0), _matrix);
			_clips.Push(new ClipState(ClipScope.Rect));
			_lastRoundedClip = null;
			return true;
		}

		Context.Commands.RemoveAt(previous.CommandIndex);
		using var outerGeometry = RoundedGeometry(previous.RoundRect);
		using var innerGeometry = DifferenceRect(inner);
		var outerPath = ProGpuGeometryFactory.Import(outerGeometry).Path;
		var innerPath = ProGpuGeometryFactory.Import(innerGeometry).Path;
		var ring = outerPath.CreateTransformed(Matrix4x4.Identity);
		ring.FillRule = FillRule.EvenOdd;
		var excluded = innerPath.CreateTransformed(Matrix4x4.Identity);
		foreach (var figure in excluded.Figures)
		{
			ring.Figures.Add(figure);
		}

		Context.PushGeometryClip(ring, _matrix);
		_clips.Push(new ClipState(ClipScope.CoalescedRoundedDifference, outerPath, previous.Transform));
		_lastRoundedClip = null;
		return true;
	}

	public void Clear(UColor color)
	{
		var clearColor = ProGpuDrawingFactory.Color(color);
		if (TryAdoptLeadingClear(clearColor))
		{
			return;
		}
		RecordReplacementClear(clearColor);
	}

	internal bool TryAdoptLeadingClear(Vector4 color)
	{
		if (Context.Commands.Count != 0 || _states.Count != 0 || _clips.Count != 0)
		{
			return false;
		}

		_leadingClearColor = color;
		return true;
	}

	internal void RecordReplacementClear(Vector4 color)
	{
		// Clear is replacement, including when the requested alpha is zero. SrcOver
		// would leave destination pixels behind for transparent and translucent colors.
		if (_effectLayerDepth > 0 && _clips.Count > 0)
		{
			IncludeEffectBounds(_clipBounds);
		}
		Context.PushBlendMode(GpuBlendMode.Src);
		Context.DrawRectangle(new SolidColorBrush(color), null, new PRect(-1_000_000, -1_000_000, 2_000_000, 2_000_000), Matrix4x4.Identity);
		Context.PopBlendMode();
	}
	public void DrawRect(in URect rect, UColor color)
	{
		IncludeEffectBounds(Rect(rect));
		Context.DrawRectangle(Solid(color), null, Rect(rect), _matrix);
	}
	public void DrawRect(in URect rect, IShader shader)
	{
		IncludeEffectBounds(Rect(rect));
		Context.DrawRectangle(Shader(shader), null, Rect(rect), _matrix);
	}
	public void DrawRoundedRect(in URect rect, Vector4 radii, UColor color)
	{
		IncludeEffectBounds(Rect(rect));
		if (radii.X == radii.Y && radii.X == radii.Z && radii.X == radii.W) Context.DrawRoundedRectangle(Solid(color), null, Rect(rect), radii.X, radii.X, _matrix);
		else
		{
			using var geometry = RoundedGeometry(rect, radii);
			Context.DrawPath(Solid(color), null, ProGpuGeometryFactory.Import(geometry).Path, _matrix);
		}
	}
	public void DrawRoundedRectBorder(in URect outer, Vector4 outerRadii, in URect inner, Vector4 innerRadii, UColor color)
	{
		var left = (float)(inner.X - outer.X);
		var top = (float)(inner.Y - outer.Y);
		var right = (float)(outer.Right - inner.Right);
		var bottom = (float)(outer.Bottom - inner.Bottom);
		if (left > 0f && NearlyEqual(left, top) && NearlyEqual(left, right) && NearlyEqual(left, bottom) &&
			AllEqual(outerRadii) && AllEqual(innerRadii))
		{
			IncludeEffectBounds(Rect(outer));
			var half = left * 0.5f;
			var strokeBounds = new PRect(
				(float)outer.X + half,
				(float)outer.Y + half,
				(float)outer.Width - left,
				(float)outer.Height - left);
			var radius = MathF.Max(0f, outerRadii.X - half);
			Context.DrawRoundedRectangle(null, new Pen(Solid(color), left), strokeBounds, radius, radius, _matrix);
			return;
		}

		using var outerGeometry = RoundedGeometry(outer, outerRadii);
		using var innerGeometry = RoundedGeometry(inner, innerRadii);
		using var border = outerGeometry.Combine(innerGeometry, GeometryCombineMode.Difference);
		var restoreCount = Save();
		ClipPath(border);
		DrawRoundedRect(outer, outerRadii, color);
		RestoreToCount(restoreCount);
	}
	public void DrawPath(IGeometry geometry, UColor color)
	{
		if (geometry is ProGpuStrokeGeometry stroke)
		{
			IncludeEffectBounds(Inflate(Rect(stroke.Source.Bounds), stroke.EffectOutset));
			Context.DrawPath(null, stroke.CreatePen(Solid(color)), stroke.Source.Path, _matrix);
			return;
		}

		IncludeEffectBounds(Rect(geometry.Bounds));
		if (geometry is ProGpuGlyphRunGeometry glyphRun)
		{
			Context.DrawGlyphRun(
				glyphRun.Glyphs,
				glyphRun.Positions,
				glyphRun.Font.Font,
				glyphRun.Font.Size,
				Solid(color),
				Vector2.Zero,
				_matrix,
				preferGlyphAtlas: ((ProGpuDrawingFactory)Factory).Options.PreferGlyphAtlas);
			return;
		}
		var native = ProGpuGeometryFactory.Import(geometry);
		if (native.Path.IsCombined)
		{
			// ProGPU's geometry-clip compiler supports the deferred boolean tree, while
			// filling that same tree directly currently yields no coverage for contained
			// rings (the shape Uno uses for BorderVisual). Filling the clipped bounds is
			// equivalent for a solid brush and keeps the boolean operation on the GPU.
			var restoreCount = Save();
			ClipPath(native);
			DrawRect(native.Bounds, color);
			RestoreToCount(restoreCount);
			return;
		}
		Context.DrawPath(Solid(color), null, native.Path, _matrix);
	}

	public void DrawPath(IGeometry geometry, UColor color, Vector2 offset)
	{
		var previous = _matrix;
		_matrix = Matrix4x4.CreateTranslation(offset.X, offset.Y, 0) * previous;
		try
		{
			DrawPath(geometry, color);
		}
		finally
		{
			_matrix = previous;
		}
	}

	public void DrawPaths(ReadOnlySpan<PathInstance> instances, UColor color)
	{
		foreach (var instance in instances)
		{
			DrawPath(instance.Geometry, color, instance.Offset);
		}
	}
	public void DrawShadow(IGeometry silhouette, UColor color, float sigmaX, float sigmaY, bool additive)
	{
		// A retained shadow visual is the native ProGPU effect path; the geometry is recorded once into it.
		var recorder = new GpuPictureRecorder();
		var child = recorder.BeginRecording(new PRect(0, 0, 1, 1));
		// The child is an opacity mask. Applying the requested shadow color here and
		// again in DropShadowEffect would square translucent color and alpha values.
		child.DrawPath(new SolidColorBrush(Vector4.One), null, ProGpuGeometryFactory.Import(silhouette).Path, _matrix);
		var picture = recorder.EndRecording();
		Context.RetainResource(picture);
		var effect = new DropShadowEffect(sigmaX, sigmaY) { Color = ProGpuDrawingFactory.Color(color), DrawSource = false };
		var bounds = TransformBounds(Rect(silhouette.Bounds), _matrix);
		if (additive)
		{
			Context.PushBlendMode(GpuBlendMode.Plus);
		}
		Context.DrawVisual(new EffectPictureVisual(picture, effect, bounds));
		if (additive)
		{
			Context.PopBlendMode();
		}
		IncludeEffectBounds(bounds, effect, alreadyTransformed: true);
	}
	public void StrokePath(IGeometry geometry, UColor color, float strokeWidth, StrokeJoin join = StrokeJoin.Miter)
	{
		IncludeEffectBounds(Inflate(Rect(geometry.Bounds), strokeWidth * 0.5f * (join is StrokeJoin.Miter or StrokeJoin.MiterOrBevel ? 10f : 1f)));
		Context.DrawPath(null, new Pen(Solid(color), strokeWidth, ProGpuGeometryAlgorithms.ToProGpu(join)), ProGpuGeometryFactory.Import(geometry).Path, _matrix);
	}
	public void DrawLine(Vector2 p0, Vector2 p1, UColor color, float strokeWidth)
	{
		var half = strokeWidth * 0.5f;
		IncludeEffectBounds(new PRect(
			MathF.Min(p0.X, p1.X) - half,
			MathF.Min(p0.Y, p1.Y) - half,
			MathF.Abs(p1.X - p0.X) + strokeWidth,
			MathF.Abs(p1.Y - p0.Y) + strokeWidth));
		Context.DrawLine(new Pen(Solid(color), strokeWidth), p0, p1, _matrix);
	}
	public void DrawImage(ITexture texture, float x, float y, float opacity = 1) => DrawTexture(texture, new URect(x, y, texture.PixelWidth, texture.PixelHeight), opacity);
	public void DrawImage(ITexture texture, float x, float y, IColorFilter colorFilter)
	{
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, ((ProGpuDrawingFactory)Factory).Context))
		{
			throw new ArgumentException("Texture belongs to another drawing factory.", nameof(texture));
		}
		if (colorFilter is not ProGpuColorFilter filter) throw new ArgumentException("Color filter belongs to another backend.", nameof(colorFilter));
		var matrix = filter.Matrix is { Length: >= 20 } values ? Matrix(values) : TintMatrix(filter.Color);
		RetainResource(texture);
		RetainResource(colorFilter);
		Context.DrawImageWithEffect(native.Texture, new PRect(x, y, texture.PixelWidth, texture.PixelHeight),
			sourceRect: new PRect(0, 0, texture.PixelWidth, texture.PixelHeight),
			samplingMode: TextureSamplingMode.Linear,
			colorMatrix: matrix, transform: _matrix);
		IncludeEffectBounds(new PRect(x, y, texture.PixelWidth, texture.PixelHeight));
	}
	public void DrawImageNineSlice(ITexture texture, in URect centerSlice, in URect destination, bool centerHollow)
	{
		var x = new[] { 0f, (float)centerSlice.X, (float)centerSlice.Right, texture.PixelWidth };
		var y = new[] { 0f, (float)centerSlice.Y, (float)centerSlice.Bottom, texture.PixelHeight };
		var dx = new[] { (float)destination.X, (float)destination.X + x[1], (float)destination.Right - (texture.PixelWidth - x[2]), (float)destination.Right };
		var dy = new[] { (float)destination.Y, (float)destination.Y + y[1], (float)destination.Bottom - (texture.PixelHeight - y[2]), (float)destination.Bottom };
		for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
		{
			if (centerHollow && row == 1 && column == 1) continue;
			DrawTexturePart(texture, new PRect(x[column], y[row], x[column + 1] - x[column], y[row + 1] - y[row]), new PRect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]), 1);
		}
	}

	public void DrawImageTiled(ITexture texture, in URect destination, EdgeExtend extendX, EdgeExtend extendY, float opacity = 1f)
	{
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, ((ProGpuDrawingFactory)Factory).Context))
		{
			throw new ArgumentException("Texture belongs to another drawing factory.", nameof(texture));
		}
		if (!Enum.IsDefined(extendX) || !Enum.IsDefined(extendY))
		{
			throw new ArgumentOutOfRangeException(nameof(extendX), "Texture extension modes must be defined.");
		}
		var bounds = Rect(destination);
		if (bounds.IsEmpty || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
		{
			return;
		}
		RetainResource(texture);
		IncludeEffectBounds(bounds);
		if (opacity < 1f)
		{
			Context.PushOpacity(Math.Clamp(opacity, 0f, 1f));
		}
		Context.DrawExtension(
			CompositorBuiltInExtensions.WpfShaderEffect,
			dataParam: new WpfShaderEffectParams
			{
				Texture = native.Texture,
				Rect = bounds,
				ShaderKey = "uno-progpu-tiled-image-v1",
				ShaderSource = ProGpuTiledImageShader.Source,
				Constants = [bounds.Width, bounds.Height, (float)extendX, (float)extendY],
				SamplingMode = TextureSamplingMode.Linear,
			},
			transform: _matrix);
		if (opacity < 1f)
		{
			Context.PopOpacity();
		}
	}
	public void DrawEffectBackdrop(IEffectFilter filter, float opacity)
	{
		if (filter is ProGpuEffectFilter { Value: ProGpuBlendEffect })
		{
			// An isolated layer cannot supply the live parent backdrop to its source.
			// Keep this distinct from supported source-layer blending until capture
			// and coverage can be realized through the renderer's extension seam.
			Unsupported("DrawEffectBackdrop.BlendGraph");
			return;
		}
		if (filter is ProGpuEffectFilter { Value: ProGpuBackdropEffect backdrop })
		{
			var material = backdrop.Material;
			if (material.Source == BackdropMaterialSource.HostBackdrop && !material.UseFallback)
			{
				_hasHostBackdrop = true;
			}
			var originalOpacity = material.MaterialOpacity;
			material.MaterialOpacity = originalOpacity * Math.Clamp(opacity, 0, 1);
			Context.DrawBackdropMaterial(material, _clipBounds, transform: _matrix);
			material.MaterialOpacity = originalOpacity;
			return;
		}
		Unsupported(nameof(DrawEffectBackdrop));
	}

	protected GpuPicture FinishPicture()
	{
		if (_finished) throw new InvalidOperationException("The drawing session has already finished.");
		while (_states.Count > 0) Restore();
		while (_clips.Count > 0) PopClip();
		_finished = true;
		return _recorder.EndRecording();
	}

	private int PushState(Scope scope, EffectLayer? layer = null)
	{
		var restoreCount = SaveCount;
		_states.Push(new State(_matrix, scope, _clipBounds, _clips.Count, layer));
		return restoreCount;
	}

	private void PopClip()
	{
		_lastRoundedClip = null;
		var clip = _clips.Pop();
		switch (clip.Scope)
		{
			case ClipScope.Rect: Context.PopClip(); break;
			case ClipScope.Geometry: Context.PopGeometryClip(); break;
			case ClipScope.CoalescedRoundedDifference:
				Context.PopGeometryClip();
				Context.PushGeometryClip(
					clip.RestorePath ?? throw new InvalidOperationException("A coalesced rounded clip had no outer geometry."),
					clip.RestoreTransform);
				break;
		}
	}

	private void DrawTexture(ITexture texture, URect destination, float opacity) => DrawTexturePart(texture, new PRect(0, 0, texture.PixelWidth, texture.PixelHeight), Rect(destination), opacity);
	private void DrawTexturePart(ITexture texture, PRect source, PRect destination, float opacity)
	{
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, ((ProGpuDrawingFactory)Factory).Context)) throw new ArgumentException("Texture belongs to another drawing factory.", nameof(texture));
		RetainResource(texture);
		IncludeEffectBounds(destination);
		if (opacity < 1) Context.PushOpacity(Math.Clamp(opacity, 0, 1));
		Context.DrawTexture(native.Texture, destination, source, _matrix, TextureSamplingMode.Linear);
		if (opacity < 1) Context.PopOpacity();
	}
	private void RetainResource(IDrawingResource resource)
	{
		var contexts = _retainedResources ??= new();
		if (!contexts.TryGetValue(Context, out var resources))
		{
			contexts.Add(Context, resources = new(ReferenceEqualityComparer.Instance));
		}
		if (resources.Add(resource))
		{
			Context.RetainResource(new ProGpuDrawingResourceLease(resource));
		}
	}
	private void Unsupported(string name) => ProGpuDiagnostics.Unsupported(name, ((ProGpuDrawingFactory)Factory).Options.FailOnUnsupportedOperation);
	private static Brush Shader(IShader shader) => shader is ProGpuShader native ? native.Brush : throw new ArgumentException("Shader belongs to another backend.", nameof(shader));
	private static SolidColorBrush Solid(UColor color) => new(ProGpuDrawingFactory.Color(color));
	private static PRect Rect(URect value) => new((float)value.X, (float)value.Y, (float)value.Width, (float)value.Height);
	private static PRect Intersect(PRect left, PRect right)
	{
		var x = MathF.Max(left.X, right.X);
		var y = MathF.Max(left.Y, right.Y);
		var rightEdge = MathF.Min(left.Right, right.Right);
		var bottom = MathF.Min(left.Bottom, right.Bottom);
		return new PRect(x, y, MathF.Max(0, rightEdge - x), MathF.Max(0, bottom - y));
	}
	private void IncludeEffectBounds(PRect bounds, EffectBase? effect = null, bool alreadyTransformed = false)
	{
		if (_effectLayerDepth == 0 || bounds.IsEmpty)
		{
			return;
		}

		var transformed = alreadyTransformed ? bounds : TransformBounds(bounds, _matrix);
		if (effect is not null)
		{
			transformed = EffectOutputBounds(transformed, effect);
		}
		_effectContentBounds = _effectContentBounds is { } current
			? Union(current, transformed)
			: transformed;
	}
	private static PRect EffectOutputBounds(PRect content, EffectBase effect)
	{
		var (paddingX, paddingY) = effect switch
		{
			BlurEffect blur => (
				MathF.Ceiling(MathF.Max(0, blur.BlurRadius) * 2f),
				MathF.Ceiling(MathF.Max(0, blur.BlurRadius) * 2f)),
			DropShadowEffect shadow => (
				MathF.Ceiling(MathF.Max(0, shadow.BlurRadiusX) * 2f),
				MathF.Ceiling(MathF.Max(0, shadow.BlurRadiusY) * 2f)),
			_ => (0f, 0f),
		};
		var output = effect is DropShadowEffect { DrawSource: false }
			? PRect.Empty
			: Inflate(content, paddingX, paddingY);
		if (effect is DropShadowEffect dropShadow)
		{
			var shadowBounds = new PRect(
				content.X + dropShadow.Offset.X - paddingX,
				content.Y + dropShadow.Offset.Y - paddingY,
				content.Width + paddingX * 2f,
				content.Height + paddingY * 2f);
			output = output.IsEmpty ? shadowBounds : Union(output, shadowBounds);
		}
		return output;
	}

	private static PRect? CompositeEffectBounds(PRect? source, EffectBase effect, PRect? destination)
	{
		if (effect is not BlendModeEffect blend ||
			!BlendModeNeedsDestinationCoverage(blend.BlendMode) ||
			destination is not { IsEmpty: false } destinationBounds)
		{
			return source;
		}

		return source is { IsEmpty: false } sourceBounds
			? Union(sourceBounds, destinationBounds)
			: destinationBounds;
	}

	private static bool BlendModeNeedsDestinationCoverage(GpuBlendMode mode) => mode is
		GpuBlendMode.Src or
		GpuBlendMode.Modulate or
		GpuBlendMode.DstIn or
		GpuBlendMode.SrcIn or
		GpuBlendMode.SrcOut or
		GpuBlendMode.DstAtop;

	private static PRect TransformBounds(PRect bounds, Matrix4x4 transform)
	{
		var p0 = TransformPoint(bounds.X, bounds.Y, transform);
		var p1 = TransformPoint(bounds.Right, bounds.Y, transform);
		var p2 = TransformPoint(bounds.Right, bounds.Bottom, transform);
		var p3 = TransformPoint(bounds.X, bounds.Bottom, transform);
		var minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
		var minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
		var maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
		var maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
		return new PRect(minX, minY, MathF.Max(0, maxX - minX), MathF.Max(0, maxY - minY));
	}
	private static Vector2 TransformPoint(float x, float y, Matrix4x4 transform)
	{
		var value = Vector4.Transform(new Vector4(x, y, 0, 1), transform);
		if (MathF.Abs(value.W) > 0.000001f && value.W != 1f)
		{
			value /= value.W;
		}
		return new Vector2(value.X, value.Y);
	}
	private static PRect Inflate(PRect bounds, float amount) => new(
		bounds.X - amount,
		bounds.Y - amount,
		bounds.Width + amount * 2f,
		bounds.Height + amount * 2f);
	private static PRect Inflate(PRect bounds, float amountX, float amountY) => new(
		bounds.X - amountX,
		bounds.Y - amountY,
		bounds.Width + amountX * 2f,
		bounds.Height + amountY * 2f);
	private static PRect Union(PRect left, PRect right)
	{
		var x = MathF.Min(left.X, right.X);
		var y = MathF.Min(left.Y, right.Y);
		var rightEdge = MathF.Max(left.Right, right.Right);
		var bottom = MathF.Max(left.Bottom, right.Bottom);
		return new PRect(x, y, rightEdge - x, bottom - y);
	}
	private static bool Contains(URect outer, URect inner) =>
		inner.Left >= outer.Left && inner.Top >= outer.Top &&
		inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
	private static bool AllEqual(Vector4 value) =>
		NearlyEqual(value.X, value.Y) && NearlyEqual(value.X, value.Z) && NearlyEqual(value.X, value.W);
	private static bool NearlyEqual(float left, float right) => MathF.Abs(left - right) <= 0.001f;
	private static ImageEffectColorMatrix Matrix(float[] value) => new(
		new Vector4(value[0], value[1], value[2], value[3]),
		new Vector4(value[5], value[6], value[7], value[8]),
		new Vector4(value[10], value[11], value[12], value[13]),
		new Vector4(value[15], value[16], value[17], value[18]),
		new Vector4(value[4], value[9], value[14], value[19]));
	private static ImageEffectColorMatrix TintMatrix(Vector4 color) => new(
		Vector4.Zero,
		Vector4.Zero,
		Vector4.Zero,
		new Vector4(0, 0, 0, color.W),
		new Vector4(color.X, color.Y, color.Z, 0));
	private static IGeometry RoundedGeometry(RoundRectangle value) => RoundedGeometry(value.Rect, new Vector4(value.TopLeft.X, value.TopRight.X, value.BottomRight.X, value.BottomLeft.X));
	private static IGeometry RoundedGeometry(URect rect, Vector4 radii)
	{
		var builder = new ProGpuPrimitiveGeometryBuilder();
		builder.AddRoundedRectangle(rect, new Vector2(radii.X), new Vector2(radii.Y), new Vector2(radii.Z), new Vector2(radii.W));
		return builder.Build();
	}
	private static IGeometry DifferenceRect(URect rect) { var builder = new ProGpuPrimitiveGeometryBuilder(); builder.AddRectangle(rect); return builder.Build(); }
	private static GpuBlendMode ToBlend(BlendMode value) => value switch
	{
		BlendMode.SrcATop => GpuBlendMode.SrcAtop,
		BlendMode.DstATop => GpuBlendMode.DstAtop,
		_ when Enum.TryParse<GpuBlendMode>(value.ToString(), true, out var mapped) => mapped,
		_ => GpuBlendMode.SrcOver,
	};
	private readonly record struct State(Matrix4x4 Matrix, Scope Scope, PRect ClipBounds, int ClipCount, EffectLayer? Layer = null);
	private readonly record struct ClipState(ClipScope Scope, PathGeometry? RestorePath = null, Matrix4x4 RestoreTransform = default);
	private readonly record struct RoundedClipCommand(DrawingContext Context, int CommandIndex, RoundRectangle RoundRect, Matrix4x4 Transform, int ClipCount);
	private sealed record EffectLayer(DrawingContext Parent, GpuPictureRecorder Recorder, EffectBase Effect, PRect? ParentContentBounds, ProGpuBlendEffect? Blend);
	private enum Scope { None, Blend, EffectLayer }
	private enum ClipScope { Rect, Geometry, CoalescedRoundedDifference }
	private sealed class EffectPictureVisual : Visual
	{
		private readonly GpuPicture _picture;
		internal EffectPictureVisual(GpuPicture picture, EffectBase effect, PRect contentBounds)
		{
			_picture = picture;
			Effect = effect;
			EffectContentBounds = contentBounds;
		}
		public override void OnRender(DrawingContext context) => context.DrawPicture(_picture);
	}
}

internal sealed class ProGpuCommandRecorder : ProGpuDrawingSession, ICommandRecorder, IDisposable
{
	private bool _finished;
	internal ProGpuCommandRecorder(ProGpuDrawingFactory factory) : base(factory) { }
	public IRenderRecord Finish()
	{
		if (_finished) throw new InvalidOperationException("Recording already finished.");
		_finished = true;
		return new ProGpuRenderRecord(FinishPicture(), LeadingClearColor, HasHostBackdrop);
	}
	public void Dispose() { if (!_finished) ((ProGpuRenderRecord)Finish()).Dispose(); }
}

internal sealed class ProGpuPresentSession : ProGpuDrawingSession, IPresentSession
{
	private readonly ProGpuDrawingFactory _factory;
	private readonly IWebGpuRenderTarget _target;
	private readonly long _frame;
	private readonly Stopwatch _record = Stopwatch.StartNew();
	private int _disposed;
	internal ProGpuPresentSession(ProGpuDrawingFactory factory, IWebGpuRenderTarget target, long frame) : base(factory) { _factory = factory; _target = target; _frame = frame; }
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_record.Stop();
		using var picture = FinishPicture();
		_factory.Present(_target, picture, LeadingClearColor, HasHostBackdrop, _frame, _record.Elapsed.TotalMilliseconds);
	}
}
