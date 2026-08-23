#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using Uno.UI.Composition.Drawing;
using PArc = ProGPU.Vector.ArcSegment;
using PCubic = ProGPU.Vector.CubicBezierSegment;
using PGeometry = ProGPU.Vector.PathGeometry;
using PFigure = ProGPU.Vector.PathFigure;
using PLine = ProGPU.Vector.LineSegment;
using PQuadratic = ProGPU.Vector.QuadraticBezierSegment;

namespace Uno.UI.Composition.ProGpu;

internal static class ProGpuGeometryAlgorithms
{
	private const int QuadraticSteps = 16;
	private const int CubicSteps = 24;
	private const float Epsilon = 0.0001f;

	internal static PGeometry CreateRoundedRectangle(in RoundRectangle value)
	{
		var rect = value.Rect;
		var left = (float)rect.Left;
		var top = (float)rect.Top;
		var right = (float)rect.Right;
		var bottom = (float)rect.Bottom;
		var width = Math.Max(0, right - left);
		var height = Math.Max(0, bottom - top);
		var tl = ClampRadius(value.TopLeft, width, height);
		var tr = ClampRadius(value.TopRight, width, height);
		var br = ClampRadius(value.BottomRight, width, height);
		var bl = ClampRadius(value.BottomLeft, width, height);

		var path = new PGeometry();
		if (width <= Epsilon || height <= Epsilon)
		{
			return path;
		}

		var figure = new PFigure(new Vector2(left + tl.X, top), isClosed: true);
		figure.Segments.Add(new PLine(new Vector2(right - tr.X, top)));
		AddCorner(figure, new Vector2(right, top + tr.Y), tr);
		figure.Segments.Add(new PLine(new Vector2(right, bottom - br.Y)));
		AddCorner(figure, new Vector2(right - br.X, bottom), br);
		figure.Segments.Add(new PLine(new Vector2(left + bl.X, bottom)));
		AddCorner(figure, new Vector2(left, bottom - bl.Y), bl);
		figure.Segments.Add(new PLine(new Vector2(left, top + tl.Y)));
		AddCorner(figure, new Vector2(left + tl.X, top), tl);
		path.Figures.Add(figure);
		return path;
	}

	internal static void StreamSegments(PGeometry source, IGeometrySink sink)
	{
		var path = Resolve(source);
		foreach (var figure in path.Figures)
		{
			sink.BeginFigure(figure.StartPoint);
			var current = figure.StartPoint;
			foreach (var segment in figure.Segments)
			{
				switch (segment)
				{
					case PLine line:
						sink.LineTo(line.Point);
						current = line.Point;
						break;
					case PQuadratic quadratic:
						sink.QuadTo(quadratic.ControlPoint, quadratic.Point);
						current = quadratic.Point;
						break;
					case PCubic cubic:
						sink.CubicTo(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point);
						current = cubic.Point;
						break;
					case PArc arc:
						foreach (var point in ProGPU.Vector.ArcSegmentGeometry.FlattenArc(current, arc))
						{
							sink.LineTo(point);
						}
						current = arc.Point;
						break;
				}
			}
			sink.EndFigure(figure.IsClosed);
		}
	}

	internal static void StreamFlattened(PGeometry source, IFlattenedPathSink sink)
	{
		foreach (var contour in Flatten(source))
		{
			if (contour.Points.Count == 0)
			{
				continue;
			}
			sink.BeginContour(contour.Points[0]);
			for (var i = 1; i < contour.Points.Count; i++)
			{
				sink.LineTo(contour.Points[i]);
			}
			sink.EndContour(contour.Closed);
		}
	}

	internal static PGeometry Trim(PGeometry source, float start, float end)
	{
		start = Math.Clamp(start, 0, 1);
		end = Math.Clamp(end, 0, 1);
		if (end <= start)
		{
			return new PGeometry { FillRule = source.FillRule };
		}

		var result = new PGeometry { FillRule = source.FillRule };
		foreach (var contour in Flatten(source))
		{
			var trimmed = SlicePolyline(contour.Points, contour.Closed, start, end);
			if (trimmed.Count < 2)
			{
				continue;
			}
			var figure = new PFigure(trimmed[0]);
			for (var i = 1; i < trimmed.Count; i++)
			{
				figure.Segments.Add(new PLine(trimmed[i]));
			}
			result.Figures.Add(figure);
		}
		return result;
	}

	internal static PGeometry Widen(PGeometry source, in StrokeStyle style)
	{
		var result = new PGeometry { FillRule = ProGPU.Vector.FillRule.Nonzero };
		if (!float.IsFinite(style.Thickness) || style.Thickness <= 0)
		{
			return result;
		}

		// Uno mirrors Skia's convention: the default pair (0, 0) means that no
		// trim path effect was authored. Only a non-default member activates trim.
		var sourcePath = style.TrimStart != default || style.TrimEnd != default
			? Trim(source, style.TrimStart, style.TrimEnd)
			: source;
		foreach (var contour in Flatten(sourcePath))
		{
			var pieces = style.DashArray is { Length: > 0 }
				? Dash(contour.Points, contour.Closed, style)
				: new List<Contour> { contour };
			foreach (var piece in pieces)
			{
				AppendStroke(result, piece, style);
			}
		}
		return result;
	}

