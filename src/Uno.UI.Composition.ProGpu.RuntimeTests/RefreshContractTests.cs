extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using Color = unouwp::Windows.UI.Color;
using Rect = unofoundation::Windows.Foundation.Rect;
using FontWeight = unouwp::Windows.UI.Text.FontWeight;
using FontStretch = unouwp::Windows.UI.Text.FontStretch;
using FontStyle = unouwp::Windows.UI.Text.FontStyle;

internal static class RefreshContractTests
{
	private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

	public static async Task Run(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var failures = new List<Exception>();
		foreach (var test in new Func<Task>[]
		{
			() => EllipticalCorners(factory),
			() => SquareBorder(factory),
			() => RetainedShadow(factory),
			() => StaticBlend(factory),
			() => TextureInputScale(factory),
			() => ShadowScale(factory, geometries),
			() => RotatedShadow(factory, geometries),
			() => LargeCorner(factory),
			() => DashUnits(factory, geometries),
			() => ScaledCurves(geometries),
			InferredTextDirection,
			FontFeatures,
			() => ObliqueShadow(factory, geometries),
		})
		{
			try
			{
				await test();
			}
			catch (Exception error)
			{
				failures.Add(error);
				Console.WriteLine(error.Message);
			}
		}
		if (failures.Count != 0)
		{
			throw new AggregateException("Updated drawing contract regressions", failures);
		}
		Console.WriteLine("Refresh contracts passed: elliptical fills/borders/clips, texture scale, shadow scale, inferred text direction.");
	}

	private static async Task EllipticalCorners(ProGpuDrawingFactory factory)
	{
		var outer = new RoundRectangle
		{
			Rect = new Rect(4, 4, 48, 40),
			TopLeft = new Vector2(20, 5), TopRight = new Vector2(12, 4),
			BottomRight = new Vector2(10, 3), BottomLeft = new Vector2(16, 6),
		};
		foreach (var mode in new[] { "fill", "border", "clip" })
		{
			var pixels = await Render(factory, drawing =>
			{
				if (mode == "fill")
				{
					drawing.DrawRoundedRect(outer, White);
				}
				else if (mode == "border")
				{
					drawing.DrawRoundedRectBorder(outer, new RoundRectangle { Rect = new Rect(10, 10, 36, 28) }, White);
				}
				else
				{
					drawing.ClipRoundRect(outer);
					drawing.DrawRect(new Rect(0, 0, 64, 64), White);
				}
			});
			AssertAlpha(pixels, 5, 12, 255, $"elliptical {mode} side");
			AssertAlpha(pixels, 4, 4, 0, $"elliptical {mode} corner");
			AssertAlpha(pixels, 24, 24, mode == "border" ? 0 : 255, $"elliptical {mode} center");
		}
	}

	private static async Task SquareBorder(ProGpuDrawingFactory factory)
	{
		var pixels = await Render(factory, drawing => drawing.DrawRoundedRectBorder(
			new RoundRectangle { Rect = new Rect(4, 4, 48, 40) },
			new RoundRectangle { Rect = new Rect(8, 8, 40, 32) }, White));
		AssertAlpha(pixels, 5, 5, 255, "square border corner");
		AssertAlpha(pixels, 24, 6, 255, "square border edge");
		AssertAlpha(pixels, 24, 24, 0, "square border center");
		AssertAlpha(pixels, 2, 24, 0, "square border exterior");
	}

	private static async Task RetainedShadow(ProGpuDrawingFactory factory)
	{
		var recorder = factory.CreateRecording();
		recorder.DrawRect(new Rect(8, 8, 16, 16), White);
		using var record = recorder.Finish();
		using var shadow = factory.CreateDropShadowFilter(0, 12, 2, 2, White);
		var actual = await Render(factory, session =>
		{
			session.Translate(12, 0);
			session.SaveLayer(shadow);
			record.Replay(session);
			session.Restore();
		});
		var expected = await Render(factory, session =>
		{
			session.Translate(12, 0);
			session.SaveLayer(shadow);
			session.DrawRect(new Rect(8, 8, 16, 16), White);
			session.Restore();
		});
		if (actual[(32 * 64 + 28) * 4 + 3] < 100 || !actual.AsSpan().SequenceEqual(expected))
		{
			throw new InvalidOperationException("Retained replay must contribute transformed bounds to its shadow layer.");
		}
	}

