#nullable enable

using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Headless.Builder;
using Uno.UI.Runtime.Skia.Headless;

namespace Uno.UI.Hosting;

public static class HeadlessHostBuilderExtensions
{
	public static IUnoPlatformHostBuilder UseHeadless(this IUnoPlatformHostBuilder builder, Action<HeadlessPlatformOptions>? configure = null)
	{
		var options = new HeadlessPlatformOptions();
		configure?.Invoke(options);

		builder.AddHostBuilder(() => new HeadlessHostBuilder(options));
		return builder;
	}
}
