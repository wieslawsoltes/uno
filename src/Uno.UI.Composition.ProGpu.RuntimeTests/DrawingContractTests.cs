extern alias unofoundation;
extern alias unouwp;

#nullable enable

using System;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using ProGPU.Backend;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using Color = unouwp::Windows.UI.Color;
using Rect = unofoundation::Windows.Foundation.Rect;

internal static class DrawingContractTests
{
	public static async Task Run(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		await RetainedTextureSurvivesCreatorDisposal(factory);
		await TintLayerPreservesCoverage(factory);
		await TiledImagesHonorIndependentAxes(factory);
		await LargeTiledImageRecordingIsBounded(factory);
		await BatchedPathsPreserveState(factory, geometries);
		Console.WriteLine("Drawing contracts passed: retained texture lifetime, tint alpha, tiled edges, and batched paths.");
	}

	private static async Task RetainedTextureSurvivesCreatorDisposal(ProGpuDrawingFactory factory)
	{
		var pixels = new byte[4 * 4 * 4];
		for (var i = 0; i < pixels.Length; i += 4)
		{
			pixels[i + 2] = 255;
			pixels[i + 3] = 255;
		}
		var source = factory.CreateTexture(4, 4, pixels);
		var native = (GpuTexture)source.GetType().GetProperty("Texture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
		var childRecorder = factory.CreateRecording();
		childRecorder.DrawImage(source, 4, 4);
		using var tint = factory.CreateTintColorFilter(Color.FromArgb(255, 0, 255, 0));
		childRecorder.DrawImage(source, 12, 4, tint);
		source.Dispose();
		source.Dispose();
		if (native.IsDisposed)
		{
			throw new InvalidOperationException("Recording must retain its image after creator Dispose, including repeated Dispose.");
		}
		using var child = childRecorder.Finish();
		var parentRecorder = factory.CreateRecording();
		child.Replay(parentRecorder);
		using var parent = parentRecorder.Finish();
		child.Dispose();
		using (var output = factory.RenderOffscreen(24, 16, drawing => parent.Replay(drawing)))
		using (var image = await factory.SnapshotAsync(output))
		{
			var actual = new byte[24 * 16 * 4];
			image.CopyPixels(actual);
			AssertPixel(actual, 24, 5, 5, 0, 0, 255, 255, "nested retained image");
			AssertPixel(actual, 24, 13, 5, 0, 255, 0, 255, "nested tinted image");
		}
		parent.Dispose();
		factory.WaitForGpuCompletion();
		if (!native.IsDisposed)
		{
			throw new InvalidOperationException("The final recording reference must release its source texture.");
		}
	}

	private static async Task TintLayerPreservesCoverage(ProGpuDrawingFactory factory)
	{
		using var filter = factory.CreateTintColorFilter(Color.FromArgb(128, 0, 255, 0));
		using var output = factory.RenderOffscreen(24, 16, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.SaveLayer(filter);
			drawing.DrawRect(new Rect(4, 4, 12, 8), Color.FromArgb(128, 255, 0, 0));
			drawing.Restore();
		});
		using var image = await factory.SnapshotAsync(output);
		var actual = new byte[24 * 16 * 4];
		image.CopyPixels(actual);
		AssertPixel(actual, 24, 8, 8, 0, 64, 0, 64, "layer tint times source coverage");
		AssertPixel(actual, 24, 1, 1, 0, 0, 0, 0, "outside tint layer");
	}

