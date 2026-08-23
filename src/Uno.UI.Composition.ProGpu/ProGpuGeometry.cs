extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using Uno.UI.Composition.Drawing;
using Rect = unofoundation::Windows.Foundation.Rect;
using PArc = ProGPU.Vector.ArcSegment;
using PCubic = ProGPU.Vector.CubicBezierSegment;
using PFillRule = ProGPU.Vector.FillRule;
using PGeometry = ProGPU.Vector.PathGeometry;
using PFigure = ProGPU.Vector.PathFigure;
using PLine = ProGPU.Vector.LineSegment;
using PQuadratic = ProGPU.Vector.QuadraticBezierSegment;

namespace Uno.UI.Composition.ProGpu;

public sealed class ProGpuGeometryFactory : IGeometryFactory
{
	public IPathBuilder CreatePathBuilder() => new ProGpuPathBuilder();

	public IPrimitiveGeometryBuilder CreatePrimitiveGeometryBuilder() => new ProGpuPrimitiveGeometryBuilder();

	internal static ProGpuGeometry Import(IGeometry geometry)
	{
		if (geometry is ProGpuGeometry native)
		{
			return native;
		}

		var sink = new SegmentImportSink(geometry.FillRule);
		geometry.StreamSegments(sink);
		return new ProGpuGeometry(sink.Geometry);
	}

	private sealed class SegmentImportSink(GeometryFillRule fillRule) : IGeometrySink
	{
		private PFigure? _figure;
		public PGeometry Geometry { get; } = new()
		{
			FillRule = fillRule == GeometryFillRule.EvenOdd ? PFillRule.EvenOdd : PFillRule.Nonzero,
		};

		public void BeginFigure(Vector2 start)
		{
			EndFigure(false);
			_figure = new PFigure(start);
			Geometry.Figures.Add(_figure);
		}

		public void LineTo(Vector2 point) => EnsureFigure().Segments.Add(new PLine(point));
		public void QuadTo(Vector2 control, Vector2 point) => EnsureFigure().Segments.Add(new PQuadratic(control, point));
		public void CubicTo(Vector2 control1, Vector2 control2, Vector2 point) => EnsureFigure().Segments.Add(new PCubic(control1, control2, point));

		public void EndFigure(bool closed)
		{
			if (_figure is not null)
			{
				_figure.IsClosed = closed;
				_figure = null;
			}
		}

		private PFigure EnsureFigure()
		{
			if (_figure is null)
			{
				BeginFigure(Vector2.Zero);
			}
			return _figure!;
		}
	}
}

public sealed class ProGpuGeometry : IGeometry, unouwp::Windows.Graphics.IGeometrySource2D
{
	private readonly RoundRectangle? _roundRect;

	internal ProGpuGeometry(PGeometry path, RoundRectangle? roundRect = null)
	{
		Path = path ?? throw new ArgumentNullException(nameof(path));
		_roundRect = roundRect;
	}

	internal PGeometry Path { get; }

	public Rect Bounds
	{
		get
		{
			if (!Path.TryGetBounds(out var min, out var max))
			{
				return default;
			}
			return new Rect(min.X, min.Y, Math.Max(0, max.X - min.X), Math.Max(0, max.Y - min.Y));
		}
	}

	public GeometryFillRule FillRule => Path.FillRule == PFillRule.EvenOdd
		? GeometryFillRule.EvenOdd
		: GeometryFillRule.NonZero;

	public bool IsEmpty => !Path.TryGetBounds(out _, out _);

	public int SegmentCount => CountSegments(Path);

	public bool FillContains(Vector2 point) => Contains(Path, point);

	public IGeometry Transform(Matrix3x2 matrix)
	{
		var transform = new Matrix4x4(
			matrix.M11, matrix.M12, 0, 0,
			matrix.M21, matrix.M22, 0, 0,
			0, 0, 1, 0,
			matrix.M31, matrix.M32, 0, 1);
		return new ProGpuGeometry(Path.CreateTransformed(transform));
	}

