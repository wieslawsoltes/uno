extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using StbImageSharp;
using Uno.UI.Composition.Drawing;
using Color = unouwp::Windows.UI.Color;
using FontStretch = unouwp::Windows.UI.Text.FontStretch;
using FontStyle = unouwp::Windows.UI.Text.FontStyle;
using FontWeight = unouwp::Windows.UI.Text.FontWeight;
using PGeometry = ProGPU.Vector.PathGeometry;
using Rect = unofoundation::Windows.Foundation.Rect;

namespace Uno.UI.Composition.ProGpu;

public sealed class ProGpuFontProvider : IFontProvider
{
	private static readonly string[] DefaultFamilies =
	[
		"Segoe UI", ".SF NS Text", "SF Pro Text", "Helvetica Neue", "Arial",
		"Roboto", "Noto Sans", "DejaVu Sans", "Liberation Sans",
	];

	private readonly FontManager _manager;
	private readonly ConcurrentDictionary<(TtfFont Font, int SizeBits), ProGpuFont> _fonts = new();

	public ProGpuFontProvider(FontManager? manager = null) => _manager = manager ?? FontManager.Default;

	public IFont? CreateFont(byte[] data, string? familyNameHint, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
	{
		ArgumentNullException.ThrowIfNull(data);
		try
		{
			TtfFont? selected = null;
			for (var face = 0; face < 64; face++)
			{
				try
				{
					var candidate = new TtfFont(data, face);
					selected ??= candidate;
					if (!string.IsNullOrWhiteSpace(familyNameHint) &&
						string.Equals(candidate.FamilyName, familyNameHint, StringComparison.OrdinalIgnoreCase))
					{
						selected = candidate;
						break;
					}
				}
				catch when (face > 0)
				{
					break;
				}
			}

			if (selected is null)
			{
				return null;
			}
			selected = _manager.MatchTypeface(selected, ToStyle(weight, stretch, style, fontSize));
			return Wrap(selected, fontSize);
		}
		catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException or OverflowException)
		{
			return null;
		}
	}

