#nullable enable

using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Uno.UI.Runtime.Skia.Headless;

internal sealed class HeadlessApplicationViewExtension : IApplicationViewExtension
{
	private readonly Hosting.HeadlessHost _host;
	private readonly ApplicationView _owner;

	public HeadlessApplicationViewExtension(Hosting.HeadlessHost host, ApplicationView owner)
	{
		_host = host;
		_owner = owner;
	}

	public bool TryResizeView(Size size)
	{
		if (!_host.TryGetWindow(_owner.WindowId, out var wrapper))
		{
			return false;
		}

		var scale = Math.Max(wrapper.RasterizationScale, 1f);
		wrapper.Resize(new Windows.Graphics.SizeInt32(
			(int)Math.Ceiling(size.Width * scale),
			(int)Math.Ceiling(size.Height * scale)));
		return true;
	}
}
