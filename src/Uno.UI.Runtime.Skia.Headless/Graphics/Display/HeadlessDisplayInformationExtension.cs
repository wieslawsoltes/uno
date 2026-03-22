#nullable enable

using Microsoft.UI;
using Windows.Graphics.Display;

namespace Uno.UI.Runtime.Skia.Headless.Graphics.Display;

internal sealed class HeadlessDisplayInformationExtension : IDisplayInformationExtension
{
	private readonly Hosting.HeadlessHost _host;
	private readonly DisplayInformation _owner;

	public HeadlessDisplayInformationExtension(Hosting.HeadlessHost host, DisplayInformation owner)
	{
		_host = host;
		_owner = owner;
	}

	private UI.Xaml.Window.HeadlessWindowWrapper? TryGetWrapper()
		=> _host.TryGetWindow(_owner.WindowId, out var wrapper) ? wrapper : null;

	public DisplayOrientations CurrentOrientation => DisplayOrientations.Landscape;

	public uint ScreenHeightInRawPixels
		=> (uint)(TryGetWrapper()?.Size.Height ?? (int)(_host.Options.WindowSize.Height * _host.Options.RasterizationScale));

	public uint ScreenWidthInRawPixels
		=> (uint)(TryGetWrapper()?.Size.Width ?? (int)(_host.Options.WindowSize.Width * _host.Options.RasterizationScale));

	public float LogicalDpi => (float)(DisplayInformation.BaseDpi * RawPixelsPerViewPixel);

	public double RawPixelsPerViewPixel => TryGetWrapper()?.RasterizationScale ?? _host.Options.RasterizationScale;

	public ResolutionScale ResolutionScale => (ResolutionScale)(int)(RawPixelsPerViewPixel * 100.0);

	public double? DiagonalSizeInInches => null;
}
