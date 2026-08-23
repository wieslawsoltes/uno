#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using ProGPU.Scene;

namespace Uno.UI.Composition.ProGpu;

public sealed record ProGpuFrameMetrics(
	long FrameNumber,
	double CpuRecordMilliseconds,
	double CpuSubmitMilliseconds,
	int CommandCount,
	long UploadBytes,
	int UnsupportedOperationCount)
{
	public double CompositorFrameMilliseconds { get; init; }
	public double SceneCompileMilliseconds { get; init; }
	public double GpuUploadMilliseconds { get; init; }
	public double RenderPassMilliseconds { get; init; }
	public int DrawCallCount { get; init; }
	public int VectorVertexCount { get; init; }
	public int VectorIndexCount { get; init; }
	public int TextVertexCount { get; init; }
	public int SceneUploadBatchCount { get; init; }
	public int SceneUploadCopyCount { get; init; }
	public int MaskRenderPassCount { get; init; }
	public int MaskRenderDrawCallCount { get; init; }
	public int MaskTexturePeakDemand { get; init; }
	public int RetainedCompositionPictureCount { get; init; }
	public long RetainedCompositionPictureHits { get; init; }
	public long RetainedCompositionPictureMisses { get; init; }
	public long RetainedCompositionPictureCompilations { get; init; }
	public bool SceneCacheHit { get; init; }
	public string? SceneCacheMissReason { get; init; }
}

public static class ProGpuDiagnostics
{
	private static long _unsupportedOperationCount;
	private static long _deviceGeneration;
	private static ProGpuFrameMetrics? _lastFrame;
	private static int _sceneDumped;

	public static event Action<ProGpuFrameMetrics>? FrameCompleted;

	public static long UnsupportedOperationCount =>
		Interlocked.Read(ref _unsupportedOperationCount);

	public static long DeviceGeneration =>
		Interlocked.Read(ref _deviceGeneration);

	public static ProGpuFrameMetrics? LastFrame => Volatile.Read(ref _lastFrame);

	internal static void Unsupported(string operation, bool fail)
	{
		Interlocked.Increment(ref _unsupportedOperationCount);
		if (fail)
		{
			throw new NotSupportedException($"The ProGPU backend does not support drawing operation '{operation}'.");
		}
	}

	internal static long NextDeviceGeneration() => Interlocked.Increment(ref _deviceGeneration);

	internal static void Publish(ProGpuFrameMetrics metrics)
	{
		Volatile.Write(ref _lastFrame, metrics);
		FrameCompleted?.Invoke(metrics);
	}

	internal static string? TryDumpScene(GpuPicture picture, long frame)
	{
		var path = Environment.GetEnvironmentVariable("UNO_PROGPU_DUMP_SCENE");
		var waitForHostBackdrop = string.Equals(
			Environment.GetEnvironmentVariable("UNO_PROGPU_DUMP_HOST_BACKDROP"),
			"1",
			StringComparison.Ordinal);
		var requestedFrame = long.TryParse(
			Environment.GetEnvironmentVariable("UNO_PROGPU_DUMP_FRAME"),
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var parsedFrame)
			? Math.Max(1, parsedFrame)
			: 1;
		if (string.IsNullOrWhiteSpace(path) || frame < requestedFrame ||
			(waitForHostBackdrop && !ContainsHostBackdrop(picture)) ||
			Interlocked.Exchange(ref _sceneDumped, 1) != 0)
		{
			return null;
		}

		var output = new StringBuilder(256 * 1024);
		output.Append("FRAME ").AppendLine(frame.ToString(CultureInfo.InvariantCulture));
		var state = new SceneDumpState();
		DumpPicture(output, picture, Matrix4x4.Identity, state, 0, "root");
		output.AppendLine(FormattableString.Invariant(
			$"FINAL rectClip={state.RectangleClipDepth} geometryClip={state.GeometryClipDepth} opacity={state.OpacityDepth} blend={state.BlendDepth}"));
		File.WriteAllText(path, output.ToString());
		return path;
	}

	private static bool ContainsHostBackdrop(GpuPicture picture) =>
		ContainsHostBackdrop(picture, new HashSet<GpuPicture>());

	private static bool ContainsHostBackdrop(GpuPicture picture, HashSet<GpuPicture> visited)
	{
		if (!visited.Add(picture))
		{
			return false;
		}

		for (var index = 0; index < picture.CommandCount; index++)
		{
			var command = picture.GetCommand(index);
			if (command.Type == RenderCommandType.DrawExtension &&
				command.DataParam is BackdropMaterialParams { Source: ProGPU.Vector.BackdropMaterialSource.HostBackdrop })
			{
				return true;
			}
			if (command.Picture is { } child && ContainsHostBackdrop(child, visited))
			{
				return true;
			}
		}
		return false;
	}