	public IGeometry Combine(IGeometry other, GeometryCombineMode mode)
	{
		ArgumentNullException.ThrowIfNull(other);
		var right = ProGpuGeometryFactory.Import(other);
		if ((_roundRect is { } leftRoundRect && right._roundRect is { } rightRoundRect && leftRoundRect == rightRoundRect) ||
			PathsEqual(Path, right.Path))
		{
			return mode switch
			{
				GeometryCombineMode.Difference or GeometryCombineMode.Xor =>
					new ProGpuGeometry(new PGeometry { FillRule = Path.FillRule }),
				GeometryCombineMode.Intersect or GeometryCombineMode.Union =>
					new ProGpuGeometry(Path.CreateTransformed(Matrix4x4.Identity), _roundRect),
				_ => throw new ArgumentOutOfRangeException(nameof(mode)),
			};
		}
		if (mode == GeometryCombineMode.Difference &&
			_roundRect is { } outerRoundRect && right._roundRect is { } innerRoundRect &&
			IsStandardBorderRing(outerRoundRect, innerRoundRect))
		{
			var ring = new PGeometry { FillRule = PFillRule.EvenOdd };
			var outerPath = Path.CreateTransformed(Matrix4x4.Identity);
			var innerPath = right.Path.CreateTransformed(Matrix4x4.Identity);
			foreach (var figure in outerPath.Figures)
			{
				ring.Figures.Add(figure);
			}
			foreach (var figure in innerPath.Figures)
			{
				ring.Figures.Add(figure);
			}
			return new ProGpuGeometry(ring);
		}
		return new ProGpuGeometry(new PGeometry
		{
			IsCombined = true,
			PathA = Path,
			PathB = right.Path,
			Op = mode switch
			{
				GeometryCombineMode.Difference => 0,
				GeometryCombineMode.Intersect => 1,
				GeometryCombineMode.Union => 2,
				GeometryCombineMode.Xor => 3,
				_ => throw new ArgumentOutOfRangeException(nameof(mode)),
			},
			FillRule = Path.FillRule,
		});
	}

	private static bool IsStandardBorderRing(RoundRectangle outer, RoundRectangle inner)
	{
		var left = (float)(inner.Rect.X - outer.Rect.X);
		var top = (float)(inner.Rect.Y - outer.Rect.Y);
		var right = (float)(outer.Rect.Right - inner.Rect.Right);
		var bottom = (float)(outer.Rect.Bottom - inner.Rect.Bottom);
		if (left < 0 || top < 0 || right < 0 || bottom < 0)
		{
			return false;
		}

		return RadiusMatches(inner.TopLeft.X, outer.TopLeft.X, left) &&
			RadiusMatches(inner.TopLeft.Y, outer.TopLeft.Y, top) &&
			RadiusMatches(inner.TopRight.X, outer.TopRight.X, right) &&
			RadiusMatches(inner.TopRight.Y, outer.TopRight.Y, top) &&
			RadiusMatches(inner.BottomRight.X, outer.BottomRight.X, right) &&
			RadiusMatches(inner.BottomRight.Y, outer.BottomRight.Y, bottom) &&
			RadiusMatches(inner.BottomLeft.X, outer.BottomLeft.X, left) &&
			RadiusMatches(inner.BottomLeft.Y, outer.BottomLeft.Y, bottom);
	}

	private static bool RadiusMatches(float inner, float outer, float inset) =>
		MathF.Abs(inner - MathF.Max(0, outer - inset)) <= 0.001f;

	private static bool PathsEqual(PGeometry left, PGeometry right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}
		if (left.IsCombined != right.IsCombined || left.FillRule != right.FillRule)
		{
			return false;
		}
		if (left.IsCombined)
		{
			return left.Op == right.Op &&
				left.PathA is { } leftA && right.PathA is { } rightA && PathsEqual(leftA, rightA) &&
				left.PathB is { } leftB && right.PathB is { } rightB && PathsEqual(leftB, rightB);
		}

