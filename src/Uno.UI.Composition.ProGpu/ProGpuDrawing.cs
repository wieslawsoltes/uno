extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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
	private readonly WgpuContext _context = new();
	private readonly Compositor _compositor;
	private readonly PictureVisual _presentVisual = new();
	private readonly ProGpuBackendOptions _options;
	private readonly TextureFormat _format;
	private readonly Vector4 _defaultClearColor;
	private GpuTexture? _hostBackdropTarget;
	private long _frameNumber;
	private bool _disposed;

	internal ProGpuDrawingFactory(IWebGpuDeviceContext device, ProGpuBackendOptions options)
	{
		ArgumentNullException.ThrowIfNull(device);
		_options = options;
		_format = ToSilkFormat(device.ColorFormat);
		_context.MaximumDeferredQueueSubmissions =
			options.MaximumDeferredQueueSubmissions;
		_context.InitializeExternalNativeDevice(
			new UnoModernWebGpuApi(),
			new UnoBorrowedWebGpuLifetime(device.Device),
			(Device*)device.Device,
			(Queue*)device.Queue,
			_format);
		var compositorOptions = options.Compositor with
		{
			PrimarySampleCount = device.SampleCount is 4 ? 4u : 1u,
		};
		_compositor = new Compositor(_context, _format, compositorOptions);
		_defaultClearColor = _compositor.ClearColor;
		_ = ProGpuDiagnostics.NextDeviceGeneration();
	}

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
		_presentVisual.Update(content, transform);
		var pixelWidth = Math.Max(1, target.Width);
		var pixelHeight = Math.Max(1, target.Height);
		if (hasHostBackdrop)
		{
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
		else
		{
			_compositor.RenderScene(
				_presentVisual,
				(uint)pixelWidth,
				(uint)pixelHeight,
				(TextureView*)target.ColorView);
		}
		if (sceneDumpPath is not null)
		{
			ProGpuDiagnostics.AppendCompositorMetrics(sceneDumpPath, _compositor.Metrics);
		}
		submit.Stop();
		var metrics = _compositor.Metrics;
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
			SceneCacheMissReason = metrics.SceneCacheMissReason,
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
		return Task.Run<IImage>(() =>
		{
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

	public IShader CreateLinearGradientShader(Vector2 start, Vector2 end, UColor[] colors, float[] colorPositions, GradientTileMode tileMode, Matrix3x2 localMatrix) =>
		new ProGpuShader(new LinearGradientBrush(start, end, Stops(colors, colorPositions))
		{
			SpreadMethod = Spread(tileMode),
			CoordinateTransform = ToMatrix(localMatrix),
		});

	public IShader CreateRadialGradientShader(Vector2 center, Vector2 gradientOrigin, float radiusX, float radiusY, UColor[] colors, float[] colorPositions, GradientTileMode tileMode, Matrix3x2 localMatrix) =>
		new ProGpuShader(new RadialGradientBrush(center, gradientOrigin, radiusX, radiusY, Stops(colors, colorPositions))
		{
			SpreadMethod = Spread(tileMode),
			CoordinateTransform = ToMatrix(localMatrix),
		});

	public IColorFilter CreateBlendModeColorFilter(UColor color, BlendMode mode) => new ProGpuColorFilter(Color(color), mode, null);
	public IColorFilter CreateColorMatrixColorFilter(float[] matrix) => new ProGpuColorFilter(default, null, (float[])matrix.Clone());
	public IEffectFilter CreateDropShadowFilter(float dx, float dy, float sigmaX, float sigmaY, UColor color) => new ProGpuEffectFilter(new DropShadowEffect(MathF.Max(sigmaX, sigmaY) * 2f) { Offset = new Vector2(dx, dy), Color = Color(color) });

	public IEffectFilter? CreateEffectFilter(EffectNode tree, URect bounds)
	{
		ArgumentNullException.ThrowIfNull(tree);
		if (TryCompileBackdrop(tree, out var backdrop))
		{
			return new ProGpuEffectFilter(new ProGpuBackdropEffect(backdrop));
		}
		return tree switch
		{
			BlurEffectNode blur when blur.Source is SourceInput => new ProGpuEffectFilter(new BlurEffect(blur.Sigma * 2f)),
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
					blur = MathF.Max(blur, blurNode.Sigma * 2f);
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

	private sealed class PictureVisual : Visual
	{
		private GpuPicture? _picture;
		private Matrix4x4 _transform = Matrix4x4.Identity;

		internal PictureVisual()
		{
		}

		internal PictureVisual(GpuPicture picture) => _picture = picture;

		internal void Update(GpuPicture picture, Matrix4x4 transform)
		{
			if (ReferenceEquals(_picture, picture) && _transform.Equals(transform))
			{
				return;
			}
			_picture = picture;
			_transform = transform;
			Invalidate();
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

internal sealed class ProGpuShader(Brush brush) : IShader { internal Brush Brush { get; } = brush; }
internal sealed record ProGpuColorFilter(Vector4 Color, BlendMode? Blend, float[]? Matrix) : IColorFilter;
internal sealed class ProGpuEffectFilter(object value) : IEffectFilter { internal object Value { get; } = value; public void Dispose() { } }
internal sealed record ProGpuBackdropEffect(BackdropMaterialBrush Material);

internal sealed class ProGpuTexture(GpuTexture texture) : ITexture
{
	private int _disposed;
	internal GpuTexture Texture { get; } = texture;
	public int PixelWidth => checked((int)Texture.Width);
	public int PixelHeight => checked((int)Texture.Height);
	public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Texture.Dispose(); }
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
		var transform = session.TotalMatrix;
		if (transform == Matrix4x4.Identity)
		{
			session.Context.DrawPicture(Picture);
		}
		else
		{
			// Uno records retained frames in logical pixels, then applies the target's
			// rasterization scale (and any host orientation) to the present session.
			// Preserve that outer transform when nesting the retained picture.
			session.Context.DrawPictureTransformed(Picture, transform);
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
	private bool _finished;

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
				Context = layer.Parent;
				Context.RetainResource(picture);
				Context.DrawVisual(new EffectPictureVisual(picture, layer.Effect));
				break;
		}
		_matrix = state.Matrix;
		_clipBounds = state.ClipBounds;
	}
	public void RestoreToCount(int count) { if (count < 1 || count > SaveCount) throw new ArgumentOutOfRangeException(nameof(count)); while (SaveCount > count) Restore(); }
	public void SaveLayer(bool antialias = false) => Save();
	public void SaveLayer(IColorFilter colorFilter, bool antialias = false)
	{
		if (colorFilter is ProGpuColorFilter { Blend: { } blend })
		{
			PushState(Scope.Blend);
			Context.PushBlendMode(ToBlend(blend));
			return;
		}
		Unsupported(nameof(SaveLayer));
		Save();
	}
	public void SaveLayer(BlendMode blendMode, bool antialias = false) { PushState(Scope.Blend); Context.PushBlendMode(ToBlend(blendMode)); }
	public void SaveLayer(IEffectFilter filter)
	{
		if (filter is not ProGpuEffectFilter { Value: EffectBase effect })
		{
			Unsupported(nameof(SaveLayer));
			Save();
			return;
		}

		var parent = Context;
		var recorder = new GpuPictureRecorder();
		PushState(Scope.EffectLayer, new EffectLayer(parent, recorder, effect));
		Context = recorder.BeginRecording(new PRect(0, 0, 1, 1));
	}

	public void ClipRect(in URect rect, ClipOperation operation = ClipOperation.Intersect, bool antialias = false)
	{
		if (operation == ClipOperation.Difference) { ClipPath(DifferenceRect(rect), operation, antialias); return; }
		_clipBounds = Intersect(_clipBounds, Rect(rect));
		Context.PushClip(Rect(rect), _matrix);
		_clips.Push(new ClipState(ClipScope.Rect));
	}
	public void ClipRoundRect(in RoundRectangle roundRect, ClipOperation operation = ClipOperation.Intersect, bool antialias = false)
	{
		if (operation == ClipOperation.Difference && TryCoalesceRoundedDifference(roundRect))
		{
			return;
		}
		using var geometry = RoundedGeometry(roundRect);
		ClipPath(geometry, operation, antialias);
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
	public void ClipPath(IGeometry geometry, ClipOperation operation = ClipOperation.Intersect, bool antialias = false)
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
		Context.PushBlendMode(GpuBlendMode.Src);
		Context.DrawRectangle(new SolidColorBrush(color), null, new PRect(-1_000_000, -1_000_000, 2_000_000, 2_000_000), Matrix4x4.Identity);
		Context.PopBlendMode();
	}
	public void DrawRect(in URect rect, UColor color, bool antialias = false) => Context.DrawRectangle(Solid(color), null, Rect(rect), _matrix);
	public void DrawRect(in URect rect, IShader shader, bool antialias = false) => Context.DrawRectangle(Shader(shader), null, Rect(rect), _matrix);
	public void DrawRoundedRect(in URect rect, Vector4 radii, UColor color, bool antialias = false)
	{
		if (radii.X == radii.Y && radii.X == radii.Z && radii.X == radii.W) Context.DrawRoundedRectangle(Solid(color), null, Rect(rect), radii.X, radii.X, _matrix);
		else { using var geometry = RoundedGeometry(rect, radii); DrawPath(geometry, color, antialias); }
	}
	public void DrawRoundedRectBorder(in URect outer, Vector4 outerRadii, in URect inner, Vector4 innerRadii, UColor color, bool antialias = false)
	{
		var left = (float)(inner.X - outer.X);
		var top = (float)(inner.Y - outer.Y);
		var right = (float)(outer.Right - inner.Right);
		var bottom = (float)(outer.Bottom - inner.Bottom);
		if (left > 0f && NearlyEqual(left, top) && NearlyEqual(left, right) && NearlyEqual(left, bottom) &&
			AllEqual(outerRadii) && AllEqual(innerRadii))
		{
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
		ClipPath(border, ClipOperation.Intersect, antialias);
		DrawRoundedRect(outer, outerRadii, color, antialias);
		RestoreToCount(restoreCount);
	}
	public void DrawPath(IGeometry geometry, UColor color, bool antialias = false)
	{
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
			ClipPath(native, ClipOperation.Intersect, antialias);
			DrawRect(native.Bounds, color, antialias);
			RestoreToCount(restoreCount);
			return;
		}
		Context.DrawPath(Solid(color), null, native.Path, _matrix);
	}
	public void DrawShadow(IGeometry silhouette, UColor color, float sigmaX, float sigmaY, bool additive, bool antialias = false)
	{
		// A retained shadow visual is the native ProGPU effect path; the geometry is recorded once into it.
		var recorder = new GpuPictureRecorder();
		var child = recorder.BeginRecording(new PRect(0, 0, 1, 1));
		child.DrawPath(Solid(color), null, ProGpuGeometryFactory.Import(silhouette).Path, _matrix);
		var picture = recorder.EndRecording();
		Context.RetainResource(picture);
		Context.DrawVisual(new EffectPictureVisual(picture, new DropShadowEffect(MathF.Max(sigmaX, sigmaY) * 2f) { Color = ProGpuDrawingFactory.Color(color) }));
	}
	public void StrokePath(IGeometry geometry, UColor color, float strokeWidth, bool antialias = false) => Context.DrawPath(null, new Pen(Solid(color), strokeWidth), ProGpuGeometryFactory.Import(geometry).Path, _matrix);
	public void DrawLine(Vector2 p0, Vector2 p1, UColor color, float strokeWidth, bool antialias = false) => Context.DrawLine(new Pen(Solid(color), strokeWidth), p0, p1, _matrix);
	public void DrawImage(ITexture texture, float x, float y, ImageSampling sampling, float opacity = 1, bool antialias = false) => DrawTexture(texture, new URect(x, y, texture.PixelWidth, texture.PixelHeight), sampling, opacity);
	public void DrawImage(ITexture texture, float x, float y, ImageSampling sampling, IColorFilter colorFilter, bool antialias = false)
	{
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, ((ProGpuDrawingFactory)Factory).Context))
		{
			throw new ArgumentException("Texture belongs to another drawing factory.", nameof(texture));
		}
		if (colorFilter is not ProGpuColorFilter filter) throw new ArgumentException("Color filter belongs to another backend.", nameof(colorFilter));
		var matrix = filter.Matrix is { Length: >= 20 } values
			? Matrix(values)
			: filter.Blend == BlendMode.SrcIn
				? TintMatrix(filter.Color)
				: (ImageEffectColorMatrix?)null;
		if (matrix is null)
		{
			Unsupported(nameof(DrawImage));
			DrawTexture(texture, new URect(x, y, texture.PixelWidth, texture.PixelHeight), sampling, 1);
			return;
		}
		Context.DrawImageWithEffect(native.Texture, new PRect(x, y, texture.PixelWidth, texture.PixelHeight),
			sourceRect: new PRect(0, 0, texture.PixelWidth, texture.PixelHeight),
			samplingMode: sampling == ImageSampling.NearestNeighbor ? TextureSamplingMode.Nearest : TextureSamplingMode.Linear,
			colorMatrix: matrix, transform: _matrix);
	}
	public void DrawImageNineSlice(ITexture texture, in URect centerSlice, in URect destination, bool centerHollow, bool antialias = false)
	{
		var x = new[] { 0f, (float)centerSlice.X, (float)centerSlice.Right, texture.PixelWidth };
		var y = new[] { 0f, (float)centerSlice.Y, (float)centerSlice.Bottom, texture.PixelHeight };
		var dx = new[] { (float)destination.X, (float)destination.X + x[1], (float)destination.Right - (texture.PixelWidth - x[2]), (float)destination.Right };
		var dy = new[] { (float)destination.Y, (float)destination.Y + y[1], (float)destination.Bottom - (texture.PixelHeight - y[2]), (float)destination.Bottom };
		for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
		{
			if (centerHollow && row == 1 && column == 1) continue;
			DrawTexturePart(texture, new PRect(x[column], y[row], x[column + 1] - x[column], y[row + 1] - y[row]), new PRect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]), ImageSampling.Linear, 1);
		}
	}
	public void DrawEffectBackdrop(IEffectFilter filter, float opacity)
	{
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

	private void DrawTexture(ITexture texture, URect destination, ImageSampling sampling, float opacity) => DrawTexturePart(texture, new PRect(0, 0, texture.PixelWidth, texture.PixelHeight), Rect(destination), sampling, opacity);
	private void DrawTexturePart(ITexture texture, PRect source, PRect destination, ImageSampling sampling, float opacity)
	{
		if (texture is not ProGpuTexture native || !ReferenceEquals(native.Texture.Context, ((ProGpuDrawingFactory)Factory).Context)) throw new ArgumentException("Texture belongs to another drawing factory.", nameof(texture));
		if (opacity < 1) Context.PushOpacity(Math.Clamp(opacity, 0, 1));
		Context.DrawTexture(native.Texture, destination, source, _matrix, sampling == ImageSampling.NearestNeighbor ? TextureSamplingMode.Nearest : TextureSamplingMode.Linear);
		if (opacity < 1) Context.PopOpacity();
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
		new Vector4(0, 0, 0, color.X),
		new Vector4(0, 0, 0, color.Y),
		new Vector4(0, 0, 0, color.Z),
		new Vector4(0, 0, 0, color.W),
		Vector4.Zero);
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
	private sealed record EffectLayer(DrawingContext Parent, GpuPictureRecorder Recorder, EffectBase Effect);
	private enum Scope { None, Blend, EffectLayer }
	private enum ClipScope { Rect, Geometry, CoalescedRoundedDifference }
	private sealed class EffectPictureVisual : Visual
	{
		private readonly GpuPicture _picture;
		internal EffectPictureVisual(GpuPicture picture, EffectBase effect) { _picture = picture; Effect = effect; }
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