	private static void AppendStroke(PGeometry result, Contour contour, in StrokeStyle style)
	{
		var points = contour.Points;
		if (points.Count < 2)
		{
			return;
		}
		var half = style.Thickness * 0.5f;
		for (var i = 1; i < points.Count; i++)
		{
			AppendSegmentQuad(result, points[i - 1], points[i], half);
		}
		if (contour.Closed)
		{
			AppendSegmentQuad(result, points[^1], points[0], half);
		}

		var join = ToProGpu(style.LineJoin);
		var joinCount = contour.Closed ? points.Count : points.Count - 2;
		for (var j = 0; j < joinCount; j++)
		{
			var index = contour.Closed ? j : j + 1;
			var previous = points[(index - 1 + points.Count) % points.Count];
			var current = points[index];
			var next = points[(index + 1) % points.Count];
			foreach (var triangle in ProGPU.Vector.StrokeJoinGeometry.CreateLineJoin(join, style.Thickness, style.MiterLimit, previous, current, next))
			{
				AppendTriangle(result, triangle.P0, triangle.P1, triangle.P2);
			}
		}

		if (!contour.Closed)
		{
			AppendCaps(result, points[0], points[1], true, ToProGpu(style.StartCap), style.Thickness);
			AppendCaps(result, points[^2], points[^1], false, ToProGpu(style.EndCap), style.Thickness);
		}
	}

	private static void AppendSegmentQuad(PGeometry result, Vector2 start, Vector2 end, float half)
	{
		var delta = end - start;
		var length = delta.Length();
		if (length <= Epsilon || !float.IsFinite(length))
		{
			return;
		}
		var normal = new Vector2(-delta.Y, delta.X) * (half / length);
		var figure = new PFigure(start + normal, isClosed: true);
		figure.Segments.Add(new PLine(end + normal));
		figure.Segments.Add(new PLine(end - normal));
		figure.Segments.Add(new PLine(start - normal));
		result.Figures.Add(figure);
	}

	private static void AppendCaps(PGeometry result, Vector2 start, Vector2 end, bool isStart, ProGPU.Vector.PenLineCap cap, float thickness)
	{
		foreach (var triangle in ProGPU.Vector.StrokeCapGeometry.CreateLineCap(cap, thickness, start, end, isStart))
		{
			AppendTriangle(result, triangle.P0, triangle.P1, triangle.P2);
		}
	}

	private static void AppendTriangle(PGeometry result, Vector2 p0, Vector2 p1, Vector2 p2)
	{
		var figure = new PFigure(p0, isClosed: true);
		figure.Segments.Add(new PLine(p1));
		figure.Segments.Add(new PLine(p2));
		result.Figures.Add(figure);
	}

	private static List<Contour> Dash(List<Vector2> points, bool closed, in StrokeStyle style)
	{
		var output = new List<Contour>();
		if (points.Count < 2 || style.DashArray is not { Length: > 0 } authored)
		{
			return output;
		}
		var pattern = new float[authored.Length % 2 == 0 ? authored.Length : authored.Length * 2];
		for (var i = 0; i < pattern.Length; i++)
		{
			pattern[i] = Math.Max(Epsilon, Math.Abs(authored[i % authored.Length] * style.Thickness));
		}
		var period = 0f;
		foreach (var value in pattern) period += value;
		var offset = ((style.DashOffset * style.Thickness % period) + period) % period;
		var patternIndex = 0;
		while (offset >= pattern[patternIndex])
		{
			offset -= pattern[patternIndex++];
			patternIndex %= pattern.Length;
		}
		var remaining = pattern[patternIndex] - offset;
		var draw = patternIndex % 2 == 0;
		Contour? active = null;
		var segmentCount = closed ? points.Count : points.Count - 1;
		for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
		{
			var a = points[segmentIndex];
			var b = points[(segmentIndex + 1) % points.Count];
			var delta = b - a;
			var length = delta.Length();
			if (length <= Epsilon) continue;
			var consumed = 0f;
			while (consumed < length - Epsilon)
			{
				var step = Math.Min(remaining, length - consumed);
				var p0 = a + delta * (consumed / length);
				var p1 = a + delta * ((consumed + step) / length);
				if (draw)
				{
					active ??= new Contour(new List<Vector2> { p0 }, false);
					if (Vector2.DistanceSquared(active.Points[^1], p1) > Epsilon * Epsilon) active.Points.Add(p1);
				}
				consumed += step;
				remaining -= step;
				if (remaining <= Epsilon)
				{
					if (draw && active is not null)
					{
						output.Add(active);
						active = null;
					}
					patternIndex = (patternIndex + 1) % pattern.Length;
					draw = patternIndex % 2 == 0;
					remaining = pattern[patternIndex];
				}
			}
		}
		if (active is not null) output.Add(active);
		return output;
	}