		var leftFigures = left.Figures;
		var rightFigures = right.Figures;
		if (leftFigures.Count != rightFigures.Count)
		{
			return false;
		}
		for (var figureIndex = 0; figureIndex < leftFigures.Count; figureIndex++)
		{
			var leftFigure = leftFigures[figureIndex];
			var rightFigure = rightFigures[figureIndex];
			if (leftFigure.StartPoint != rightFigure.StartPoint ||
				leftFigure.IsClosed != rightFigure.IsClosed ||
				leftFigure.IsFilled != rightFigure.IsFilled ||
				leftFigure.StrokeStartLineCap != rightFigure.StrokeStartLineCap ||
				leftFigure.StrokeEndLineCap != rightFigure.StrokeEndLineCap ||
				leftFigure.Segments.Count != rightFigure.Segments.Count)
			{
				return false;
			}
			for (var segmentIndex = 0; segmentIndex < leftFigure.Segments.Count; segmentIndex++)
			{
				var leftSegment = leftFigure.Segments[segmentIndex];
				var rightSegment = rightFigure.Segments[segmentIndex];
				if (leftSegment.IsSmoothJoin != rightSegment.IsSmoothJoin || leftSegment.IsStroked != rightSegment.IsStroked)
				{
					return false;
				}
				if (leftSegment switch
				{
					PLine leftLine when rightSegment is PLine rightLine => leftLine.Point != rightLine.Point,
					PQuadratic leftQuadratic when rightSegment is PQuadratic rightQuadratic =>
						leftQuadratic.ControlPoint != rightQuadratic.ControlPoint || leftQuadratic.Point != rightQuadratic.Point,
					PCubic leftCubic when rightSegment is PCubic rightCubic =>
						leftCubic.ControlPoint1 != rightCubic.ControlPoint1 ||
						leftCubic.ControlPoint2 != rightCubic.ControlPoint2 || leftCubic.Point != rightCubic.Point,
					PArc leftArc when rightSegment is PArc rightArc =>
						leftArc.Point != rightArc.Point || leftArc.Size != rightArc.Size ||
						leftArc.RotationAngle != rightArc.RotationAngle || leftArc.IsLargeArc != rightArc.IsLargeArc ||
						leftArc.SweepDirection != rightArc.SweepDirection,
					_ => true,
				})
				{
					return false;
				}
			}
		}
		return true;
	}

	public IGeometry GetFilledGeometry(float trimStart, float trimEnd)
	{
		// Uno and the Skia backend use the default pair (0, 0) to mean that no
		// trim effect is present. A literal empty trim range is only meaningful
		// when at least one trim value was explicitly set.
		if ((trimStart == default && trimEnd == default) ||
			(trimStart <= 0 && trimEnd >= 1))
		{
			return new ProGpuGeometry(Path.CreateTransformed(Matrix4x4.Identity), _roundRect);
		}

		return new ProGpuGeometry(ProGpuGeometryAlgorithms.Trim(Path, trimStart, trimEnd));
	}

	public IGeometry GetStrokeFillGeometry(in StrokeStyle style) =>
		new ProGpuGeometry(ProGpuGeometryAlgorithms.Widen(Path, style));

	public RoundRectangle? TryGetRoundRect() => _roundRect;

	public void StreamFlattened(IFlattenedPathSink sink)
	{
		ArgumentNullException.ThrowIfNull(sink);
		ProGpuGeometryAlgorithms.StreamFlattened(Path, sink);
	}

	public void StreamSegments(IGeometrySink sink)
	{
		ArgumentNullException.ThrowIfNull(sink);
		ProGpuGeometryAlgorithms.StreamSegments(Path, sink);
	}

	public void Dispose()
	{
		// ProGPU vector paths are immutable managed values. GPU caches are owned by
		// retained records/compositors, not by this CPU geometry handle.
	}

	private static int CountSegments(PGeometry path)
	{
		if (path.IsCombined)
		{
			return (path.PathA is null ? 0 : CountSegments(path.PathA)) +
				(path.PathB is null ? 0 : CountSegments(path.PathB));
		}

		var count = 0;
		foreach (var figure in path.Figures)
		{
			count += figure.Segments.Count;
		}
		return count;
	}

	private static bool Contains(PGeometry path, Vector2 point)
	{
		if (!path.IsCombined)
		{
			return ProGPU.Vector.PathGeometryHitTesting.TryContainsFill(path, point, 0, false, out var contains) && contains;
		}

		var left = path.PathA is not null && Contains(path.PathA, point);
		var right = path.PathB is not null && Contains(path.PathB, point);
		return path.Op switch
		{
			0 => left && !right,
			1 => left && right,
			2 => left || right,
			3 => left != right,
			4 => right && !left,
			_ => false,
		};
	}
}

