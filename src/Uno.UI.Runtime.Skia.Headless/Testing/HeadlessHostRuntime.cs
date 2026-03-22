#nullable enable

using Microsoft.UI.Xaml;
using Uno.UI.Runtime.Skia.Headless.Hosting;

namespace Uno.UI.Runtime.Skia.Headless.Testing;

internal sealed class HeadlessHostRuntime : IAsyncDisposable
{
	private readonly HeadlessHost _host;
	private readonly Task _runTask;

	private HeadlessHostRuntime(HeadlessHost host, Task runTask)
	{
		_host = host;
		_runTask = runTask;
	}

	public static async Task<HeadlessHostRuntime> StartNewAsync(Type applicationType, HeadlessPlatformOptions options, CancellationToken cancellationToken = default)
	{
		var host = new HeadlessHost(
			() => (Application)Activator.CreateInstance(applicationType)!,
			applicationType,
			options);

		var runTask = Task.Run(() => host.RunAsync(), CancellationToken.None);
		using var registration = cancellationToken.Register(host.RequestExit);
		await host.Initialized.WaitAsync(cancellationToken);
		return new HeadlessHostRuntime(host, runTask);
	}

	public Task DispatchAsync(Action action, CancellationToken cancellationToken = default)
		=> _host.DispatchAsync(action, cancellationToken);

	public Task<TResult> DispatchAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default)
		=> _host.DispatchAsync(action, cancellationToken);

	public Task ResetStateAsync() => _host.ResetStateAsync();

	public async ValueTask DisposeAsync()
	{
		_host.RequestExit();
		await _runTask;
		_host.Dispose();
	}
}
