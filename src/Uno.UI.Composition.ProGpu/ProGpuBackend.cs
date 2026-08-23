#nullable enable

using System;
using Uno.UI.Composition.Drawing;
using Uno.UI.Hosting;

namespace Uno.UI.Composition.ProGpu;

/// <summary>
/// Registers the complete ProGPU drawing and content stack through Uno's
/// public backend seams.
/// </summary>
public static class ProGpuBackend
{
	public static IUnoPlatformHostBuilder UseProGpuDrawingBackend(
		this IUnoPlatformHostBuilder builder,
		ProGpuBackendOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		options ??= new ProGpuBackendOptions();

		var geometry = new ProGpuGeometryFactory();
		var fonts = new ProGpuFontProvider(options.FontManager);
		var images = new ProGpuImageEncoderDecoder();

		builder.GraphicsBackend(new ProGpuGraphicsProvider(options));
		builder.GeometryFactory(geometry);
		builder.FontProvider(fonts);
		builder.ImageEncoderDecoder(images);
		builder.SvgRenderer(new ProGpuSvgRenderer(fonts));
		return builder;
	}
}

public sealed class ProGpuBackendOptions
{
	/// <summary>
	/// Gets the bounded number of queue submissions ProGPU may keep in flight
	/// before forcing a blocking queue drain.
	/// </summary>
	/// <remarks>
	/// Uno's presentation loop already polls the WebGPU device every frame.
	/// A wider safety window avoids serializing CPU and GPU work during bursts
	/// while retaining a finite resource-residency bound.
	/// </remarks>
	public int MaximumDeferredQueueSubmissions { get; init; } = 64;

	public ProGPU.Text.FontManager FontManager { get; init; } = ProGPU.Text.FontManager.Default;

	public ProGPU.Scene.CompositorOptions Compositor { get; init; } =
		ProGPU.Scene.CompositorOptions.Default with
		{
			PrimarySampleCount = 1,
			EnableGpuHitTesting = false,
			EnableIncrementalScenePages = true,
		};

	public bool PreferGlyphAtlas { get; init; } = true;

	public bool FailOnUnsupportedOperation { get; init; } = true;
}
