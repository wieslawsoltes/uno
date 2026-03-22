#nullable enable

using Windows.Foundation;

namespace Uno.UI.Runtime.Skia.Headless;

public sealed class HeadlessPlatformOptions
{
	public Size WindowSize { get; set; } = new(1024, 768);

	public double RasterizationScale { get; set; } = 1.0;

	public int FrameRate { get; set; } = 60;

	public bool UseSoftwareRenderer { get; set; } = true;

	public bool SupportsMultipleWindows { get; set; } = true;

	internal HeadlessPlatformOptions Clone() => new()
	{
		WindowSize = WindowSize,
		RasterizationScale = RasterizationScale,
		FrameRate = FrameRate,
		UseSoftwareRenderer = UseSoftwareRenderer,
		SupportsMultipleWindows = SupportsMultipleWindows
	};
}
