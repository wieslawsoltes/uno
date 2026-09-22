extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using Uno.UI.Hosting;
using Color = unouwp::Windows.UI.Color;
using FontStretch = unouwp::Windows.UI.Text.FontStretch;
using FontStyle = unouwp::Windows.UI.Text.FontStyle;
using FontWeight = unouwp::Windows.UI.Text.FontWeight;
using Rect = unofoundation::Windows.Foundation.Rect;

internal static class ContentContractTests
{
	public static async Task Run(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometryFactory)
	{
		RunHostRegistration();
		var font = CreateFont(CreateFixtureFont(bitmap: false), 20);
		RunFontMetrics(font);
		RunShapeCache(font);
		RunBitmapCache(geometryFactory);
		await RunGeometryLifetime(factory, geometryFactory, font);
		await RunLottie(factory, geometryFactory);
		Console.WriteLine("ProGPU content contracts passed: font metrics, shaping cache, bitmap cache, geometry lifetime, and managed Lottie GPU frames.");
	}

	private static void RunHostRegistration()
	{
		var builder = UnoPlatformHostBuilder.Create();
		builder.LottieRenderer(new UnusableLottieRenderer());
		builder.UseProGpuDrawingBackend();
		// Execute the composition-root registrations without selecting a window or creating another GPU device.
		var registrations = typeof(UnoPlatformHostBuilder).GetField("_drawingRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)
			?.GetValue(builder) as IEnumerable<Action>
			?? throw new InvalidOperationException("The host's drawing registrations could not be inspected by the contract test.");
		foreach (var register in registrations)
		{
			register();
		}
		Require(ImageEncoderDecoder.Current is ProGpuImageEncoderDecoder, "UseProGpuDrawingBackend must register the ProGPU image codec.");
		Require(SvgRenderer.Current is ProGpuSvgRenderer, "UseProGpuDrawingBackend must register the ProGPU SVG renderer.");
		Require(LottieRenderer.Current is ManagedLottieRenderer, "UseProGpuDrawingBackend must replace the previous Lottie choice with the neutral managed renderer.");
	}

	private sealed class UnusableLottieRenderer : ILottieRenderer
	{
		public ILottieAnimation? Load(string animationJson, IGeometryFactory geometry) => null;
	}

	private static IFont CreateFont(byte[] bytes, float size) =>
		new ProGpuFontProvider().CreateFont(bytes, null, new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, size)
		?? throw new InvalidOperationException("The deterministic SFNT content fixture could not be loaded.");

	private static void RunFontMetrics(IFont font)
	{
		var large = CreateFont(CreateFixtureFont(bitmap: false), 40);
		Require(float.IsFinite(font.CapHeight) && font.CapHeight > 0, "Cap height must be finite and positive.");
		Require(MathF.Abs(font.CapHeight - 14) < 0.001f, $"OS/2 cap height should be 14px at 20px/em, got {font.CapHeight}.");
		Require(MathF.Abs(large.CapHeight - 2 * font.CapHeight) < 0.001f, "Cap height must scale with the font size.");
		Require(MathF.Abs(font.CapHeight + font.Ascent) > 0.1f, "Cap height must not be substituted with ascent.");
		var fallback = CreateFont(CreateFixtureFont(bitmap: false, capHeight: false), 20);
		Require(MathF.Abs(fallback.CapHeight - 14) < 0.001f, "A font lacking OS/2 cap height and an H outline must use the 0.7em fallback.");
		var hFallback = CreateFont(CreateFixtureFont(bitmap: false, capHeight: false, mappedCharacter: 'H', outlineHeight: 550), 20);
		Require(MathF.Abs(hFallback.CapHeight - 11) < 0.001f, $"Without an OS/2 cap height, H outline height must win over ascent and the 0.7em default; got {hFallback.CapHeight}.");
	}

	private static void RunShapeCache(IFont font)
	{
		var runs = new List<GlyphRun>();
		foreach (var direction in new[] { TextDirection.LeftToRight, TextDirection.RightToLeft })
		{
			foreach (var ligatures in new[] { false, true })
			{
				var first = font.Shape("AA", direction, ligatures);
				var second = font.Shape("AA", direction, ligatures);
				Require(first.Count == 2 && first.Glyphs[0] == 1 && first.Glyphs[1] == 1, "The deterministic font must shape AA into two mapped glyphs.");
				Require(SharesArrays(first, second), $"A repeated short run must reuse all shaped arrays ({direction}, ligatures={ligatures}).");
				foreach (var previous in runs)
				{
					Require(!SharesArrays(previous, first), "Direction and ligature settings must have separate shaping cache entries.");
				}
				runs.Add(first);
			}
		}

		var longText = new string('A', 33);
		Require(!SharesArrays(font.Shape(longText, TextDirection.LeftToRight), font.Shape(longText, TextDirection.LeftToRight)),
			"Long runs must stay outside the bounded short-run shaping cache.");
	}

	private static bool SharesArrays(GlyphRun first, GlyphRun second) =>
		ReferenceEquals(first.Glyphs, second.Glyphs) && ReferenceEquals(first.Offsets, second.Offsets) &&
		ReferenceEquals(first.Advances, second.Advances) && ReferenceEquals(first.Clusters, second.Clusters);

	private static void RunBitmapCache(ProGpuGeometryFactory geometry)
	{
		var font = CreateFont(CreateFixtureFont(bitmap: true), 20);
		var first = new List<GlyphRunElement>();
		var second = new List<GlyphRunElement>();
		font.BuildGlyphRun(geometry, [1], [new Vector2(10, 20)], 3, first);
		font.BuildGlyphRun(geometry, [1], [new Vector2(27, 31)], 8, second);
		Require(first.Count == 1 && first[0] is GlyphImage, "The synthetic sbix glyph must produce one bitmap element.");
		Require(second.Count == 1 && second[0] is GlyphImage, "The repeated sbix glyph must produce one bitmap element.");
		var a = (GlyphImage)first[0];
		var b = (GlyphImage)second[0];
		Require(a.PixelWidth == 1 && a.PixelHeight == 1 && a.Pixels.Length == 4 && a.Pixels[3] != 0, "The sbix fixture must decode its nontransparent 1x1 PNG.");
		Require(ReferenceEquals(a.Pixels, b.Pixels), "Repeated bitmap glyphs must retain pixel-buffer identity for texture cache reuse.");
		Require(MathF.Abs(a.X - 12) < 0.001f && MathF.Abs(a.Y - 28) < 0.001f, "The sbix bearing and baseline must be applied to bitmap placement.");
		Require(MathF.Abs(b.X - a.X - 17) < 0.001f && MathF.Abs(b.Y - a.Y - 16) < 0.001f, "Cached pixels must not cache the first occurrence's position or baseline.");
	}

	private static async Task RunGeometryLifetime(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometry, IFont font)
	{
		var builder = geometry.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(8, 8, 16, 16));
		var source = builder.Build();
		var stroke = source.GetStrokeFillGeometry(new StrokeStyle { Thickness = 4, LineJoin = StrokeJoin.Round, MiterLimit = 4 });
		var elements = new List<GlyphRunElement>();
		font.BuildGlyphRun(geometry, [1], [new Vector2(36, 44)], 0, elements);
		Require(elements.Count == 1 && elements[0] is GlyphOutline, "The outline fixture must preserve the merged native glyph-run route.");
		var glyph = ((GlyphOutline)elements[0]).Outline;
		_ = stroke.Bounds;
		_ = glyph.Bounds;
		foreach (var resource in new[] { source, stroke, glyph })
		{
			Require(resource is DrawingResource, "All ProGPU geometry variants must participate in drawing-resource lifetime.");
			resource.AddRef();
			resource.Dispose();
			resource.Dispose();
		}

		IRenderRecord record;
		try
		{
			Require(source.FillContains(new Vector2(16, 16)), "A retained source geometry must remain usable after creator disposal.");
			Require(stroke.Bounds.Width > source.Bounds.Width && !glyph.IsEmpty, "Retained stroke and glyph geometry must remain usable after creator disposal.");
			var recorder = factory.CreateRecording();
			recorder.DrawPath(source, Color.FromArgb(255, 255, 0, 0));
			recorder.DrawPath(stroke, Color.FromArgb(255, 0, 255, 0));
			recorder.DrawPath(glyph, Color.FromArgb(255, 0, 0, 255));
			record = recorder.Finish();
		}
		finally
		{
			foreach (var resource in new[] { source, stroke, glyph })
			{
				resource.Release();
			}
		}

		using (record)
		using (var texture = factory.RenderOffscreen(64, 64, record.Replay))
		using (var image = await factory.SnapshotAsync(texture))
		{
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			var center = (16 * 64 + 16) * 4;
			Require(pixels[center + 2] > 200 && pixels[center + 3] > 240, "Recording replay must preserve the filled geometry after every caller reference is released.");
			Require(CountDominant(pixels, 1) > 40, "Recording replay lost the retained green stroke geometry.");
			Require(CountDominant(pixels, 0) > 30, "Recording replay lost the retained blue native glyph run.");
		}
	}

