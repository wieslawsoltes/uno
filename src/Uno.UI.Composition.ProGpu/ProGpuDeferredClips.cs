#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Scene;
using ProGPU.Backend;
using ProGPU.Vector;

namespace Uno.UI.Composition.ProGpu;

// Inverse clips need the render viewport, which is not known while Uno records a subtree.
internal static class ProGpuDeferredClips
{
	private static readonly ConditionalWeakTable<GpuPicture, Source> Sources = new();

	internal static GpuPicture Clone(GpuPicture picture)
	{
		var clone = picture.Clone();
		if (Sources.TryGetValue(picture, out var source))
		{
			Sources.Add(clone, source);
		}
		return clone;
	}

	internal static GpuPicture Finish(GpuPictureRecorder recorder, DrawingContext context)
	{
		var deferred = false;
		foreach (var command in context.Commands)
		{
			if (NeedsPreparation(command))
			{
				deferred = true;
				break;
			}
		}
		if (!deferred)
		{
			return recorder.EndRecording();
		}
		var source = new DrawingContext();
		source.Append(context);
		var entry = new Source(source);
		context.RetainResource(entry);
		var picture = recorder.EndRecording();
		Sources.Add(picture, entry);
		return picture;
	}

	private static bool NeedsPreparation(RenderCommand command) =>
		command.Visual is ProGpuDifferenceVisual ||
		command.Picture is { } picture && Sources.TryGetValue(picture, out _) ||
		command.Visual is ProGpuDrawingSession.EffectPictureVisual effect && Sources.TryGetValue(effect.Picture, out _);

	internal static GpuPicture? Prepare(GpuPicture picture, Rect viewport, Matrix4x4 parentTransform)
	{
		if (!Sources.TryGetValue(picture, out var source))
		{
			return null;
		}
		lock (source)
		{
			if (source.Prepared is not null && source.Viewport == viewport && source.Transform == parentTransform)
			{
				return source.Prepared.Clone();
			}
			var prepared = PrepareCore(source.Context, viewport, parentTransform);
			source.Prepared?.Dispose();
			source.Prepared = prepared;
			source.Viewport = viewport;
			source.Transform = parentTransform;
			return prepared.Clone();
		}
	}

	private static GpuPicture PrepareCore(DrawingContext source, Rect viewport, Matrix4x4 parentTransform)
	{
		var recorder = new GpuPictureRecorder();
		var context = recorder.BeginRecording(viewport);
		try
		{
			context.Append(source);
			var clips = new Stack<Rect>();
			var effectiveViewport = viewport;
			for (var i = 0; i < context.Commands.Count; i++)
			{
				var command = context.Commands[i];
				var transform = (command.Transform == default ? Matrix4x4.Identity : command.Transform) * parentTransform;
				if (command.Type is RenderCommandType.PushClip or RenderCommandType.PushGeometryClip or RenderCommandType.PushOpacityMask)
				{
					clips.Push(effectiveViewport);
					var bounds = command.Rect;
					if (command.Type == RenderCommandType.PushGeometryClip && command.Path is { } path && path.TryGetBounds(out var min, out var max))
					{
						bounds = new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
					}
					effectiveViewport = ProGpuDrawingSession.Intersect(effectiveViewport, ProGpuDrawingSession.TransformBounds(bounds, transform));
				}
				else if (command.Type is RenderCommandType.PopClip or RenderCommandType.PopGeometryClip or RenderCommandType.PopOpacityMask)
				{
					effectiveViewport = clips.Pop();
				}
				if (command.Visual is ProGpuDifferenceVisual inverse)
				{
					var child = Prepare(inverse.Picture, effectiveViewport, transform);
					if (child is not null)
					{
						context.RetainResource(child);
					}
					var maskRecorder = new GpuPictureRecorder();
					var maskContext = maskRecorder.BeginRecording(effectiveViewport);
					if (Matrix4x4.Invert(transform, out var inverseTransform))
					{
						var bounds = ProGpuDrawingSession.TransformBounds(effectiveViewport, inverseTransform);
						var coverageRecorder = new GpuPictureRecorder();
						var coverage = coverageRecorder.BeginRecording(bounds);
						coverage.DrawRectangle(new SolidColorBrush(Vector4.One), null, bounds, Matrix4x4.Identity);
						coverage.PushBlendMode(GpuBlendMode.DstOut);
						coverage.DrawPath(new SolidColorBrush(Vector4.One), null, inverse.Excluded, Matrix4x4.Identity);
						coverage.PopBlendMode();
						var coveragePicture = coverageRecorder.EndRecording();
						maskContext.RetainResource(coveragePicture);
						maskContext.PushOpacityMask(coveragePicture, bounds);
						maskContext.DrawPicture(child ?? inverse.Picture);
						maskContext.PopOpacityMask();
					}
					var replacement = maskRecorder.EndRecording();
					context.RetainResource(replacement);
					command.Type = RenderCommandType.DrawPicture;
					command.Visual = null;
					command.Picture = replacement;
				}
				else if (command.Picture is { } nested && Prepare(nested, effectiveViewport, transform) is { } prepared)
				{
					context.RetainResource(prepared);
					command.Picture = prepared;
				}
				else if (command.Visual is ProGpuDrawingSession.EffectPictureVisual effect)
				{
					if (!Matrix4x4.Invert(transform, out var inverseTransform))
					{
						continue;
					}
					var localViewport = ProGpuDrawingSession.TransformBounds(effectiveViewport, inverseTransform);
					var sourceBounds = ProGpuDrawingSession.Intersect(effect.EffectContentBounds!.Value, EffectInputBounds(localViewport, effect.Effect!));
					var expanded = ProGpuDrawingSession.TransformBounds(sourceBounds, transform);
					if (Prepare(effect.Picture, expanded, transform) is { } preparedEffect)
					{
						context.RetainResource(preparedEffect);
						command.Visual = new ProGpuDrawingSession.EffectPictureVisual(preparedEffect, effect.Effect!, sourceBounds);
					}
				}
				context.Commands[i] = command;
			}
			return recorder.EndRecording();
		}
		catch
		{
			context.Clear();
			throw;
		}
	}

	private static Rect EffectInputBounds(Rect viewport, EffectBase effect)
	{
		var x = effect is BlurEffect blur ? MathF.Ceiling(MathF.Max(0, blur.BlurRadius) * 2) : 0;
		var y = x;
		if (effect is DropShadowEffect shadow)
		{
			x = MathF.Ceiling(MathF.Max(0, shadow.BlurRadiusX) * 2) + MathF.Abs(shadow.Offset.X);
			y = MathF.Ceiling(MathF.Max(0, shadow.BlurRadiusY) * 2) + MathF.Abs(shadow.Offset.Y);
		}
		return new Rect(viewport.X - x, viewport.Y - y, viewport.Width + x * 2, viewport.Height + y * 2);
	}

	private sealed class Source(DrawingContext context) : IDisposable
	{
		internal DrawingContext Context { get; } = context;
		internal GpuPicture? Prepared;
		internal Rect Viewport;
		internal Matrix4x4 Transform;

		public void Dispose()
		{
			lock (this)
			{
				Prepared?.Dispose();
				Prepared = null;
				Context.Clear();
			}
		}
	}

}

internal sealed class ProGpuDifferenceVisual(GpuPicture picture, PathGeometry excluded) : Visual
{
	internal GpuPicture Picture { get; } = picture;
	internal PathGeometry Excluded { get; } = excluded;
	public override void OnRender(DrawingContext context) => throw new InvalidOperationException("An inverse clip must be resolved against the target viewport before rendering.");
}
