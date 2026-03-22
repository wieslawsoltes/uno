#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Uno.ApplicationModel.Core;
using Uno.Foundation.Extensibility;
using Uno.Helpers;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Headless.ApplicationModel.Core;
using Uno.UI.Runtime.Skia.Headless.Graphics.Display;
using Uno.UI.Runtime.Skia.Headless.UI.Xaml.Window;
using Uno.UI.Xaml.Controls;
using Windows.ApplicationModel.Core;
using Windows.Graphics.Display;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace Uno.UI.Runtime.Skia.Headless.Hosting;

internal sealed class HeadlessHost : SkiaHost, ISkiaApplicationHost, IDisposable
{
	[ThreadStatic]
	private static bool _isDispatcherThread;

	private readonly EventLoop _eventLoop = new();
	private readonly Func<Application> _appBuilder;
	private readonly Type _applicationType;
	private readonly ManualResetEventSlim _shutdownGate = new(false);
	private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly HeadlessCoreApplicationExtension _coreApplicationExtension;
	private readonly ConcurrentDictionary<WindowId, HeadlessWindowWrapper> _windows = new();

	private bool _disposed;
	private WindowId? _activeWindowId;

	internal HeadlessHost(Func<Application> appBuilder, Type applicationType, HeadlessPlatformOptions options)
	{
		_appBuilder = appBuilder;
		_applicationType = applicationType;
		Options = options.Clone();
		_coreApplicationExtension = new HeadlessCoreApplicationExtension(this);
	}

	internal HeadlessPlatformOptions Options { get; }

	internal Task Initialized => _initialized.Task;

	protected override void Initialize()
	{
		_eventLoop.Schedule(InitializeCore);
		_initialized.Task.GetAwaiter().GetResult();
	}

	protected override Task RunLoop()
	{
		_shutdownGate.Wait();
		return Task.CompletedTask;
	}

	internal void Post(Action action)
	{
		if (_disposed)
		{
			return;
		}

		_eventLoop.Schedule(action);
	}

	internal Task DispatchAsync(Action action, CancellationToken cancellationToken = default)
		=> DispatchAsync<object?>(() =>
		{
			action();
			return Task.FromResult<object?>(null);
		}, cancellationToken);

	internal Task<TResult> DispatchAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(HeadlessHost));
		}

		var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() => _ = ExecuteAsync());

		return tcs.Task;

		async Task ExecuteAsync()
		{
			if (cancellationToken.IsCancellationRequested)
			{
				tcs.TrySetCanceled(cancellationToken);
				return;
			}

			try
			{
				tcs.TrySetResult(await action());
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				tcs.TrySetCanceled(cancellationToken);
			}
			catch (Exception error)
			{
				tcs.TrySetException(error);
			}
		}
	}

	internal HeadlessWindowWrapper CreateWindow(Window window, XamlRoot xamlRoot)
	{
		if (!Options.SupportsMultipleWindows && !_windows.IsEmpty)
		{
			throw new InvalidOperationException("Headless platform is configured for a single window.");
		}

		var wrapper = new HeadlessWindowWrapper(window, xamlRoot, this, Options);
		_windows[window.AppWindow.Id] = wrapper;
		XamlRootMap.Register(xamlRoot, wrapper);
		return wrapper;
	}

	internal bool TryGetWindow(WindowId windowId, out HeadlessWindowWrapper wrapper)
		=> _windows.TryGetValue(windowId, out wrapper!);

	internal IReadOnlyCollection<HeadlessWindowWrapper> GetWindows()
		=> _windows.Values.ToArray();

	internal void BringToFront(HeadlessWindowWrapper wrapper)
	{
		if (_activeWindowId is null)
		{
			ActivateWindow(wrapper);
		}
	}

	internal void ActivateWindow(HeadlessWindowWrapper wrapper)
	{
		foreach (var candidate in _windows.Values)
		{
			candidate.SetActivationState(candidate == wrapper);
		}

		_activeWindowId = wrapper.ManagedWindow.AppWindow.Id;
	}

	internal void CloseWindow(HeadlessWindowWrapper wrapper)
	{
		var window = wrapper.ManagedWindow;
		var xamlRoot = wrapper.ManagedXamlRoot;

		_windows.TryRemove(window.AppWindow.Id, out _);
		XamlRootMap.Unregister(xamlRoot);
		wrapper.Dispose();

		if (_activeWindowId == window.AppWindow.Id)
		{
			_activeWindowId = null;
			if (_windows.Values.FirstOrDefault() is { } next)
			{
				ActivateWindow(next);
			}
		}
	}

	internal async Task ResetStateAsync()
	{
		await DispatchAsync(() =>
		{
			var windows = Uno.UI.ApplicationHelper.WindowsInternal.ToArray();
			var primaryWindow = Window.InitialWindow ?? windows.FirstOrDefault();

			foreach (var window in windows)
			{
				if (window.RootElement?.XamlRoot is { } xamlRoot)
				{
					Microsoft.UI.Xaml.Media.VisualTreeHelper.CloseAllPopups(xamlRoot);
				}

				if (window != primaryWindow)
				{
					window.Close();
				}
			}

			if (primaryWindow is not null)
			{
				primaryWindow.Content = null;
				primaryWindow.Background = null;
			}

			Uno.UI.Core.KeyboardStateTracker.Reset();
		});
	}

	internal void RequestExit()
	{
		if (_disposed)
		{
			return;
		}

		_shutdownGate.Set();
	}

	private void InitializeCore()
	{
		_isDispatcherThread = true;
		Thread.CurrentThread.Name ??= $"Uno Headless UI ({_applicationType.Name})";

		CoreDispatcher.DispatchOverride = (action, _) => _eventLoop.Schedule(action);
		CoreDispatcher.HasThreadAccessOverride = () => _isDispatcherThread;

		ApiExtensibility.Register(typeof(ICoreApplicationExtension), _ => _coreApplicationExtension);
		ApiExtensibility.Register(typeof(INativeWindowFactoryExtension), _ => new HeadlessNativeWindowFactoryExtension(this, Options.SupportsMultipleWindows));
		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoKeyboardInputSource), host => ((HeadlessWindowWrapper)host).KeyboardInputSource);
		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoCorePointerInputSource), host => ((HeadlessWindowWrapper)host).PointerInputSource);
		ApiExtensibility.Register<ApplicationView>(typeof(IApplicationViewExtension), owner => new HeadlessApplicationViewExtension(this, owner));
		ApiExtensibility.Register<DisplayInformation>(typeof(IDisplayInformationExtension), owner => new HeadlessDisplayInformationExtension(this, owner));

		Application.Start(_ =>
		{
			var app = _appBuilder();
			app.Host = this;

			var initialWindow = Window.Current ?? new Window();

			_initialized.TrySetResult();
		});
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_shutdownGate.Set();
		_eventLoop.Dispose();
		_shutdownGate.Dispose();
	}
}
