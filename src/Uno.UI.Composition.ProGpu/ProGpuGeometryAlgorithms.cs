extern alias unofoundation;

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
using Rect = unofoundation::Windows.Foundation.Rect;

namespace Uno.UI.Composition.ProGpu;

internal static class ProGpuGeometryAlgorithms
{
	private const float Epsilon = 0.0001f;

	internal static Rect GetBounds(PGeometry source)
	{
		if (source.IsCombined)
		{
			// Preserve deferred boolean geometry; resolving it here submits GPU work and reads it back.
			return source.TryGetBounds(out var combinedMin, out var combinedMax)
				? new Rect(combinedMin.X, combinedMin.Y, combinedMax.X - combinedMin.X, combinedMax.Y - combinedMin.Y)
				: default;
		}
		var min = new Vector2(float.PositiveInfinity);
		var max = new Vector2(float.NegativeInfinity);
		void Include(Vector2 point)
		{
			min = Vector2.Min(min, point);
			max = Vector2.Max(max, point);
		}
		void IncludeCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
		{
			void Extremum(double a, double b, double c)
			{
				void Evaluate(double t)
				{
					if (t > 0 && t < 1)
					{
						var u = (float)t;
						var v = 1 - u;
						Include(v * v * v * p0 + 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u * p3);
					}
				}
				if (Math.Abs(a) < 1e-12)
				{
					if (Math.Abs(b) > 1e-12)
					{
						Evaluate(-c / b);
					}
					return;
				}
				var discriminant = b * b - 4 * a * c;
				if (discriminant >= 0)
				{
					var root = Math.Sqrt(discriminant);
					Evaluate((-b + root) / (2 * a));
					Evaluate((-b - root) / (2 * a));
				}
			}
			Extremum(-p0.X + 3d * p1.X - 3d * p2.X + p3.X, 2d * (p0.X - 2d * p1.X + p2.X), p1.X - p0.X);
			Extremum(-p0.Y + 3d * p1.Y - 3d * p2.Y + p3.Y, 2d * (p0.Y - 2d * p1.Y + p2.Y), p1.Y - p0.Y);
			Include(p3);
		}
		foreach (var figure in source.Figures)
		{
			if (figure.Segments.Count == 0)
			{
				continue;
			}
			var current = figure.StartPoint;
			Include(current);
			foreach (var segment in figure.Segments)
			{
				switch (segment)
				{
					case PLine line:
						Include(current = line.Point);
						break;
					case PQuadratic quadratic:
						IncludeCubic(current, current + (quadratic.ControlPoint - current) * (2f / 3), quadratic.Point + (quadratic.ControlPoint - quadratic.Point) * (2f / 3), quadratic.Point);
						current = quadratic.Point;
						break;
					case PCubic cubic:
						IncludeCubic(current, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point);
						current = cubic.Point;
						break;
					case PArc arc:
						if (ProGPU.Vector.ArcSegmentGeometry.TryGetArcBounds(current, arc, out var arcMin, out var arcMax))
						{
							Include(arcMin);
							Include(arcMax);
						}
						Include(current = arc.Point);
						break;
				}
			}
		}
		return float.IsPositiveInfinity(min.X) ? default : new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
	}

