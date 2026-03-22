#nullable enable

using Uno.UI.Hosting;

namespace Uno.UI.Runtime.Skia.Headless.Builder;

public sealed class HeadlessHostBuilder : IPlatformHostBuilder
{
	private readonly HeadlessPlatformOptions _options;

	internal HeadlessHostBuilder(HeadlessPlatformOptions options)
	{
		_options = options.Clone();
	}

	bool IPlatformHostBuilder.IsSupported => true;

	UnoPlatformHost IPlatformHostBuilder.Create(Func<Microsoft.UI.Xaml.Application> appBuilder, Type applicationType)
		=> new Hosting.HeadlessHost(appBuilder, applicationType, _options);
}