	private static int CountDominant(byte[] pixels, int channel)
	{
		var count = 0;
		for (var i = 0; i < pixels.Length; i += 4)
		{
			if (pixels[i + channel] > 180 && pixels[i + (channel + 1) % 3] < 80 && pixels[i + (channel + 2) % 3] < 80)
			{
				count++;
			}
		}
		return count;
	}

	private static async Task RunLottie(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometry)
	{
		const string json = """
			{"v":"5.7.0","w":64,"h":64,"fr":60,"ip":0,"op":60,"layers":[
			{"ty":4,"ind":1,"ip":0,"op":60,"ks":{"p":{"a":1,"k":[{"t":0,"s":[0,0],"e":[32,0]},{"t":60,"s":[32,0]}]}},
			"shapes":[{"ty":"rc","p":{"k":[12,32]},"s":{"k":[16,16]},"r":{"k":0}},
			{"ty":"fl","c":{"k":[1,0,0,1]},"o":{"k":100}}]}]}
			""";
		var renderer = LottieRenderer.Current
			?? throw new InvalidOperationException("The ProGPU host registration did not install a Lottie renderer.");
		using var animation = renderer.Load(json, geometry)
			?? throw new InvalidOperationException("Managed Lottie could not load the deterministic animated rectangle.");
		Require(animation.Size == new Vector2(64, 64) && animation.Duration == TimeSpan.FromSeconds(1), "Managed Lottie must retain the animation dimensions and duration.");
		var first = await RenderFrame(0.1f);
		var second = await RenderFrame(0.8f);
		Require(CountDominant(first, 2) > 180 && CountDominant(second, 2) > 180, "Both managed Lottie frames must contain a visible red rectangle on the GPU.");
		var changed = 0;
		for (var i = 0; i < first.Length; i += 4)
		{
			if (Math.Abs(first[i + 3] - second[i + 3]) > 128)
			{
				changed++;
			}
		}
		Require(changed > 300, $"Managed Lottie position animation must change GPU coverage; only {changed} pixels changed.");

		async Task<byte[]> RenderFrame(float progress)
		{
			using var texture = factory.RenderOffscreen(64, 64, session => animation.Render(session, progress, new Rect(0, 0, 64, 64)));
			using var image = await factory.SnapshotAsync(texture);
			var pixels = new byte[64 * 64 * 4];
			image.CopyPixels(pixels);
			return pixels;
		}
	}