	internal static PGeometry CreateRoundedRectangle(in RoundRectangle value)
	{
		var rect = value.Rect;
		var left = (float)rect.Left;
		var top = (float)rect.Top;
		var right = (float)rect.Right;
		var bottom = (float)rect.Bottom;
		var width = Math.Max(0, right - left);
		var height = Math.Max(0, bottom - top);
		var tl = NormalizeCorner(value.TopLeft);
		var tr = NormalizeCorner(value.TopRight);
		var br = NormalizeCorner(value.BottomRight);
		var bl = NormalizeCorner(value.BottomLeft);

		var factor = MathF.Min(1, MathF.Min(
			MathF.Min(RadiusScale(width, tl.X + tr.X), RadiusScale(width, bl.X + br.X)),
			MathF.Min(RadiusScale(height, tl.Y + bl.Y), RadiusScale(height, tr.Y + br.Y))));
		tl *= factor;
		tr *= factor;
		br *= factor;
		bl *= factor;

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

	private static Vector2 NormalizeCorner(Vector2 radius) =>
		radius.X > Epsilon && radius.Y > Epsilon ? radius : Vector2.Zero;

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

	internal static void StreamFlattened(PGeometry source, IFlattenedPathSink sink, float scale = 1f)
	{
		foreach (var contour in Flatten(source, scale))
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

	internal static PGeometry Trim(PGeometry source, float start, float end, float scale = 1f)
	{
		start = Math.Clamp(start, 0, 1);
		end = Math.Clamp(end, 0, 1);
		if (end <= start)
		{
			return new PGeometry { FillRule = source.FillRule };
		}

		var result = new PGeometry { FillRule = source.FillRule };
		foreach (var contour in Flatten(source, scale))
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

	internal static PGeometry Widen(PGeometry source, in StrokeStyle style, float scale = 1f)
	{
		var result = new PGeometry { FillRule = ProGPU.Vector.FillRule.Nonzero };
		if (!float.IsFinite(style.Thickness) || style.Thickness <= 0)
		{
			return result;
		}

		// Uno mirrors Skia's convention: the default pair (0, 0) means that no
		// trim path effect was authored. Only a non-default member activates trim.
		var sourcePath = style.TrimStart != default || style.TrimEnd != default
			? Trim(source, style.TrimStart, style.TrimEnd, scale)
			: source;
		foreach (var contour in Flatten(sourcePath, scale))
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
		// Stroke primitives form a union under nonzero fill, including self-overlap.
		var first = p1 - p0;
		var second = p2 - p0;
		if (first.X * second.Y - first.Y * second.X > 0)
		{
			(p1, p2) = (p2, p1);
		}
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
			pattern[i] = Math.Max(Epsilon, Math.Abs(authored[i % authored.Length]));
		}
		var period = 0f;
		foreach (var value in pattern) period += value;
		var offset = ((style.DashOffset % period) + period) % period;
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

	private static List<Contour> Flatten(PGeometry source, float scale)
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
						var quadraticSteps = Subdivisions(Vector2.Distance(current - 2f * quadratic.ControlPoint + quadratic.Point, Vector2.Zero) * 2f, scale);
						for (var i = 1; i <= quadraticSteps; i++)
						{
							var t = (float)i / quadraticSteps;
							var u = 1 - t;
							AddDistinct(points, u * u * current + 2 * u * t * quadratic.ControlPoint + t * t * quadratic.Point);
						}
						current = quadratic.Point;
						break;
					case PCubic cubic:
						var cubicSteps = Subdivisions(6f * MathF.Max(
							(current - 2f * cubic.ControlPoint1 + cubic.ControlPoint2).Length(),
							(cubic.ControlPoint1 - 2f * cubic.ControlPoint2 + cubic.Point).Length()), scale);
						for (var i = 1; i <= cubicSteps; i++)
						{
							var t = (float)i / cubicSteps;
							var u = 1 - t;
							AddDistinct(points, u * u * u * current + 3 * u * u * t * cubic.ControlPoint1 + 3 * u * t * t * cubic.ControlPoint2 + t * t * t * cubic.Point);
						}
						current = cubic.Point;
						break;
					case PArc arc:
						AppendArc(points, current, arc, scale);
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

	private static int Subdivisions(float secondDerivativeBound, float scale)
	{
		// The chord error is bounded by max(|p''|) / (8 * n²), at 1/8 device pixel.
		var magnification = float.IsFinite(scale) ? MathF.Max(1, scale) : 1f;
		return (int)Math.Clamp(Math.Ceiling(Math.Sqrt(secondDerivativeBound * (double)magnification)), 1, 65536);
	}

	private static void AppendArc(List<Vector2> points, Vector2 start, PArc arc, float scale)
	{
		if (!ProGPU.Vector.ArcSegmentGeometry.TryGetArcCenter(start, arc.Point, arc.Size, arc.RotationAngle,
			arc.IsLargeArc, arc.SweepDirection, out var center, out var angle, out var sweep, out var rx, out var ry))
		{
			AddDistinct(points, arc.Point);
			return;
		}
		var steps = Subdivisions(MathF.Max(rx, ry) * sweep * sweep, scale);
		var rotation = Matrix3x2.CreateRotation(arc.RotationAngle * MathF.PI / 180f);
		for (var i = 1; i < steps; i++)
		{
			var theta = angle + sweep * i / steps;
			AddDistinct(points, center + Vector2.Transform(new Vector2(rx * MathF.Cos(theta), ry * MathF.Sin(theta)), rotation));
		}
		AddDistinct(points, arc.Point);
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
		if (radius.X > Epsilon && radius.Y > Epsilon)
		{
			figure.Segments.Add(new PArc(end, radius, 0, false, ProGPU.Vector.SweepDirection.Clockwise));
		}
	}

	private static float RadiusScale(float extent, float sum) => sum > 0 ? extent / sum : 1f;

	private static void AddDistinct(List<Vector2> points, Vector2 point)
	{
		if (points.Count == 0 || Vector2.DistanceSquared(points[^1], point) > Epsilon * Epsilon) points.Add(point);
	}

	internal static ProGPU.Vector.PenLineJoin ToProGpu(StrokeJoin value) => value switch
	{
		StrokeJoin.Round => ProGPU.Vector.PenLineJoin.Round,
		StrokeJoin.Bevel => ProGPU.Vector.PenLineJoin.Bevel,
		_ => ProGPU.Vector.PenLineJoin.Miter,
	};

	internal static ProGPU.Vector.PenLineCap ToProGpu(StrokeCap value) => value switch
	{
		StrokeCap.Round => ProGPU.Vector.PenLineCap.Round,
		StrokeCap.Square => ProGPU.Vector.PenLineCap.Square,
		StrokeCap.Triangle => ProGPU.Vector.PenLineCap.Triangle,
		_ => ProGPU.Vector.PenLineCap.Flat,
	};

	private sealed record Contour(List<Vector2> Points, bool Closed);
}