	internal static void AppendCompositorMetrics(string path, in CompositorMetrics metrics)
	{
		File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture,
			$"METRICS frameMs={metrics.FrameTimeMs:F3} compileMs={metrics.VisualTreeCompileTimeMs:F3} uploadMs={metrics.GpuUploadTimeMs:F3} passMs={metrics.RenderPassTimeMs:F3} vectors={metrics.VectorVerticesCount}/{metrics.VectorIndicesCount} text={metrics.TextVerticesCount} masks={metrics.MaskRenderPassCount} maskDraws={metrics.MaskRenderDrawCallCount} generalMaskPeak={metrics.GeneralGeometryMaskPeakDemand} opacityMaskPeak={metrics.OpacityMaskPeakDemand} maskPool={metrics.MaskTexturePoolCount} cacheHit={metrics.SceneCacheHit} cacheMiss={metrics.SceneCacheMissReason}{Environment.NewLine}"));
	}

	private static void DumpPicture(StringBuilder output, GpuPicture picture, Matrix4x4 parent, SceneDumpState state, int depth, string name)
	{
		if (depth > 128)
		{
			output.Append(' ', depth * 2).AppendLine("DEPTH LIMIT");
			return;
		}

		var initialRect = state.RectangleClipDepth;
		var initialGeometry = state.GeometryClipDepth;
		var initialOpacity = state.OpacityDepth;
		var initialBlend = state.BlendDepth;
		output.Append(' ', depth * 2).Append("PICTURE ").Append(name)
			.Append(" commands=").Append(picture.CommandCount)
			.Append(" retained=").Append(picture.RetainedResourceCount)
			.Append(" parent=").AppendLine(Matrix(parent));

		for (var index = 0; index < picture.CommandCount; index++)
		{
			var command = picture.GetCommand(index);
			var local = command.Transform == default ? Matrix4x4.Identity : command.Transform;
			var active = local * parent;
			output.Append(' ', depth * 2 + 2).Append(index).Append(' ').Append(command.Type)
				.Append(" m=").Append(Matrix(active));
			if (!command.Rect.IsEmpty)
			{
				output.Append(" rect=").Append(Rect(command.Rect));
			}
			if (command.Path is { } geometry)
			{
				if (geometry.TryGetBounds(out var minimum, out var maximum))
				{
					output.Append(" path=").Append(Rect(new ProGPU.Scene.Rect(minimum, maximum - minimum)));
				}
				else
				{
					output.Append(" path=empty");
				}
				output.Append(" combined=").Append(geometry.IsCombined);
				if (command.Type is RenderCommandType.DrawPath or RenderCommandType.PushGeometryClip)
				{
					AppendPathDetails(output, geometry);
				}
			}
			if (command.Type == RenderCommandType.DrawGlyphRun)
			{
				output.Append(" glyphs=").Append(command.GlyphRangeCount)
					.Append(" size=").Append(command.FontSize.ToString("F2", CultureInfo.InvariantCulture));
				if (command.GlyphPositions is { Length: > 0 } positions)
				{
					var first = Math.Clamp(command.GlyphRangeStart, 0, positions.Length - 1);
					output.Append(" first=").Append(Point(positions[first] + command.Position));
				}
			}
			if (command.Brush is ProGPU.Vector.SolidColorBrush solid)
			{
				output.Append(" color=").Append(Color(solid.Color))
					.Append(" brushOpacity=").Append(solid.Opacity.ToString("F3", CultureInfo.InvariantCulture));
			}
			if (command.Type == RenderCommandType.DrawExtension && command.DataParam is BackdropMaterialParams backdrop)
			{
				output.Append(" extension=").Append(command.ExtensionId)
					.Append(" rect=").Append(Rect(backdrop.Rect))
					.Append(" backdropKind=").Append(backdrop.Kind)
					.Append(" source=").Append(backdrop.Source)
					.Append(" blur=").Append(backdrop.BlurRadius.ToString("F3", CultureInfo.InvariantCulture))
					.Append(" saturation=").Append(backdrop.Saturation.ToString("F3", CultureInfo.InvariantCulture))
					.Append(" tint=").Append(Color(backdrop.TintColor))
					.Append(" luminosity=").Append(Color(backdrop.LuminosityColor))
					.Append(" opacity=").Append(backdrop.MaterialOpacity.ToString("F3", CultureInfo.InvariantCulture));
			}
			output.AppendLine();

			switch (command.Type)
			{
				case RenderCommandType.PushClip: state.RectangleClipDepth++; break;
				case RenderCommandType.PopClip: state.RectangleClipDepth--; break;
				case RenderCommandType.PushGeometryClip: state.GeometryClipDepth++; break;
				case RenderCommandType.PopGeometryClip: state.GeometryClipDepth--; break;
				case RenderCommandType.PushOpacity: state.OpacityDepth++; break;
				case RenderCommandType.PopOpacity: state.OpacityDepth--; break;
				case RenderCommandType.PushBlendMode: state.BlendDepth++; break;
				case RenderCommandType.PopBlendMode: state.BlendDepth--; break;
				case RenderCommandType.DrawPicture when command.Picture is { } child:
					DumpPicture(output, child, active, state, depth + 1, $"{name}/{index}");
					break;
			}
		}

		if (state.RectangleClipDepth != initialRect || state.GeometryClipDepth != initialGeometry ||
			state.OpacityDepth != initialOpacity || state.BlendDepth != initialBlend)
		{
			output.Append(' ', depth * 2).Append("UNBALANCED ").Append(name)
				.Append(" rect=").Append(state.RectangleClipDepth - initialRect)
				.Append(" geometry=").Append(state.GeometryClipDepth - initialGeometry)
				.Append(" opacity=").Append(state.OpacityDepth - initialOpacity)
				.Append(" blend=").AppendLine((state.BlendDepth - initialBlend).ToString(CultureInfo.InvariantCulture));
		}
	}

	private static void AppendPathDetails(StringBuilder output, ProGPU.Vector.PathGeometry geometry)
	{
		output.Append(" fill=").Append(geometry.FillRule)
			.Append(" op=").Append(geometry.Op)
			.Append(" figures=").Append(geometry.Figures.Count);
		if (geometry.IsCombined)
		{
			AppendCombinedOperand(output, "a", geometry.PathA);
			AppendCombinedOperand(output, "b", geometry.PathB);
			return;
		}

		for (var figureIndex = 0; figureIndex < geometry.Figures.Count; figureIndex++)
		{
			var figure = geometry.Figures[figureIndex];
			output.Append(" f").Append(figureIndex)
				.Append('=').Append(Point(figure.StartPoint))
				.Append(':').Append(figure.IsClosed ? 'C' : 'O').Append(':');
			for (var segmentIndex = 0; segmentIndex < figure.Segments.Count; segmentIndex++)
			{
				if (segmentIndex != 0)
				{
					output.Append(',');
				}
				output.Append(figure.Segments[segmentIndex] switch
				{
					ProGPU.Vector.LineSegment => 'L',
					ProGPU.Vector.QuadraticBezierSegment => 'Q',
					ProGPU.Vector.CubicBezierSegment => 'C',
					ProGPU.Vector.ArcSegment => 'A',
					_ => '?',
				});
			}
		}
	}

	private static void AppendCombinedOperand(StringBuilder output, string name, ProGPU.Vector.PathGeometry? operand)
	{
		output.Append(' ').Append(name).Append('=');
		if (operand is null)
		{
			output.Append("null");
			return;
		}
		output.Append(operand.IsCombined ? "combined" : "simple")
			.Append('/').Append(operand.FillRule)
			.Append('/').Append(operand.Figures.Count);
		if (operand.TryGetBounds(out var minimum, out var maximum))
		{
			output.Append('/').Append(Rect(new ProGPU.Scene.Rect(minimum, maximum - minimum)));
		}
	}

	private static string Matrix(Matrix4x4 value) => FormattableString.Invariant(
		$"[{value.M11:F3},{value.M12:F3},{value.M21:F3},{value.M22:F3},{value.M41:F3},{value.M42:F3}]");

	private static string Rect(ProGPU.Scene.Rect value) => FormattableString.Invariant(
		$"[{value.X:F2},{value.Y:F2},{value.Width:F2},{value.Height:F2}]");

	private static string Point(Vector2 value) => FormattableString.Invariant($"[{value.X:F2},{value.Y:F2}]");

	private static string Color(Vector4 value) => FormattableString.Invariant($"[{value.X:F3},{value.Y:F3},{value.Z:F3},{value.W:F3}]");

	private sealed class SceneDumpState
	{
		internal int RectangleClipDepth;
		internal int GeometryClipDepth;
		internal int OpacityDepth;
		internal int BlendDepth;
	}
}