internal sealed class ProGpuPathBuilder : IPathBuilder
{
	private PGeometry _path = new();
	private PFigure? _figure;

	public GeometryFillRule FillRule { get; set; } = GeometryFillRule.NonZero;

	public void MoveTo(Vector2 point)
	{
		FinishFigure();
		_figure = new PFigure(point);
		_path.Figures.Add(_figure);
	}

	public void LineTo(Vector2 point) => EnsureFigure().Segments.Add(new PLine(point));
	public void CubicTo(Vector2 control1, Vector2 control2, Vector2 end) => EnsureFigure().Segments.Add(new PCubic(control1, control2, end));
	public void QuadraticTo(Vector2 control, Vector2 end) => EnsureFigure().Segments.Add(new PQuadratic(control, end));
	public void ArcTo(Vector2 radius, float rotationAngle, bool isLargeArc, bool clockwise, Vector2 end) =>
		EnsureFigure().Segments.Add(new PArc(end, radius, rotationAngle, isLargeArc,
			clockwise ? ProGPU.Vector.SweepDirection.Clockwise : ProGPU.Vector.SweepDirection.Counterclockwise));

	public void Close()
	{
		if (_figure is not null)
		{
			_figure.IsClosed = true;
		}
	}

	public IGeometry Build()
	{
		FinishFigure();
		_path.FillRule = FillRule == GeometryFillRule.EvenOdd ? PFillRule.EvenOdd : PFillRule.Nonzero;
		var result = new ProGpuGeometry(_path);
		_path = new PGeometry();
		FillRule = GeometryFillRule.NonZero;
		return result;
	}

	private PFigure EnsureFigure()
	{
		if (_figure is null)
		{
			MoveTo(Vector2.Zero);
		}
		return _figure!;
	}

	private void FinishFigure() => _figure = null;
}

internal sealed class ProGpuPrimitiveGeometryBuilder : IPrimitiveGeometryBuilder
{
	private PGeometry _path = new();
	private RoundRectangle? _singleRoundRect;

	public GeometryFillRule FillRule { get; set; } = GeometryFillRule.NonZero;

	public void AddRectangle(Rect rect)
	{
		Append(ProGPU.Vector.PrimitivePathGeometry.CreateRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height));
		_singleRoundRect = _path.Figures.Count == 1 ? new RoundRectangle { Rect = rect } : null;
	}

	public void AddRoundedRectangle(Rect rect, float radiusX, float radiusY) =>
		AddRoundedRectangle(rect, new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY));

	public void AddRoundedRectangle(Rect rect, Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft)
	{
		var roundRect = new RoundRectangle
		{
			Rect = rect,
			TopLeft = topLeft,
			TopRight = topRight,
			BottomRight = bottomRight,
			BottomLeft = bottomLeft,
		};
		Append(ProGpuGeometryAlgorithms.CreateRoundedRectangle(roundRect));
		_singleRoundRect = _path.Figures.Count == 1 ? roundRect : null;
	}

	public void AddEllipse(Vector2 center, float radiusX, float radiusY)
	{
		Append(ProGPU.Vector.PrimitivePathGeometry.CreateEllipse(center, radiusX, radiusY));
		_singleRoundRect = null;
	}

	public void AddGeometry(IGeometry geometry)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		Append(ProGpuGeometryFactory.Import(geometry).Path.CreateTransformed(Matrix4x4.Identity));
		_singleRoundRect = null;
	}

	public IGeometry Build()
	{
		_path.FillRule = FillRule == GeometryFillRule.EvenOdd ? PFillRule.EvenOdd : PFillRule.Nonzero;
		var result = new ProGpuGeometry(_path, _singleRoundRect);
		_path = new PGeometry();
		_singleRoundRect = null;
		FillRule = GeometryFillRule.NonZero;
		return result;
	}

	private void Append(PGeometry geometry)
	{
		foreach (var figure in geometry.Figures)
		{
			_path.Figures.Add(figure);
		}
	}
}
