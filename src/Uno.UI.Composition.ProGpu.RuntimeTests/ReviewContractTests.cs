extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using Color = unouwp::Windows.UI.Color;
using Rect = unofoundation::Windows.Foundation.Rect;

internal static class ReviewContractTests
{
	public static async Task Run(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var failures = new List<Exception>();
		foreach (var test in new Func<Task>[]
		{
			() => BorrowedDeviceLoss(),
			() => ExternalTexture(factory),
			() => DiagnosticFailure(factory),
			() => CurveBounds(geometries),
			() => RetinaClip(factory, geometries, false),
			() => RetinaClip(factory, geometries, true),
			() => RetinaClip(factory, geometries, true, true),
			() => LargeNestedClip(factory, geometries),
			() => EffectGraphRejection(factory),
			() => LayerBlur(factory),
			() => BackdropBounds(factory),
			() => ClearOnlyMask(factory, false),
			() => ClearOnlyMask(factory, true),
			() => FractionalMask(factory),
			() => ClearPremultiplied(factory),
			() => CanonicalClipCapacity(factory),
		})
		{
			try
			{
				await test();
			}
			catch (Exception error)
			{
				failures.Add(error);
				Console.WriteLine(error);
			}
		}
		if (failures.Count != 0)
		{
			throw new AggregateException("Backend review regressions", failures);
		}
	}

	private static Task BorrowedDeviceLoss()
	{
		using var context = new ProGPU.Backend.WgpuContext();
		var type = typeof(ProGpuDrawingFactory).Assembly.GetType("Uno.UI.Composition.ProGpu.UnoBorrowedWebGpuLifetime")!;
		type.GetMethod("ObserveCompletion", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [context, Silk.NET.WebGPU.QueueWorkDoneStatus.DeviceLost]);
		if (!context.IsDeviceLost)
		{
			throw new InvalidOperationException("Queue device loss must mark the borrowed context before disposal.");
		}
		return Task.CompletedTask;
	}

	private static async Task ExternalTexture(ProGpuDrawingFactory factory)
	{
		var interop = (object)factory as IWebGpuTextureInterop
			?? throw new InvalidOperationException("The WebGPU drawing factory must support Uno's texture interop interface.");
		ITexture Create()
		{
			unsafe
			{
				return interop.CreateRenderTexture(8, 8, out _, out _);
			}
		}
		using var source = Create();
		var recorder = factory.CreateRecording();
		recorder.DrawImage(source, 0, 0);
		using var record = recorder.Finish();
		source.Dispose();
		using var output = factory.CreateTexture(8, 8, new byte[8 * 8 * 4]);
		using var target = Target(output);
		unsafe void Write(byte green)
		{
			var native = Native(source);
			var destination = new Silk.NET.WebGPU.ImageCopyTexture { Texture = native.TexturePtr };
			var layout = new Silk.NET.WebGPU.TextureDataLayout { BytesPerRow = 32, RowsPerImage = 8 };
			var size = new Silk.NET.WebGPU.Extent3D { Width = 8, Height = 8, DepthOrArrayLayers = 1 };
			var bytes = new byte[8 * 8 * 4];
			for (var i = 0; i < bytes.Length; i += 4)
			{
				bytes[i + 1] = green;
				bytes[i + 3] = 255;
			}
			fixed (byte* data = bytes)
			{
				interop.Api.QueueWriteTexture(interop.Queue, &destination, data, (nuint)bytes.Length, &layout, &size);
			}
		}
		foreach (var green in new byte[] { 40, 200 })
		{
			Write(green);
			using (var session = factory.BeginPresent(target))
			{
				record.Replay(session);
			}
			using var image = await factory.SnapshotAsync(output);
			var pixels = new byte[8 * 8 * 4];
			image.CopyPixels(pixels);
			if (pixels[1] != green || pixels[3] != 255)
			{
				throw new InvalidOperationException($"External texture writes must invalidate retained presentation: {pixels[1]} != {green}.");
			}
		}
	}