	private static async Task StaticBlend(ProGpuDrawingFactory factory)
	{
		var cyan = Color.FromArgb(255, 0, 255, 255);
		var yellow = Color.FromArgb(255, 255, 255, 0);
		using var texture = factory.CreateTexture(1, 1, new byte[] { 0, 255, 255, 255 });
		foreach (var foreground in new EffectNode[] { new ColorInput(yellow), new TextureInput(texture, EdgeExtend.Clamp, EdgeExtend.Clamp) })
		{
			using var filter = factory.CreateEffectFilter(new BlendEffectNode(new ColorInput(cyan), foreground, BlendMode.Multiply), new Rect(4, 4, 40, 40))
				?? throw new InvalidOperationException("Static blend inputs must compile to an effect.");
			var pixels = await Render(factory, session => session.DrawEffectBackdrop(filter, 0.5f));
			var pixel = pixels.AsSpan((24 * 64 + 24) * 4, 4);
			if (pixel[0] > 2 || pixel[1] < 125 || pixel[1] > 130 || pixel[2] > 2 || pixel[3] < 125 || pixel[3] > 130)
			{
				throw new InvalidOperationException($"Static cyan/yellow multiply at half opacity must be premultiplied green: {Convert.ToHexString(pixel)}.");
			}
			AssertAlpha(pixels, 2, 2, 0, "static blend bounds");
		}
	}

	private static async Task TextureInputScale(ProGpuDrawingFactory factory)
	{
		var bytes = new byte[16 * 24 * 4];
		Array.Fill(bytes, (byte)255);
		using var texture = factory.CreateTexture(16, 24, bytes);
		using var filter = factory.CreateEffectFilter(new BlendEffectNode(
			new TextureInput(texture, ScaleX: 2, ScaleY: 3), new SourceInput(), BlendMode.SrcOver), new Rect(4, 4, 40, 40))!;
		var pixels = await Render(factory, drawing => { drawing.SaveLayer(filter); drawing.Restore(); });
		AssertAlpha(pixels, 7, 7, 255, "scaled texture interior");
		AssertAlpha(pixels, 14, 7, 0, "scaled texture horizontal extent");
		AssertAlpha(pixels, 7, 14, 0, "scaled texture vertical extent");
	}

