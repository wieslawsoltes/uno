#nullable enable

using Microsoft.UI.Xaml;
using Private.Infrastructure;
using Uno.UI.Runtime.Skia.Headless.Testing;
using Xunit;

namespace Uno.UI.Runtime.Skia.Headless.XUnit;

public abstract class UnoHeadlessTestBase : IAsyncLifetime
{
	private readonly UnoHeadlessTestSession _session;

	protected UnoHeadlessTestBase()
	{
		_session = UnoHeadlessTestSession.GetOrStartForAssembly(GetType().Assembly);
	}

	protected Window CurrentWindow => TestServices.WindowHelper.CurrentTestWindow;

	protected Task RunOnUIThreadAsync(Action action, CancellationToken cancellationToken = default)
		=> RunOnUIThreadAsync(() =>
		{
			action();
			return Task.CompletedTask;
		}, cancellationToken);

	protected Task<T> RunOnUIThreadAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
		=> RunOnUIThreadAsync(() => Task.FromResult(action()), cancellationToken);

	protected Task RunOnUIThreadAsync(Func<Task> action, CancellationToken cancellationToken = default)
		=> RunOnUIThreadAsync(async () =>
		{
			await action();
			return true;
		}, cancellationToken);

	protected Task<T> RunOnUIThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
		=> _session.DispatchAsync(async () =>
		{
			PrepareCurrentWindow();
			return await action();
		}, cancellationToken);

	protected Task<Window> CreateAdditionalWindowAsync(CancellationToken cancellationToken = default)
		=> RunOnUIThreadAsync(() =>
		{
			var window = HeadlessWindowFactory.CreateAndActivateWindow();
			TestServices.WindowHelper.Initialize(window);
			return window;
		}, cancellationToken);

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	public ValueTask DisposeAsync() => new(_session.ResetStateAsync());

	private static void PrepareCurrentWindow()
	{
		var window = Window.Current ?? throw new InvalidOperationException("No current Uno window is available for the headless xUnit test.");
		TestServices.WindowHelper.Initialize(window);
	}
}