	public IFont? MatchFamily(string familyName, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
	{
		var font = _manager.MatchFamily(familyName, ToStyle(weight, stretch, style, fontSize));
		return font is null ? null : Wrap(font, fontSize);
	}

	public ValueTask<IFont?> MatchCharacterAsync(int codepoint, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
	{
		var font = _manager.MatchCharacter(null, ToStyle(weight, stretch, style, fontSize), null, codepoint);
		return new ValueTask<IFont?>(font is null ? null : Wrap(font, fontSize));
	}

	public IFont GetDefaultFont(FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
	{
		foreach (var family in DefaultFamilies)
		{
			if (MatchFamily(family, weight, stretch, style, fontSize) is { } font)
			{
				return font;
			}
		}

		foreach (var family in _manager.FontFamilies)
		{
			if (MatchFamily(family, weight, stretch, style, fontSize) is { } font)
			{
				return font;
			}
		}

		throw new InvalidOperationException("ProGPU could not resolve a usable default system font. Register an embedded fallback font before building the host.");
	}

	private ProGpuFont Wrap(TtfFont font, float size) =>
		_fonts.GetOrAdd((font, BitConverter.SingleToInt32Bits(size)), static key => new ProGpuFont(key.Font, BitConverter.Int32BitsToSingle(key.SizeBits)));

	private static FontStyleRequest ToStyle(FontWeight weight, FontStretch stretch, FontStyle style, float fontSize) => new(
		Math.Clamp((int)weight.Weight, 1, 1000),
		stretch switch
		{
			FontStretch.UltraCondensed => 1,
			FontStretch.ExtraCondensed => 2,
			FontStretch.Condensed => 3,
			FontStretch.SemiCondensed => 4,
			FontStretch.SemiExpanded => 6,
			FontStretch.Expanded => 7,
			FontStretch.ExtraExpanded => 8,
			FontStretch.UltraExpanded => 9,
			_ => 5,
		},
		style switch
		{
			FontStyle.Italic => FontSlant.Italic,
			FontStyle.Oblique => FontSlant.Oblique,
			_ => FontSlant.Upright,
		})
	{
		OpticalSize = fontSize * 72f / 96f,
	};
}

public sealed class ProGpuFont : IFont
{
	private readonly float _scale;

	internal ProGpuFont(TtfFont font, float size)
	{
		Font = font;
		Size = float.IsFinite(size) && size > 0 ? size : 12f;
		_scale = Font.UnitsPerEm == 0 ? 1 : Size / Font.UnitsPerEm;
	}

	internal TtfFont Font { get; }
	internal float Size { get; }

	public float Ascent => -Font.Ascender * _scale;
	public float Descent => -Font.Descender * _scale;
	public float? UnderlinePosition => Font.UnderlinePosition is { } value ? -value * _scale : null;
	public float? UnderlineThickness => Font.UnderlineThickness is { } value ? Math.Abs(value * _scale) : null;
	public float? StrikeoutPosition => Font.StrikeoutPosition is { } value ? -value * _scale : null;
	public float? StrikeoutThickness => Font.StrikeoutThickness is { } value ? Math.Abs(value * _scale) : null;
	public string FamilyName => Font.FamilyName;

	public GlyphRun Shape(ReadOnlySpan<char> text, TextDirection direction, bool enableLigatures = true)
	{
		var shapingDirection = direction == TextDirection.RightToLeft ? ShapingDirection.RightToLeft : ShapingDirection.LeftToRight;
		var options = (enableLigatures
			? TextShapingOptions.Default
			: TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting("liga", 0)))
			.WithDirection(shapingDirection);
		var shaped = OpenTypeTextShaper.Shape(text.ToString(), Font, Size, options);
		var glyphs = new ushort[shaped.Count];
		var offsets = new Vector2[shaped.Count];
		var advances = new float[shaped.Count];
		var clusters = new int[shaped.Count];
		for (var i = 0; i < shaped.Count; i++)
		{
			var glyph = shaped[i];
			glyphs[i] = glyph.GlyphIndex;
			offsets[i] = new Vector2(glyph.OffsetX, glyph.OffsetY);
			advances[i] = glyph.AdvanceX;
			clusters[i] = glyph.Cluster;
		}
		return new GlyphRun(glyphs, offsets, advances, clusters);
	}

	public void BuildGlyphRun(IGeometryFactory geometry, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions, float baselineY, IList<GlyphRunElement> elements)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(elements);
		var monochromeGlyphs = new List<ushort>(glyphs.Length);
		var monochromePositions = new List<Vector2>(glyphs.Length);
		for (var i = 0; i < glyphs.Length; i++)
		{
			var glyphId = glyphs[i];
			var origin = new Vector2(positions[i].X, positions[i].Y + baselineY);
			if (TryBuildColorLayers(geometry, glyphId, origin, out var layers))
			{
				elements.Add(new GlyphColorLayers(layers));
				continue;
			}
			if (TryBuildBitmapGlyph(glyphId, origin, out var image))
			{
				elements.Add(image);
				continue;
			}
			if (Font.GetGlyphOutline(glyphId) is { } outline)
			{
				monochromeGlyphs.Add(glyphId);
				monochromePositions.Add(origin);
			}
		}
		if (monochromeGlyphs.Count > 0)
		{
			elements.Add(new GlyphOutline(new ProGpuGlyphRunGeometry(this, monochromeGlyphs.ToArray(), monochromePositions.ToArray())));
		}
	}

	public ushort GetGlyphIndex(int codepoint) => (uint)codepoint <= 0x10FFFF ? Font.GetGlyphIndex((uint)codepoint) : (ushort)0;
	public bool ContainsGlyph(int codepoint) => GetGlyphIndex(codepoint) != 0;
	public float GetGlyphAdvance(ushort glyph) => Font.GetAdvanceWidth(glyph, Size);

	private bool TryBuildColorLayers(IGeometryFactory geometry, ushort glyph, Vector2 origin, out IReadOnlyList<GlyphColorLayer> result)
	{
		result = Array.Empty<GlyphColorLayer>();
		if (Font.GetColorLayers(glyph) is not { Count: > 0 } source)
		{
			return false;
		}
		var layers = new List<GlyphColorLayer>(source.Count);
		foreach (var layer in source)
		{
			var path = layer.Geometry ?? Font.GetGlyphOutline(layer.GlyphId);
			if (path is null) continue;
			var builder = geometry.CreatePathBuilder();
			var transform = layer.UsesSvgCoordinates
				? Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(origin)
				: Matrix3x2.CreateScale(_scale, -_scale) * Matrix3x2.CreateTranslation(origin);
			EmitPath(path, builder, transform);
			var color = layer.Color;
			layers.Add(new GlyphColorLayer(builder.Build(), Color.FromArgb(ToByte(color.W), ToByte(color.X), ToByte(color.Y), ToByte(color.Z))));
		}
		result = layers;
		return layers.Count > 0;
	}

	private bool TryBuildBitmapGlyph(ushort glyph, Vector2 origin, out GlyphImage image)
	{
		image = null!;
		if (!Font.TryGetBitmapGlyph(glyph, Size, out var bitmap))
		{
			return false;
		}
		try
		{
			var decoded = ImageResult.FromMemory(bitmap.Data.ToArray(), ColorComponents.RedGreenBlueAlpha);
			var bgra = new byte[decoded.Data.Length];
			for (var i = 0; i < decoded.Data.Length; i += 4)
			{
				var a = decoded.Data[i + 3];
				bgra[i] = (byte)(decoded.Data[i + 2] * a / 255);
				bgra[i + 1] = (byte)(decoded.Data[i + 1] * a / 255);
				bgra[i + 2] = (byte)(decoded.Data[i] * a / 255);
				bgra[i + 3] = a;
			}
			var scale = bitmap.PixelsPerEm == 0 ? 1 : Size / bitmap.PixelsPerEm;
			var x = origin.X + (bitmap.UsesHorizontalMetrics ? bitmap.BearingX : -bitmap.OriginOffsetX) * scale;
			var y = origin.Y + (bitmap.UsesHorizontalMetrics ? -bitmap.BearingY : bitmap.OriginOffsetY - decoded.Height) * scale;
			image = new GlyphImage(bgra, decoded.Width, decoded.Height, x, y);
			return true;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	internal static void EmitPath(PGeometry path, IPathBuilder builder, Matrix3x2 transform)
	{
		foreach (var figure in path.Figures)
		{
			builder.MoveTo(Vector2.Transform(figure.StartPoint, transform));
			foreach (var segment in figure.Segments)
			{
				switch (segment)
				{
					case ProGPU.Vector.LineSegment line:
						builder.LineTo(Vector2.Transform(line.Point, transform));
						break;
					case ProGPU.Vector.QuadraticBezierSegment quadratic:
						builder.QuadraticTo(Vector2.Transform(quadratic.ControlPoint, transform), Vector2.Transform(quadratic.Point, transform));
						break;
					case ProGPU.Vector.CubicBezierSegment cubic:
						builder.CubicTo(Vector2.Transform(cubic.ControlPoint1, transform), Vector2.Transform(cubic.ControlPoint2, transform), Vector2.Transform(cubic.Point, transform));
						break;
					case ProGPU.Vector.ArcSegment arc:
						builder.ArcTo(new Vector2(Math.Abs(arc.Size.X * transform.M11), Math.Abs(arc.Size.Y * transform.M22)), arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection == ProGPU.Vector.SweepDirection.Counterclockwise, Vector2.Transform(arc.Point, transform));
						break;
				}
			}
			if (figure.IsClosed) builder.Close();
		}
	}

	private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}

/// <summary>
/// An <see cref="IGeometry"/> currency carrying a native ProGPU glyph run.
/// A ProGPU session consumes it through the glyph-atlas fast path; foreign
/// sessions receive lazily generated neutral outlines through the normal
/// geometry contract.
/// </summary>
internal sealed class ProGpuGlyphRunGeometry : IGeometry
{
	private ProGpuGeometry? _fallback;

	internal ProGpuGlyphRunGeometry(ProGpuFont font, ushort[] glyphs, Vector2[] positions)
	{
		Font = font;
		Glyphs = glyphs;
		Positions = positions;
	}

	internal ProGpuFont Font { get; }
	internal ushort[] Glyphs { get; }
	internal Vector2[] Positions { get; }
	public Rect Bounds => Fallback.Bounds;
	public GeometryFillRule FillRule => GeometryFillRule.NonZero;
	public bool IsEmpty => Glyphs.Length == 0;
	public int SegmentCount => Fallback.SegmentCount;
	public bool FillContains(Vector2 point) => Fallback.FillContains(point);
	public IGeometry Transform(Matrix3x2 matrix) => Fallback.Transform(matrix);
	public IGeometry Combine(IGeometry other, GeometryCombineMode mode) => Fallback.Combine(other, mode);
	public IGeometry GetFilledGeometry(float trimStart, float trimEnd) => Fallback.GetFilledGeometry(trimStart, trimEnd);
	public IGeometry GetStrokeFillGeometry(in StrokeStyle style) => Fallback.GetStrokeFillGeometry(style);
	public RoundRectangle? TryGetRoundRect() => null;
	public void StreamFlattened(IFlattenedPathSink sink) => Fallback.StreamFlattened(sink);
	public void StreamSegments(IGeometrySink sink) => Fallback.StreamSegments(sink);
	public void Dispose() => _fallback?.Dispose();

	private ProGpuGeometry Fallback
	{
		get
		{
			if (_fallback is not null) return _fallback;
			var builder = new ProGpuPathBuilder();
			var scale = Font.Font.UnitsPerEm == 0 ? 1f : Font.Size / Font.Font.UnitsPerEm;
			for (var i = 0; i < Glyphs.Length; i++)
			{
				if (Font.Font.GetGlyphOutline(Glyphs[i]) is { } outline)
				{
					ProGpuFont.EmitPath(outline, builder, Matrix3x2.CreateScale(scale, -scale) * Matrix3x2.CreateTranslation(Positions[i]));
				}
			}
			return _fallback = (ProGpuGeometry)builder.Build();
		}
	}
}