	private static async Task ShadowScale(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(10, 8, 8, 6));
		using var source = builder.Build();
		builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(20, 24, 16, 18));
		using var scaled = builder.Build();
		var actual = await Render(factory, drawing => { drawing.Scale(2, 3); drawing.DrawShadow(source, White, 2, 1, false); });
		var expected = await Render(factory, drawing => drawing.DrawShadow(scaled, White, 4, 3, false));
		for (var i = 0; i < actual.Length; i++)
		{
			if (Math.Abs(actual[i] - expected[i]) > 3)
			{
				throw new InvalidOperationException($"Shadow sigma must follow session scale: byte {i} = {actual[i]}, expected {expected[i]}.");
			}
		}
	}

	private static async Task RotatedShadow(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(20, 20, 12, 8));
		using var source = builder.Build();
		var matrix = new Matrix3x2(0, 1, -1, 0, 64, 0);
		using var rotated = source.Transform(matrix);
		var actual = await Render(factory, drawing =>
		{
			drawing.SetMatrix(new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 64, 0, 0, 1));
			drawing.DrawShadow(source, White, 6, 1, false);
		});
		var expected = await Render(factory, drawing => drawing.DrawShadow(rotated, White, 1, 6, false));
		for (var i = 0; i < actual.Length; i++)
		{
			if (Math.Abs(actual[i] - expected[i]) > 4)
			{
				throw new InvalidOperationException($"Rotated shadow differs at byte {i}: {actual[i]}, expected {expected[i]}.");
			}
		}
	}

	private static async Task LargeCorner(ProGpuDrawingFactory factory)
	{
		var shape = new RoundRectangle { Rect = new Rect(4, 4, 56, 56), TopLeft = new Vector2(50, 50) };
		foreach (var clip in new[] { false, true })
		{
			var pixels = await Render(factory, drawing =>
			{
				if (clip)
				{
					drawing.ClipRoundRect(shape);
					drawing.DrawRect(shape.Rect, White);
				}
				else
				{
					drawing.DrawRoundedRect(shape, White);
				}
			});
			AssertAlpha(pixels, 10, 22, 0, "large unequal corner exterior");
			AssertAlpha(pixels, 54, 10, 255, "large unequal corner interior");
		}
	}

	private static async Task DashUnits(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePathBuilder();
		builder.MoveTo(new Vector2(4, 24));
		builder.LineTo(new Vector2(60, 24));
		using var line = builder.Build();
		foreach (var trimmed in new[] { false, true })
		{
			using var stroke = line.GetStrokeFillGeometry(new StrokeStyle
			{
				Thickness = 4, DashArray = [8, 8], MiterLimit = 10,
				TrimEnd = trimmed ? 1 : 0,
			});
			var pixels = await Render(factory, drawing => drawing.DrawPath(stroke, White));
			AssertAlpha(pixels, 7, 24, 255, "dash in geometry units");
			AssertAlpha(pixels, 16, 24, 0, "dash gap in geometry units");
			AssertAlpha(pixels, 23, 24, 255, "second dash in geometry units");
		}
	}

	private static Task ScaledCurves(ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePathBuilder();
		builder.MoveTo(Vector2.Zero);
		builder.QuadraticTo(new Vector2(0, 1), Vector2.One);
		using var curve = builder.Build();
		var sink = new CurveSink();
		curve.StreamFlattened(sink, 1000);
		if (sink.Points.Count < 3 || sink.Points[0] != Vector2.Zero || sink.Points[^1] != Vector2.One)
		{
			throw new InvalidOperationException("Flattened curve must retain both endpoints and interior coverage.");
		}
		for (var i = 1; i < sink.Points.Count; i++)
		{
			var a = sink.Points[i - 1];
			var b = sink.Points[i];
			var t = (MathF.Sqrt(a.X) + MathF.Sqrt(b.X)) * 0.5f;
			var exact = new Vector2(t * t, 2 * t - t * t);
			var chord = (a + b) * 0.5f;
			if (Vector2.Distance(exact, chord) * 1000 > 0.15f)
			{
				throw new InvalidOperationException("Magnified curve flattening exceeds the device-pixel error bound.");
			}
		}
		return Task.CompletedTask;
	}

	private sealed class CurveSink : IFlattenedPathSink
	{
		internal List<Vector2> Points { get; } = new();
		public void BeginContour(Vector2 start) => Points.Add(start);
		public void LineTo(Vector2 point) => Points.Add(point);
		public void EndContour(bool closed) { }
	}

	private static Task InferredTextDirection()
	{
		var font = new ProGpuFontProvider().GetDefaultFont(new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, 18);
		foreach (var (text, expected) in new[] { ("Hello", TextDirection.LeftToRight), ("שלום", TextDirection.RightToLeft), ("مرحبا", TextDirection.RightToLeft) })
		{
			var inferred = font.Shape(text, out var direction);
			var explicitRun = font.Shape(text, expected);
			if (direction != expected || !inferred.Glyphs.AsSpan().SequenceEqual(explicitRun.Glyphs))
			{
				throw new InvalidOperationException($"Inferred shaping direction for '{text}' was {direction}, expected {expected}.");
			}
		}
		return Task.CompletedTask;
	}

	private static Task FontFeatures()
	{
		using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Inter-Regular.ttf")!;
		using var bytes = new MemoryStream();
		resource.CopyTo(bytes);
		var font = new ProGpuFontProvider().CreateFont(bytes.ToArray(), "Inter", new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, 32)!;
		const string localizedText = "ş ţ Ş Ţ";
		var romanian = font.Shape(localizedText, TextDirection.LeftToRight, new ShapingOptions { Language = "ro", Script = "Latn" });
		var regionalRomanian = font.Shape(localizedText, TextDirection.LeftToRight, new ShapingOptions { Language = "ro-RO", Script = "Latn" });
		var defaultLatin = font.Shape(localizedText, TextDirection.LeftToRight, new ShapingOptions { Language = "en", Script = "Latn" });
		if (romanian.Glyphs.AsSpan().SequenceEqual(defaultLatin.Glyphs) || !romanian.Glyphs.AsSpan().SequenceEqual(regionalRomanian.Glyphs))
		{
			throw new InvalidOperationException("Regional Romanian language tags must preserve localized comma-below substitutions.");
		}
		var kerned = font.Shape("AV", TextDirection.LeftToRight, new ShapingOptions { Language = "en", Script = "Latn" });
		var unkerned = font.Shape("AV", TextDirection.LeftToRight, new ShapingOptions { DisableKerning = true });
		if (kerned.Advances.Sum() >= unkerned.Advances.Sum() - 0.1f)
		{
			throw new InvalidOperationException("Explicit shaping options must preserve observable Inter AV kerning.");
		}
		if (!font.TryGetTable(0x68656164, out var first) || !font.TryGetTable(0x68656164, out var second) ||
			first.Length < 12 || !first.AsSpan().SequenceEqual(second) || ReferenceEquals(first, second) || font.TryGetTable(0x42414421, out _))
		{
			throw new InvalidOperationException("Font table reads must return independent copies and report missing tables.");
		}
		first[0] ^= 255;
		if (!font.TryGetTable(0x68656164, out var third) || !second.AsSpan().SequenceEqual(third))
		{
			throw new InvalidOperationException("Changing a returned table must not mutate the font.");
		}
		return Task.CompletedTask;
	}

	private static async Task ObliqueShadow(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(24, 28, 16, 8));
		using var shape = builder.Build();
		var reference = await Render(factory, drawing => drawing.DrawShadow(shape, White, 6, 1, false));
		var rotation = Matrix4x4.CreateTranslation(-32, -32, 0) * Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateTranslation(32, 32, 0);
		var actual = await Render(factory, drawing => { drawing.SetMatrix(rotation); drawing.DrawShadow(shape, White, 6, 1, false); });
		using var identity = factory.CreateColorMatrixColorFilter([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
		var nested = await Render(factory, drawing => { drawing.SaveLayer(identity); drawing.SetMatrix(rotation); drawing.DrawShadow(shape, White, 6, 1, false); drawing.Restore(); });
		Matrix4x4.Invert(rotation, out var inverse);
		var totalError = 0f;
		var nestedError = 0f;
		var maxNestedError = 0;
		for (var y = 0; y < 64; y++)
		{
			for (var x = 0; x < 64; x++)
			{
				var p = Vector3.Transform(new Vector3(x + 0.5f, y + 0.5f, 0), inverse);
				var px = p.X - 0.5f;
				var py = p.Y - 0.5f;
				var ix = (int)MathF.Floor(px);
				var iy = (int)MathF.Floor(py);
				float Alpha(int sx, int sy) => sx < 0 || sy < 0 || sx >= 64 || sy >= 64 ? 0 : reference[(sy * 64 + sx) * 4 + 3];
				var top = float.Lerp(Alpha(ix, iy), Alpha(ix + 1, iy), px - ix);
				var bottom = float.Lerp(Alpha(ix, iy + 1), Alpha(ix + 1, iy + 1), px - ix);
				var expected = float.Lerp(top, bottom, py - iy);
				var index = (y * 64 + x) * 4 + 3;
				totalError += MathF.Abs(actual[index] - expected);
				nestedError += Math.Abs(nested[index] - actual[index]);
				maxNestedError = Math.Max(maxNestedError, Math.Abs(nested[index] - actual[index]));
			}
		}
		Console.WriteLine($"Oblique errors: rotated={totalError / 4096}, nested={nestedError / 4096}, maxNested={maxNestedError}");
		if (maxNestedError > 4)
		{
			throw new InvalidOperationException("Nested oblique shadow mismatch.");
		}
		if (totalError / 4096 > 2)
		{
			throw new InvalidOperationException($"Oblique shadow differs from rotated reference: mean alpha error {totalError / 4096}.");
		}
	}

	private static async Task<byte[]> Render(ProGpuDrawingFactory factory, Action<IDrawingSession> draw)
	{
		using var target = factory.RenderOffscreen(64, 64, drawing => { drawing.Clear(Color.FromArgb(0, 0, 0, 0)); draw(drawing); });
		using var image = await factory.SnapshotAsync(target);
		var pixels = new byte[64 * 64 * 4];
		image.CopyPixels(pixels);
		return pixels;
	}

	private static void AssertAlpha(byte[] pixels, int x, int y, int expected, string scenario)
	{
		var actual = pixels[(y * 64 + x) * 4 + 3];
		if (Math.Abs(actual - expected) > 4)
		{
			throw new InvalidOperationException($"{scenario}: alpha {actual}, expected {expected}.");
		}
	}
}