	private static async Task DiagnosticFailure(ProGpuDrawingFactory factory)
	{
		var previous = Environment.GetEnvironmentVariable("UNO_PROGPU_DUMP_SCENE");
		var previousFrame = Environment.GetEnvironmentVariable("UNO_PROGPU_DUMP_FRAME");
		try
		{
			Environment.SetEnvironmentVariable("UNO_PROGPU_DUMP_SCENE", System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString(), "scene.txt"));
			Environment.SetEnvironmentVariable("UNO_PROGPU_DUMP_FRAME", "1");
			using var texture = factory.CreateTexture(8, 8, new byte[8 * 8 * 4]);
			var recorder = factory.CreateRecording();
			recorder.Clear(Color.FromArgb(255, 0, 255, 0));
			using var record = recorder.Finish();
			Present(factory, texture, record);
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[8 * 8 * 4];
			image.CopyPixels(pixels);
			if (pixels[1] != 255 || pixels[3] != 255)
			{
				throw new InvalidOperationException("Diagnostic I/O failure must not prevent presentation.");
			}
		}
		finally
		{
			Environment.SetEnvironmentVariable("UNO_PROGPU_DUMP_SCENE", previous);
			Environment.SetEnvironmentVariable("UNO_PROGPU_DUMP_FRAME", previousFrame);
		}
	}

	private static ProGPU.Backend.GpuTexture Native(ITexture texture) =>
		(ProGPU.Backend.GpuTexture)texture.GetType().GetProperty("Texture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(texture)!;

	private static unsafe SmokeTarget Target(ITexture texture) => new(Native(texture).ViewPtr, texture.PixelWidth, texture.PixelHeight);

	private static async Task ClearPremultiplied(ProGpuDrawingFactory factory)
	{
		foreach (var alpha in new byte[] { 0, 128, 255 })
		{
			foreach (var leading in new[] { true, false })
			{
				using var texture = factory.RenderOffscreen(4, 4, drawing =>
				{
					if (!leading)
					{
						drawing.DrawRect(new Rect(0, 0, 4, 4), Color.FromArgb(255, 255, 255, 255));
					}
					drawing.Clear(Color.FromArgb(alpha, 255, 255, 255));
				});
				using var image = await factory.SnapshotAsync(texture);
				var pixels = new byte[4 * 4 * 4];
				image.CopyPixels(pixels);
				foreach (var value in pixels)
				{
					if (Math.Abs(value - alpha) > 1)
					{
						throw new InvalidOperationException($"Clear must produce premultiplied pixels: alpha={alpha}, leading={leading}, value={value}.");
					}
				}
			}
		}
	}

	private static async Task BackdropBounds(ProGpuDrawingFactory factory)
	{
		using var blur = factory.CreateEffectFilter(new BlurEffectNode(new SourceInput(), 3, false), new Rect(16, 16, 32, 32))!;
		using var identity = factory.CreateColorMatrixColorFilter([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
		async Task<byte[]> Render(bool layer, float opacity)
		{
			using var texture = factory.RenderOffscreen(64, 64, drawing =>
			{
				if (layer)
				{
					drawing.SaveLayer(identity);
				}
				drawing.DrawRect(new Rect(0, 0, 32, 64), Color.FromArgb(255, 255, 0, 0));
				drawing.DrawRect(new Rect(32, 0, 32, 64), Color.FromArgb(255, 0, 0, 255));
				drawing.ClipRect(new Rect(0, 0, 64, 64));
				drawing.DrawEffectBackdrop(blur, opacity);
				if (layer)
				{
					drawing.Restore();
				}
			});
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			return pixels;
		}
		var lowOpacityBlue = 0;
		foreach (var opacity in new[] { 0.25f, 0.75f })
		{
			var direct = await Render(false, opacity);
			var nested = await Render(true, opacity);
			var blue = direct[(32 * 64 + 31) * 4];
			if (opacity == 0.25f)
			{
				lowOpacityBlue = blue;
			}
			else if (blue < lowOpacityBlue * 2)
			{
				throw new InvalidOperationException("Backdrop opacity must change its contribution to the result.");
			}
			if (direct[(8 * 64 + 31) * 4] != 0 || direct[(32 * 64 + 31) * 4] == 0)
			{
				throw new InvalidOperationException("Backdrop blur must affect only its declared bounds, even with a larger clip.");
			}
			for (var i = 0; i < direct.Length; i++)
			{
				if (Math.Abs(direct[i] - nested[i]) > 3)
				{
					throw new InvalidOperationException($"Nested backdrop must preserve content and opacity {opacity}: byte {i}, direct={direct[i]}, nested={nested[i]}.");
				}
			}
		}
	}

	private static async Task FractionalMask(ProGpuDrawingFactory factory)
	{
		async Task<byte[]> Render(bool mask)
		{
			using var texture = factory.RenderOffscreen(64, 64, drawing =>
			{
				drawing.Clear(Color.FromArgb(mask ? (byte)255 : (byte)0, 255, 255, 255));
				if (mask)
				{
					drawing.SaveLayerMask();
				}
				drawing.DrawRect(new Rect(20.5, 20.5, 24, 24), Color.FromArgb(255, 255, 255, 255));
				if (mask)
				{
					drawing.Restore();
				}
			});
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			return pixels;
		}
		var expected = await Render(false);
		var actual = await Render(true);
		for (var i = 3; i < expected.Length; i += 4)
		{
			if (Math.Abs(expected[i] - actual[i]) > 2)
			{
				throw new InvalidOperationException($"Mask edge coverage applied twice at byte {i}: expected={expected[i]}, actual={actual[i]}.");
			}
		}
	}

	private static async Task CanonicalClipCapacity(ProGpuDrawingFactory factory)
	{
		using var texture = factory.RenderOffscreen(2048, 1536, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.Scale(2, 2);
			for (var i = 0; i < 10; i++)
			{
				drawing.Save();
				drawing.ClipRoundRect(new RoundRectangle { Rect = new Rect(0, 0, 976 - i, 712 - i), TopLeft = new Vector2(8.5f + i) });
				drawing.DrawRect(new Rect(0, 0, 976, 712), Color.FromArgb(255, 255, 255, 255));
				drawing.Restore();
			}
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[2048 * 1536 * 4];
		image.CopyPixels(pixels);
		if (pixels[(100 * 2048 + 100) * 4 + 3] != 255)
		{
			throw new InvalidOperationException("Large asymmetric clips must preserve interior coverage without exhausting the atlas.");
		}
	}

	private static async Task LargeNestedClip(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(75, 50), 75, 50);
		using var ellipse = builder.Build();
		var child = factory.CreateRecording();
		child.ClipPath(ellipse);
		child.DrawRect(new Rect(0, 0, 150, 100), Color.FromArgb(255, 255, 255, 255));
		using var childRecord = child.Finish();
		var frame = factory.CreateRecording();
		frame.Clear(Color.FromArgb(0, 0, 0, 0));
		frame.SetMatrix(Matrix4x4.CreateScale(2, 2, 1) * Matrix4x4.CreateTranslation(96, 112, 0));
		frame.ClipRoundRect(new RoundRectangle { Rect = new Rect(0, 0, 976, 712), TopLeft = new Vector2(8.5f) });
		frame.SetMatrix(Matrix4x4.CreateScale(2, 2, 1) * Matrix4x4.CreateTranslation(976, 173, 0));
		childRecord.Replay(frame);
		using var commands = frame.Finish();
		using var texture = factory.CreateTexture(2048, 1536, new byte[2048 * 1536 * 4]);
		Present(factory, texture, commands);
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[2048 * 1536 * 4];
		image.CopyPixels(pixels);
		for (var y = 183; y < 363; y++)
		{
			if (pixels[(y * 2048 + 1126) * 4 + 3] < 250)
			{
				throw new InvalidOperationException($"Full-window nested ellipse has a gap at y={y}.");
			}
		}
	}

	private static Task EffectGraphRejection(ProGpuDrawingFactory factory)
	{
		using var effect = factory.CreateEffectFilter(new ColorMatrixEffectNode(new BlurEffectNode(new SourceInput(), 2, false), new float[20]), new Rect(0, 0, 64, 64));
		if (effect is not null)
		{
			throw new InvalidOperationException("Unsupported effect graphs must be rejected rather than losing the zero-alpha matrix.");
		}
		using var clamped = factory.CreateEffectFilter(new BlurEffectNode(new SourceInput(), 2, true), new Rect(0, 0, 64, 64));
		if (clamped is not null)
		{
			throw new InvalidOperationException("Unsupported clamp-edge blur must use Uno's fallback, not silently soften its edges.");
		}
		return Task.CompletedTask;
	}

	private static async Task LayerBlur(ProGpuDrawingFactory factory)
	{
		using var effect = factory.CreateEffectFilter(new BlurEffectNode(new SourceInput(), 2, false), new Rect(0, 0, 64, 64))
			?? throw new InvalidOperationException("A source blur must compile.");
		using var texture = factory.RenderOffscreen(64, 64, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.SaveLayer(effect);
			drawing.DrawRect(new Rect(20, 20, 24, 24), Color.FromArgb(255, 255, 255, 255));
			drawing.Restore();
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[64 * 64 * 4];
		image.CopyPixels(pixels);
		var edge = pixels[(32 * 64 + 19) * 4 + 3];
		if (edge is < 10 or > 200 || pixels[(32 * 64 + 32) * 4 + 3] < 240)
		{
			throw new InvalidOperationException($"Layer blur must soften the source edge: alpha={edge}.");
		}
	}

	private static async Task ClearOnlyMask(ProGpuDrawingFactory factory, bool nonempty)
	{
		using var texture = factory.RenderOffscreen(64, 64, drawing =>
		{
			drawing.Clear(Color.FromArgb(255, 255, 255, 255));
			drawing.SaveLayerMask();
			if (nonempty)
			{
				drawing.DrawRect(new Rect(20, 20, 24, 24), Color.FromArgb(255, 255, 255, 255));
			}
			drawing.Restore();
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[64 * 64 * 4];
		image.CopyPixels(pixels);
		if (pixels[(32 * 64 + 32) * 4 + 3] != (nonempty ? 255 : 0) || pixels[(4 * 64 + 4) * 4 + 3] != 0)
		{
			throw new InvalidOperationException("A destination-in mask must clear a clear-only destination outside its source.");
		}
	}

	private static Task CurveBounds(ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePathBuilder();
		builder.MoveTo(Vector2.Zero);
		builder.QuadraticTo(new Vector2(100, 100), new Vector2(200, 0));
		using var quadratic = builder.Build();
		if (Math.Abs(quadratic.Bounds.Height - 50) > 0.001)
		{
			throw new InvalidOperationException($"Quadratic bounds must be tight: {quadratic.Bounds}");
		}
		builder.MoveTo(Vector2.Zero);
		builder.CubicTo(new Vector2(0, 100), new Vector2(100, 100), new Vector2(100, 0));
		using var cubic = builder.Build();
		if (Math.Abs(cubic.Bounds.Height - 75) > 0.001)
		{
			throw new InvalidOperationException($"Cubic bounds must be tight: {cubic.Bounds}");
		}
		return Task.CompletedTask;
	}

	private static async Task RetinaClip(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries, bool retained, bool present = false)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(50), 50, 50);
		using var ellipse = builder.Build();
		using var shader = factory.CreateLinearGradientShader(Vector2.Zero, new Vector2(100), [Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255)], [0, 1], GradientTileMode.Clamp, Matrix3x2.Identity);
		var recorder = factory.CreateRecording();
		recorder.ClipPath(ellipse);
		recorder.DrawRect(new Rect(0, 0, 100, 100), shader);
		using var record = recorder.Finish();
		void Draw(IDrawingSession drawing)
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.ClipRoundRect(new RoundRectangle
			{
				Rect = new Rect(0, 0, 256, 256),
				TopLeft = new Vector2(8), TopRight = new Vector2(8),
				BottomLeft = new Vector2(8), BottomRight = new Vector2(8),
			});
			drawing.Scale(2, 2);
			drawing.Translate(10, 10);
			if (retained)
			{
				record.Replay(drawing);
			}
			else
			{
				drawing.ClipPath(ellipse);
				drawing.DrawRect(new Rect(0, 0, 100, 100), shader);
			}
		}
		using var target = present ? factory.CreateTexture(256, 256, new byte[256 * 256 * 4]) : factory.RenderOffscreen(256, 256, Draw);
		if (present)
		{
			var frame = factory.CreateRecording();
			Draw(frame);
			using var commands = frame.Finish();
			Present(factory, target, commands);
		}
		using var image = await factory.SnapshotAsync(target);
		var pixels = new byte[256 * 256 * 4];
		image.CopyPixels(pixels);
		for (var y = 30; y < 210; y++)
		{
			if (pixels[(y * 256 + 120) * 4 + 3] < 250)
			{
				throw new InvalidOperationException($"Scaled ellipse clip has a gap at y={y}, retained={retained}, present={present}.");
			}
		}
	}

	private static unsafe void Present(ProGpuDrawingFactory factory, ITexture texture, IRenderRecord record)
	{
		var native = (ProGPU.Backend.GpuTexture)texture.GetType().GetProperty("Texture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(texture)!;
		using var target = new SmokeTarget(native.ViewPtr, texture.PixelWidth, texture.PixelHeight);
		using var session = factory.BeginPresent(target);
		record.Replay(session);
	}
}
