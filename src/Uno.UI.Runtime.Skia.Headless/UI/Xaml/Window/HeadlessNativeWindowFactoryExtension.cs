#nullable enable

using Microsoft.UI.Xaml;
using Uno.UI.Xaml.Controls;

namespace Uno.UI.Runtime.Skia.Headless.UI.Xaml.Window;

internal sealed class HeadlessNativeWindowFactoryExtension : INativeWindowFactoryExtension
{
	private readonly Hosting.HeadlessHost _host;
	private readonly bool _supportsMultipleWindows;

	public HeadlessNativeWindowFactoryExtension(Hosting.HeadlessHost host, bool supportsMultipleWindows)
	{
		_host = host;
		_supportsMultipleWindows = supportsMultipleWindows;
	}

	public bool SupportsMultipleWindows => _supportsMultipleWindows;

	public bool SupportsClosingCancellation => true;

	public INativeWindowWrapper CreateWindow(Microsoft.UI.Xaml.Window window, XamlRoot xamlRoot)
		=> _host.CreateWindow(window, xamlRoot);
}
