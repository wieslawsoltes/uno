#nullable enable

using Microsoft.UI.Xaml;
using Uno.UI.Xaml;

namespace Uno.UI.Runtime.Skia.Headless;

public static class HeadlessWindowFactory
{
	public static Window CreateWindow()
		=> new(WindowType.DesktopXamlSource);

	public static Window CreateAndActivateWindow()
	{
		var window = CreateWindow();
		window.Activate();
		return window;
	}
}
