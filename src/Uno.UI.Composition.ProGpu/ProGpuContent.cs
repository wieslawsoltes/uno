extern alias unouwp;

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Uno.UI.Composition.Drawing;
using BitmapAlphaMode = unouwp::Windows.Graphics.Imaging.BitmapAlphaMode;
using BitmapEncoderFormat = unouwp::Uno.UI.Composition.Drawing.BitmapEncoderFormat;
using BitmapPixelFormat = unouwp::Windows.Graphics.Imaging.BitmapPixelFormat;

namespace Uno.UI.Composition.ProGpu;

/// <summary>
/// Skia-free neutral image codec for the ProGPU stack. Decoding and encoding
/// remain CPU content operations by Uno's contract; GPU upload/residency is
/// owned by <see cref="ProGpuDrawingFactory"/>.
/// </summary>
public sealed class ProGpuImageEncoderDecoder : IImageEncoderDecoder
{
	private readonly ManagedImageDecoderBackend _codec = new();

	public bool TryDecode(Stream stream, int? targetWidth, int? targetHeight, [NotNullWhen(true)] out ImageFrames? frames) =>
		_codec.TryDecode(stream, targetWidth, targetHeight, out frames);

	public IImage CreateImage(int pixelWidth, int pixelHeight, ReadOnlySpan<byte> bgraPremul) =>
		_codec.CreateImage(pixelWidth, pixelHeight, bgraPremul);

	public ImageFrames CreateFrames(IImage image) => _codec.CreateFrames(image);

	public void Encode(Stream destination, byte[] pixels, int width, int height, BitmapPixelFormat pixelFormat, BitmapAlphaMode alphaMode, BitmapEncoderFormat format, int quality) =>
		_codec.Encode(destination, pixels, width, height, pixelFormat, alphaMode, format, quality);
}

/// <summary>
/// Parses SVG through Uno's backend-neutral managed document engine while
/// forcing all geometry, gradients, images, and replay through the registered
/// ProGPU factories. No raster or Skia SVG surface is introduced.
/// </summary>
public sealed class ProGpuSvgRenderer : ISvgRenderer
{
	private readonly ProGpuFontProvider _fonts;
	private readonly ManagedSvgRenderer _renderer = new();

	public ProGpuSvgRenderer(ProGpuFontProvider fonts) =>
		_fonts = fonts ?? throw new ArgumentNullException(nameof(fonts));

	public ISvgDocument? Parse(byte[] svg, IGeometryFactory geometry, IDrawingFactory drawing)
	{
		ArgumentNullException.ThrowIfNull(svg);
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(drawing);
		_ = _fonts; // retained as the SVG text/fallback authority for the typed fast path.
		return _renderer.Parse(svg, geometry, drawing);
	}
}
