#nullable enable

using Uno.ApplicationModel.Core;

namespace Uno.UI.Runtime.Skia.Headless.ApplicationModel.Core;

internal sealed class HeadlessCoreApplicationExtension : ICoreApplicationExtension
{
	private readonly Hosting.HeadlessHost _host;

	public HeadlessCoreApplicationExtension(Hosting.HeadlessHost host)
	{
		_host = host;
	}

	public bool CanExit => true;

	public void Exit() => _host.RequestExit();
}