	private static byte[] CreateFixtureFont(bool bitmap, bool capHeight = true, char mappedCharacter = 'A', ushort outlineHeight = 700)
	{
		var head = new byte[54];
		U16(head, 18, 1000);
		var hhea = new byte[36];
		U16(hhea, 4, 800);
		U16(hhea, 6, unchecked((ushort)-200));
		U16(hhea, 34, 3);
		var maxp = new byte[6];
		U32(maxp, 0, 0x00010000);
		U16(maxp, 4, 3);
		var hmtx = new byte[12];
		U16(hmtx, 0, 650);
		U16(hmtx, 4, 650);
		U16(hmtx, 8, 650);
		var os2 = new byte[96];
		U16(os2, 0, 2);
		U16(os2, 4, 400);
		U16(os2, 6, 5);
		U16(os2, 88, capHeight ? (ushort)700 : (ushort)0);
		var cmap = new byte[40];
		U16(cmap, 2, 1);
		U16(cmap, 4, 3);
		U16(cmap, 6, 10);
		U32(cmap, 8, 12);
		U16(cmap, 12, 12);
		U32(cmap, 16, 28);
		U32(cmap, 24, 1);
		U32(cmap, 28, mappedCharacter);
		U32(cmap, 32, mappedCharacter);
		U32(cmap, 36, 1);
		var glyf = new byte[34];
		U16(glyf, 0, 1);
		U16(glyf, 6, 600);
		U16(glyf, 8, outlineHeight);
		U16(glyf, 10, 3);
		glyf.AsSpan(14, 4).Fill(1);
		U16(glyf, 20, 600);
		U16(glyf, 24, unchecked((ushort)-600));
		U16(glyf, 30, outlineHeight);
		var loca = new byte[8];
		U16(loca, 4, 17);
		U16(loca, 6, 17);
		var tables = new List<(string Tag, byte[] Data)>
		{
			("head", head), ("hhea", hhea), ("maxp", maxp), ("hmtx", hmtx),
			("OS/2", os2), ("cmap", cmap), ("glyf", glyf), ("loca", loca),
		};
		if (bitmap)
		{
			var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z3GQAAAAASUVORK5CYII=");
			var sbix = new byte[40 + png.Length];
			U16(sbix, 0, 1);
			U16(sbix, 2, 1);
			U32(sbix, 4, 1);
			U32(sbix, 8, 12);
			U16(sbix, 12, 20);
			U16(sbix, 14, 72);
			U32(sbix, 16, 20);
			U32(sbix, 20, 20);
			U32(sbix, 24, (uint)(28 + png.Length));
			U32(sbix, 28, (uint)(28 + png.Length));
			U16(sbix, 32, unchecked((ushort)-2));
			U16(sbix, 34, 6);
			Encoding.ASCII.GetBytes("png ").CopyTo(sbix, 36);
			png.CopyTo(sbix, 40);
			tables.Add(("sbix", sbix));
		}

		var length = 12 + 16 * tables.Count;
		foreach (var table in tables)
		{
			length += (table.Data.Length + 3) & ~3;
		}
		var bytes = new byte[length];
		U32(bytes, 0, 0x00010000);
		U16(bytes, 4, (ushort)tables.Count);
		var offset = 12 + 16 * tables.Count;
		for (var i = 0; i < tables.Count; i++)
		{
			var table = tables[i];
			var record = 12 + 16 * i;
			Encoding.ASCII.GetBytes(table.Tag).CopyTo(bytes, record);
			U32(bytes, record + 8, (uint)offset);
			U32(bytes, record + 12, (uint)table.Data.Length);
			table.Data.CopyTo(bytes, offset);
			offset += (table.Data.Length + 3) & ~3;
		}
		return bytes;
	}

	private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
	private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