	private static async Task TiledImagesHonorIndependentAxes(ProGpuDrawingFactory factory)
	{
		var pixels = new byte[8 * 8 * 4];
		for (var y = 0; y < 8; y++)
		{
			for (var x = 0; x < 8; x++)
			{
				var offset = (y * 8 + x) * 4;
				pixels[offset + (x < 4 ? 2 : 0)] = 255;
				pixels[offset + 1] = y < 4 ? (byte)0 : (byte)128;
				pixels[offset + 3] = 255;
			}
		}
		using var source = factory.CreateTexture(8, 8, pixels);
		foreach (var extendX in Enum.GetValues<EdgeExtend>())
		{
			foreach (var extendY in Enum.GetValues<EdgeExtend>())
			{
				using var output = factory.RenderOffscreen(32, 32, drawing =>
				{
					drawing.Clear(Color.FromArgb(0, 0, 0, 0));
					drawing.DrawImageTiled(source, new Rect(4, 4, 24, 24), extendX, extendY);
				});
				using var image = await factory.SnapshotAsync(output);
				var actual = new byte[32 * 32 * 4];
				image.CopyPixels(actual);
				AssertPixel(actual, 32, 6, 6, 0, 0, 255, 255, $"{extendX}/{extendY} source");
				var red = extendX is EdgeExtend.Wrap;
				var green = extendY is EdgeExtend.Clamp or EdgeExtend.Mirror;
				var visible = extendX != EdgeExtend.None && extendY != EdgeExtend.None;
				AssertPixel(actual, 32, 14, 14,
					visible && !red ? 255 : 0, visible && green ? 128 : 0,
					visible && red ? 255 : 0, visible ? 255 : 0, $"{extendX}/{extendY} extension");
				var visibleX = extendX != EdgeExtend.None;
				AssertPixel(actual, 32, 14, 6,
					visibleX && !red ? 255 : 0, 0,
					visibleX && red ? 255 : 0, visibleX ? 255 : 0, $"{extendX}/{extendY} X-only extension");
				var visibleY = extendY != EdgeExtend.None;
				AssertPixel(actual, 32, 6, 14,
					0, visibleY && green ? 128 : 0,
					visibleY ? 255 : 0, visibleY ? 255 : 0, $"{extendX}/{extendY} Y-only extension");
				AssertPixel(actual, 32, 1, 1, 0, 0, 0, 0, $"{extendX}/{extendY} destination clip");
			}
		}

		using var transformed = factory.RenderOffscreen(48, 40, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.Translate(4, 3);
			drawing.Scale(2, 2);
			drawing.DrawImageTiled(source, new Rect(3.25, 2.5, 13.25, 11.5), EdgeExtend.Mirror, EdgeExtend.Wrap);
		});
		using var transformedImage = await factory.SnapshotAsync(transformed);
		var transformedPixels = new byte[48 * 40 * 4];
		transformedImage.CopyPixels(transformedPixels);
		AssertPixel(transformedPixels, 48, 31, 27, 255, 0, 0, 255, "transformed mirrored tile with wrapped Y");
		AssertPixel(transformedPixels, 48, 35, 27, 0, 0, 255, 255, "transformed fractional final tile");
		AssertPixel(transformedPixels, 48, 38, 27, 0, 0, 0, 0, "after transformed final column");
		AssertPixel(transformedPixels, 48, 31, 33, 0, 0, 0, 0, "after transformed final row");
		AssertPixel(transformedPixels, 48, 8, 12, 0, 0, 0, 0, "before transformed tile origin");
	}

	public static async Task AssertBlendBackdropUnsupported(ProGpuDrawingFactory factory)
	{
		var unsupportedBefore = ProGpuDiagnostics.UnsupportedOperationCount;
		using var filter = factory.CreateEffectFilter(
			new BlendEffectNode(new ColorInput(Color.FromArgb(255, 128, 255, 64)), new SourceInput(), BlendMode.Multiply),
			new Rect(4, 4, 24, 16)) ?? throw new InvalidOperationException("The ProGPU backend rejected a constant-background blend graph.");
		using var output = factory.RenderOffscreen(48, 32, drawing =>
		{
			drawing.DrawRect(new Rect(0, 0, 24, 32), Color.FromArgb(255, 255, 0, 0));
			drawing.DrawRect(new Rect(24, 0, 24, 32), Color.FromArgb(255, 0, 0, 255));
			var depth = drawing.Save();
			drawing.Translate(6, 4);
			var matrix = drawing.TotalMatrix;
			var saveCount = drawing.SaveCount;
			var rejected = false;
			try
			{
				drawing.DrawEffectBackdrop(filter, 0.5f);
			}
			catch (NotSupportedException exception) when (exception.Message == "The ProGPU backend does not support drawing operation 'DrawEffectBackdrop.BlendGraph'.")
			{
				rejected = true;
			}
			if (!rejected || drawing.TotalMatrix != matrix || drawing.SaveCount != saveCount)
			{
				throw new InvalidOperationException("Unsupported live blend backdrops must fail explicitly without changing drawing state.");
			}
			drawing.RestoreToCount(depth);
		});
		using var image = await factory.SnapshotAsync(output);
		var actual = new byte[48 * 32 * 4];
		image.CopyPixels(actual);
		AssertPixel(actual, 48, 14, 12, 0, 0, 255, 255, "red backdrop unchanged after unsupported operation");
		AssertPixel(actual, 48, 30, 12, 255, 0, 0, 255, "blue backdrop unchanged after unsupported operation");
		AssertPixel(actual, 48, 8, 12, 0, 0, 255, 255, "left of translated effect bounds");
		AssertPixel(actual, 48, 36, 12, 255, 0, 0, 255, "right of translated effect bounds");
		AssertPixel(actual, 48, 14, 6, 0, 0, 255, 255, "above translated effect bounds");
		AssertPixel(actual, 48, 30, 26, 255, 0, 0, 255, "below translated effect bounds");
		if (ProGpuDiagnostics.UnsupportedOperationCount != unsupportedBefore + 1)
		{
			throw new InvalidOperationException("The deliberate unsupported blend-backdrop probe must record exactly one diagnostic.");
		}
		Console.WriteLine("Unsupported live blend-backdrop probe passed: explicit failure, one diagnostic, and unchanged drawing state/pixels.");
	}

	private static async Task LargeTiledImageRecordingIsBounded(ProGpuDrawingFactory factory)
	{
		using var source = factory.CreateTexture(1, 1, [0, 0, 128, 128]);
		var warmup = factory.CreateRecording();
		warmup.DrawImageTiled(source, new Rect(0, 0, 8, 8), EdgeExtend.Wrap, EdgeExtend.Mirror, 0.5f);
		using (warmup.Finish()) { }

		var before = GC.GetAllocatedBytesForCurrentThread();
		var recorder = factory.CreateRecording();
		recorder.DrawImageTiled(source, new Rect(0, 0, 3840, 2160), EdgeExtend.Wrap, EdgeExtend.Mirror, 0.5f);
		using var record = recorder.Finish();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		if (allocated >= 1024 * 1024)
		{
			throw new InvalidOperationException($"A one-pixel texture tiled across 4K must record within 1 MiB; allocated {allocated:N0} bytes.");
		}

		using var output = factory.RenderOffscreen(40, 24, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.Translate(4, 3);
			drawing.Scale(1f / 120f, 1f / 120f);
			record.Replay(drawing);
		});
		using var image = await factory.SnapshotAsync(output);
		var actual = new byte[40 * 24 * 4];
		image.CopyPixels(actual);
		AssertPixel(actual, 40, 20, 10, 0, 0, 64, 64, "large tiling preserves premultiplied alpha and opacity");
		AssertPixel(actual, 40, 38, 10, 0, 0, 0, 0, "large tiling transformed destination clip");
		Console.WriteLine($"4K one-pixel tiling recording allocated {allocated:N0} bytes.");
	}

	private static async Task BatchedPathsPreserveState(ProGpuDrawingFactory factory, ProGpuGeometryFactory geometries)
	{
		var builder = geometries.CreatePrimitiveGeometryBuilder();
		builder.AddRectangle(new Rect(0, 0, 4, 4));
		using var geometry = builder.Build();
		using var output = factory.RenderOffscreen(32, 16, drawing =>
		{
			drawing.Clear(Color.FromArgb(0, 0, 0, 0));
			drawing.Translate(2, 2);
			var matrix = drawing.TotalMatrix;
			var depth = drawing.SaveCount;
			drawing.DrawPaths([new PathInstance(geometry, new Vector2(2, 2)), new PathInstance(geometry, new Vector2(10, 2))], Color.FromArgb(255, 255, 0, 0));
			if (drawing.TotalMatrix != matrix || drawing.SaveCount != depth)
			{
				throw new InvalidOperationException("DrawPaths must preserve the caller's matrix and save depth.");
			}
		});
		using var image = await factory.SnapshotAsync(output);
		var actual = new byte[32 * 16 * 4];
		image.CopyPixels(actual);
		AssertPixel(actual, 32, 5, 5, 0, 0, 255, 255, "first translated path");
		AssertPixel(actual, 32, 13, 5, 0, 0, 255, 255, "second translated path");
		AssertPixel(actual, 32, 9, 5, 0, 0, 0, 0, "between translated paths");
	}

	private static void AssertPixel(byte[] pixels, int width, int x, int y, int b, int g, int r, int a, string scenario)
	{
		var actual = pixels.AsSpan((y * width + x) * 4, 4);
		if (Math.Abs(actual[0] - b) > 4 || Math.Abs(actual[1] - g) > 4 || Math.Abs(actual[2] - r) > 4 || Math.Abs(actual[3] - a) > 4)
		{
			throw new InvalidOperationException($"{scenario}: {Convert.ToHexString(actual)}, expected BGRA ({b}, {g}, {r}, {a}).");
		}
	}
}