	private static List<Vector2> SlicePolyline(List<Vector2> points, bool closed, float start, float end)
	{
		var result = new List<Vector2>();
		if (points.Count < 2) return result;
		var segmentCount = closed ? points.Count : points.Count - 1;
		var lengths = new float[segmentCount];
		var total = 0f;
		for (var i = 0; i < segmentCount; i++)
		{
			lengths[i] = Vector2.Distance(points[i], points[(i + 1) % points.Count]);
			total += lengths[i];
		}
		var from = total * start;
		var to = total * end;
		var cursor = 0f;
		for (var i = 0; i < segmentCount && cursor < to; i++)
		{
			var next = cursor + lengths[i];
			if (next > from && cursor < to && lengths[i] > Epsilon)
			{
				var a = points[i];
				var b = points[(i + 1) % points.Count];
				var localStart = Math.Clamp((from - cursor) / lengths[i], 0, 1);
				var localEnd = Math.Clamp((to - cursor) / lengths[i], 0, 1);
				var p0 = Vector2.Lerp(a, b, localStart);
				var p1 = Vector2.Lerp(a, b, localEnd);
				if (result.Count == 0 || Vector2.DistanceSquared(result[^1], p0) > Epsilon * Epsilon) result.Add(p0);
				if (Vector2.DistanceSquared(result[^1], p1) > Epsilon * Epsilon) result.Add(p1);
			}
			cursor = next;
		}
		return result;
	}

	private static List<Contour> Flatten(PGeometry source)
	{
		var result = new List<Contour>();
		var path = Resolve(source);
		foreach (var figure in path.Figures)
		{
			var points = new List<Vector2> { figure.StartPoint };
			var current = figure.StartPoint;
			foreach (var segment in figure.Segments)
			{
				switch (segment)
				{
					case PLine line:
						AddDistinct(points, line.Point);
						current = line.Point;
						break;
					case PQuadratic quadratic:
						for (var i = 1; i <= QuadraticSteps; i++)
						{
							var t = (float)i / QuadraticSteps;
							var u = 1 - t;
							AddDistinct(points, u * u * current + 2 * u * t * quadratic.ControlPoint + t * t * quadratic.Point);
						}
						current = quadratic.Point;
						break;
					case PCubic cubic:
						for (var i = 1; i <= CubicSteps; i++)
						{
							var t = (float)i / CubicSteps;
							var u = 1 - t;
							AddDistinct(points, u * u * u * current + 3 * u * u * t * cubic.ControlPoint1 + 3 * u * t * t * cubic.ControlPoint2 + t * t * t * cubic.Point);
						}
						current = cubic.Point;
						break;
					case PArc arc:
						foreach (var point in ProGPU.Vector.ArcSegmentGeometry.FlattenArc(current, arc)) AddDistinct(points, point);
						current = arc.Point;
						break;
				}
			}
			if (figure.IsClosed && points.Count > 1 && Vector2.DistanceSquared(points[0], points[^1]) <= Epsilon * Epsilon)
			{
				points.RemoveAt(points.Count - 1);
			}
			result.Add(new Contour(points, figure.IsClosed));
		}
		return result;
	}

	private static PGeometry Resolve(PGeometry path)
	{
		while (path.IsCombined && path.PathA is not null && path.PathB is not null)
		{
			path = ProGPU.Vector.PathOpGeometrySolver.Combine(path.PathA, path.PathB, path.Op);
		}
		return path;
	}

	private static void AddCorner(PFigure figure, Vector2 end, Vector2 radius)
	{
		if (radius.X <= Epsilon || radius.Y <= Epsilon)
		{
			figure.Segments.Add(new PLine(end));
		}
		else
		{
			figure.Segments.Add(new PArc(end, radius, 0, false, ProGPU.Vector.SweepDirection.Clockwise));
		}
	}

	private static Vector2 ClampRadius(Vector2 radius, float width, float height) =>
		new(Math.Clamp(Math.Abs(radius.X), 0, width * 0.5f), Math.Clamp(Math.Abs(radius.Y), 0, height * 0.5f));

	private static void AddDistinct(List<Vector2> points, Vector2 point)
	{
		if (points.Count == 0 || Vector2.DistanceSquared(points[^1], point) > Epsilon * Epsilon) points.Add(point);
	}

	private static ProGPU.Vector.PenLineJoin ToProGpu(StrokeJoin value) => value switch
	{
		StrokeJoin.Round => ProGPU.Vector.PenLineJoin.Round,
		StrokeJoin.Bevel => ProGPU.Vector.PenLineJoin.Bevel,
		_ => ProGPU.Vector.PenLineJoin.Miter,
	};

	private static ProGPU.Vector.PenLineCap ToProGpu(StrokeCap value) => value switch
	{
		StrokeCap.Round => ProGPU.Vector.PenLineCap.Round,
		StrokeCap.Square => ProGPU.Vector.PenLineCap.Square,
		StrokeCap.Triangle => ProGPU.Vector.PenLineCap.Triangle,
		_ => ProGPU.Vector.PenLineCap.Flat,
	};

	private sealed record Contour(List<Vector2> Points, bool Closed);
}
