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
			() => TextureRetirement(factory),
			() => DifferenceClip(factory, geometries, false),
			() => DifferenceClip(factory, geometries, true),
			() => RetainedDifference(factory, geometries),
			() => DifferenceClear(factory, geometries),
			() => DifferenceCapacity(factory, geometries),
			() => DifferenceRetention(factory, geometries),
			() => DifferenceBackdrop(factory, geometries),
			() => ExternalTexture(factory),
			() => DiagnosticFailure(factory),
			() => CurveBounds(geometries),
			() => StrokeWinding(factory, geometries),
			() => RetinaClip(factory, geometries, false),
			() => RetinaClip(factory, geometries, true),
			() => RetinaClip(factory, geometries, true, true),
			() => LargeNestedClip(factory, geometries),
			() => EffectGraphRejection(factory),
			() => LayerBlur(factory),
			() => BackdropBounds(factory),
			() => AcrylicMaterial(factory),
			() => AcrylicDpi(factory),
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

	private static Task TextureRetirement(ProGpuDrawingFactory factory)
	{
		using var texture = factory.CreateTexture(1, 1, new byte[4]);
		var native = Native(texture);
		var notified = false;
		var serialized = false;
		void OnRetired(ulong id)
		{
			if (id == native.Id)
			{
				notified = true;
				serialized = System.Threading.Monitor.IsEntered(native.Context.RenderLock);
			}
		}
		ProGPU.Backend.GpuTexture.OnDisposedWithId += OnRetired;
		try
		{
			texture.Dispose();
		}
		finally
		{
			ProGPU.Backend.GpuTexture.OnDisposedWithId -= OnRetired;
		}
		if (!notified || !serialized)
		{
			throw new InvalidOperationException("Native texture retirement notifications must be serialized with compositor cache mutation.");
		}
		return Task.CompletedTask;
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

	private static async Task StrokeWinding(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePathBuilder();
		builder.MoveTo(new Vector2(10, 30));
		builder.LineTo(new Vector2(30, 30));
		builder.LineTo(new Vector2(30, 50));
		builder.LineTo(new Vector2(50, 50));
		builder.LineTo(new Vector2(50, 26));
		builder.LineTo(new Vector2(10, 26));
		using var path = builder.Build();
		using var stroke = path.GetStrokeFillGeometry(new StrokeStyle { Thickness = 12, LineJoin = StrokeJoin.Miter, MiterLimit = 4 });
		using var texture = factory.RenderOffscreen(64, 64, drawing =>
		{
			drawing.ClipPath(stroke);
			drawing.DrawRect(new Rect(0, 0, 64, 64), Color.FromArgb(255, 255, 255, 255));
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[64 * 64 * 4];
		image.CopyPixels(pixels);
		if (!stroke.FillContains(new Vector2(34.5f, 26.5f)) || pixels[(26 * 64 + 34) * 4 + 3] < 250)
		{
			throw new InvalidOperationException("Overlapping stroke segments and joins must form a union, without winding cancellation holes.");
		}
	}

	private static async Task DifferenceBackdrop(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(32), 10, 10);
		using var ellipse = builder.Build();
		using var blur = factory.CreateEffectFilter(new BlurEffectNode(new SourceInput(), 3, false), new Rect(0, 0, 64, 64))!;
		async Task<byte[]> Render(bool clip)
		{
			using var texture = factory.RenderOffscreen(64, 64, drawing =>
			{
				drawing.DrawRect(new Rect(0, 0, 32, 64), Color.FromArgb(255, 255, 0, 0));
				drawing.DrawRect(new Rect(32, 0, 32, 64), Color.FromArgb(255, 0, 0, 255));
				if (clip)
				{
					drawing.ClipPath(ellipse, ClipOperation.Difference);
				}
				drawing.DrawEffectBackdrop(blur, 0.75f);
			});
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			return pixels;
		}
		var expected = await Render(false);
		var actual = await Render(true);
		for (var y = 2; y < 62; y++)
		{
			for (var x = 2; x < 62; x++)
			{
				var distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(32));
				if (distance > 12)
				{
					for (var channel = 0; channel < 4; channel++)
					{
						var index = (y * 64 + x) * 4 + channel;
						if (Math.Abs(actual[index] - expected[index]) > 3)
						{
							throw new InvalidOperationException("Inverse clipping must preserve unpartitioned backdrop sampling outside the hole.");
						}
					}
				}
				else if (distance < 8 && (actual[(y * 64 + x) * 4] != (x < 32 ? 0 : 255) || actual[(y * 64 + x) * 4 + 2] != (x < 32 ? 255 : 0)))
				{
					throw new InvalidOperationException("Inverse clipping must preserve original backdrop pixels inside the hole.");
				}
			}
		}
	}

	private static async Task DifferenceCapacity(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(50), 10, 10);
		using var ellipse = builder.Build();
		using var texture = factory.RenderOffscreen(4096, 256, drawing =>
		{
			for (var i = 0; i < 8; i++)
			{
				drawing.ClipPath(ellipse, ClipOperation.Difference);
			}
			drawing.DrawRect(new Rect(0, 0, 4096, 256), Color.FromArgb(255, 255, 255, 255));
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[4096 * 256 * 4];
		image.CopyPixels(pixels);
		if (pixels[(50 * 4096 + 50) * 4 + 3] != 0 || pixels[(50 * 4096 + 4090) * 4 + 3] != 255)
		{
			throw new InvalidOperationException("Nested inverse clips must support 4K-wide targets without atlas-sized outer paths.");
		}
	}

	private static async Task DifferenceRetention(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(50), 10, 10);
		using var ellipse = builder.Build();
		using var identity = factory.CreateColorMatrixColorFilter([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
		using var source = factory.CreateTexture(1, 1, new byte[] { 0, 255, 0, 255 });
		var child = factory.CreateRecording();
		child.SaveLayer(identity);
		child.ClipPath(ellipse, ClipOperation.Difference);
		child.Clear(Color.FromArgb(0, 0, 0, 0));
		child.DrawImageTiled(source, new Rect(0, 0, 100, 100), EdgeExtend.Wrap, EdgeExtend.Wrap);
		child.Restore();
		using var childRecord = child.Finish();
		var parent = factory.CreateRecording();
		parent.Clear(Color.FromArgb(0, 0, 0, 0));
		childRecord.Replay(parent);
		using var record = parent.Finish();
		childRecord.Dispose();
		source.Dispose();
		foreach (var size in new[] { 100, 160 })
		{
			using var output = factory.CreateTexture(size, size, new byte[size * size * 4]);
			using var target = Target(output);
			for (var frame = 0; frame < 2; frame++)
			{
				using (var session = factory.BeginPresent(target))
				{
					record.Replay(session);
				}
				if (frame == 1 && ProGpuDiagnostics.LastFrame?.TargetContentReused != true)
				{
					throw new InvalidOperationException("Unchanged inverse recordings must reuse retained target contents.");
				}
			}
			using var image = await factory.SnapshotAsync(output);
			var pixels = new byte[size * size * 4];
			image.CopyPixels(pixels);
			if (pixels[(20 * size + 20) * 4 + 1] != 255 || pixels[(50 * size + 50) * 4 + 3] != 0)
			{
				throw new InvalidOperationException($"Deferred clips lifetime/effect/resize: size={size}, outside={pixels[(20 * size + 20) * 4 + 1]},{pixels[(20 * size + 20) * 4 + 3]}, hole={pixels[(50 * size + 50) * 4 + 1]},{pixels[(50 * size + 50) * 4 + 3]}.");
			}
		}
	}

	private static async Task DifferenceClear(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(50), 10, 10);
		using var ellipse = builder.Build();
		using var texture = factory.RenderOffscreen(100, 100, drawing =>
		{
			drawing.Clear(Color.FromArgb(255, 255, 0, 0));
			drawing.ClipPath(ellipse, ClipOperation.Difference);
			drawing.Clear(Color.FromArgb(128, 255, 255, 255));
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[100 * 100 * 4];
		image.CopyPixels(pixels);
		var center = (50 * 100 + 50) * 4;
		if (pixels[3] != 128 || pixels[1] != 128 || pixels[center + 1] != 0 || pixels[center + 2] != 255 || pixels[center + 3] != 255)
		{
			throw new InvalidOperationException($"Inverse-clipped clear must preserve the excluded destination: outside={pixels[3]}, hole={pixels[center + 1]},{pixels[center + 2]},{pixels[center + 3]}.");
		}
	}

	private static async Task RetainedDifference(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(25, 50), 10, 10);
		using var ellipse = builder.Build();
		var recorder = factory.CreateRecording();
		recorder.ClipPath(ellipse, ClipOperation.Difference);
		recorder.DrawRect(new Rect(-100, -100, 300, 300), Color.FromArgb(128, 255, 255, 255));
		using var record = recorder.Finish();
		foreach (var rotation in new[] { 0f, 0.3f })
		{
			var transform = Matrix4x4.CreateRotationZ(rotation) * Matrix4x4.CreateTranslation(20.25f, 0.5f, 0);
			Matrix4x4.Invert(transform, out var inverse);
			using var texture = factory.RenderOffscreen(100, 100, drawing =>
			{
				drawing.SetMatrix(transform);
				record.Replay(drawing);
			});
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[100 * 100 * 4];
			image.CopyPixels(pixels);
			for (var y = 2; y < 98; y++)
			{
				for (var x = 2; x < 98; x++)
				{
					var local = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), inverse);
					var distance = Vector2.Distance(local, new Vector2(25, 50));
					if (distance is > 8 and < 12)
					{
						continue;
					}
					var expected = distance < 8 ? 0 : 128;
					if (Math.Abs(pixels[(y * 100 + x) * 4 + 3] - expected) > 2)
					{
						throw new InvalidOperationException($"Retained difference clip seam at {x},{y}, rotation={rotation}, alpha={pixels[(y * 100 + x) * 4 + 3]} expected={expected}.");
					}
				}
			}
		}
	}

	private static async Task DifferenceClip(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries, bool transformed)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddEllipse(new Vector2(25, 50), 10, 10);
		using var ellipse = builder.Build();
		using var texture = factory.RenderOffscreen(100, 100, drawing =>
		{
			if (transformed)
			{
				drawing.ClipRect(new Rect(0, 0, 100, 100));
				drawing.Translate(50, 0);
			}
			drawing.ClipPath(ellipse, ClipOperation.Difference);
			drawing.DrawRect(new Rect(transformed ? -50 : 0, 0, 100, 100), Color.FromArgb(255, 255, 255, 255));
		});
		using var image = await factory.SnapshotAsync(texture);
		var pixels = new byte[100 * 100 * 4];
		image.CopyPixels(pixels);
		var centerX = transformed ? 75 : 25;
		if (pixels[(50 * 100 + 5) * 4 + 3] != 255 || pixels[(50 * 100 + centerX) * 4 + 3] != 0)
		{
			throw new InvalidOperationException($"Difference clip must preserve the physical outer clip across transforms: transformed={transformed}.");
		}
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

	private static async Task AcrylicDpi(ProGpuDrawingFactory factory)
	{
		async Task<byte[]> Render(bool scaled, bool retained = false)
		{
			var size = scaled ? 32 : 64;
			var graph = new BlendEffectNode(
				new BlendEffectNode(new BlurEffectNode(new SourceInput(), scaled ? 3 : 6, true), new ColorInput(default), BlendMode.Luminosity),
				new ColorInput(default), BlendMode.Color);
			using var filter = factory.CreateEffectFilter(graph, new Rect(0, 0, size, size))!;
			void Draw(IDrawingSession drawing)
			{
				drawing.DrawRect(new Rect(0, 0, size, size), Color.FromArgb(255, 0, 0, 0));
				drawing.DrawRect(new Rect(size / 2, 0, size / 2, size), Color.FromArgb(255, 255, 255, 255));
				drawing.DrawEffectBackdrop(filter, 1);
			}
			var recorder = factory.CreateRecording();
			Draw(recorder);
			using var record = recorder.Finish();
			using var texture = factory.RenderOffscreen(64, 64, drawing =>
			{
				if (scaled)
				{
					drawing.Scale(2, 2);
				}
				if (retained)
				{
					record.Replay(drawing);
				}
				else
				{
					Draw(drawing);
				}
			});
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			return pixels;
		}
		var scaled = await Render(true);
		var physical = await Render(false);
		var retained = await Render(true, true);
		for (var x = 16; x < 48; x++)
		{
			var offset = (32 * 64 + x) * 4;
			if (Math.Abs(scaled[offset] - physical[offset]) > 2 || Math.Abs(retained[offset] - physical[offset]) > 2)
			{
				throw new InvalidOperationException($"Acrylic DPI must scale blur width: x={x}, scaled={scaled[offset]}, physical={physical[offset]}.");
			}
		}
	}

	private static async Task AcrylicMaterial(ProGpuDrawingFactory factory)
	{
		var background = Color.FromArgb(255, 32, 64, 96);
		var luminosity = Color.FromArgb(160, 180, 190, 200);
		var tint = Color.FromArgb(96, 100, 140, 240);
		var bounds = new Rect(16, 16, 32, 32);
		var graph = new BlendEffectNode(
			new BlendEffectNode(new BlurEffectNode(new SourceInput(), 3, true), new ColorInput(luminosity), BlendMode.Luminosity),
			new ColorInput(tint), BlendMode.Color);
		using var unsupported = factory.CreateEffectFilter(graph with { Mode = BlendMode.Multiply }, bounds);
		if (unsupported is not null)
		{
			throw new InvalidOperationException("The acrylic mapping must preserve the graph's blend operators.");
		}
		using var filter = factory.CreateEffectFilter(graph, bounds)
			?? throw new InvalidOperationException("Uno's acrylic material graph must be supported.");
		using var identity = factory.CreateColorMatrixColorFilter([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
		var original = new Vector3(background.R, background.G, background.B) / 255f;
		var expected = Blend(Blend(original, luminosity, false), tint, true);
		foreach (var layer in new[] { 0, 1, 2 })
		{
			foreach (var opacity in new[] { 0.25f, 0.75f, 1f })
			{
				using var texture = factory.RenderOffscreen(64, 64, drawing =>
				{
					if (layer == 1)
					{
						drawing.SaveLayer(identity);
					}
					if (layer == 2)
					{
						drawing.SaveLayer(filter);
					}
					drawing.DrawRect(new Rect(0, 0, 64, 64), background);
					if (layer != 2)
					{
						drawing.DrawEffectBackdrop(filter, opacity);
					}
					if (layer != 0)
					{
						drawing.Restore();
					}
				});
				using var image = await factory.SnapshotAsync(texture);
				var pixels = new byte[64 * 64 * 4];
				image.CopyPixels(pixels);
				var mixed = Vector3.Lerp(original, expected, layer == 2 ? 1 : opacity) * 255;
				var offset = (32 * 64 + 32) * 4;
				if (Math.Abs(pixels[offset] - mixed.Z) > 3 || Math.Abs(pixels[offset + 1] - mixed.Y) > 3 || Math.Abs(pixels[offset + 2] - mixed.X) > 3 || pixels[offset + 3] != 255)
				{
					throw new InvalidOperationException($"Acrylic blend layer={layer} opacity={opacity}: BGRA={pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]}, expected RGB={mixed}.");
				}
				if (layer != 2 && (pixels[0] != background.B || pixels[1] != background.G || pixels[2] != background.R))
				{
					throw new InvalidOperationException("Acrylic must leave pixels outside its bounds unchanged.");
				}
				if (layer == 2 && pixels[3] != 0)
				{
					throw new InvalidOperationException("Acrylic layers must crop their output to the effect bounds.");
				}
			}
		}

		static Vector3 Blend(Vector3 destination, Color source, bool colorMode)
		{
			var color = new Vector3(source.R, source.G, source.B) / 255f;
			var weights = new Vector3(0.3f, 0.59f, 0.11f);
			var lightness = Vector3.Dot(colorMode ? destination : color, weights);
			var mixed = colorMode ? color : destination;
			mixed += new Vector3(lightness - Vector3.Dot(mixed, weights));
			var minimum = Math.Min(mixed.X, Math.Min(mixed.Y, mixed.Z));
			var maximum = Math.Max(mixed.X, Math.Max(mixed.Y, mixed.Z));
			if (minimum < 0)
			{
				mixed = new Vector3(lightness) + (mixed - new Vector3(lightness)) * lightness / (lightness - minimum);
			}
			if (maximum > 1)
			{
				mixed = new Vector3(lightness) + (mixed - new Vector3(lightness)) * (1 - lightness) / (maximum - lightness);
			}
			return Vector3.Lerp(destination, mixed, source.A / 255f);
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
