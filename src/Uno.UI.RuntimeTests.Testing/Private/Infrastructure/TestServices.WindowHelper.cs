#nullable enable

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Uno.UI.Runtime.Skia.Headless;

namespace Private.Infrastructure;

public partial class TestServices
{
	public static class WindowHelper
	{
		private static Window? _currentTestWindow;
		private static XamlRoot? _xamlRoot;

		public static Window CurrentTestWindow
		{
			get => _currentTestWindow ?? Window.Current ?? throw new InvalidOperationException("No current Uno window is available for the headless test.");
			set => _currentTestWindow = value;
		}

		public static XamlRoot XamlRoot
		{
			get => _xamlRoot
				?? CurrentTestWindow.Content?.XamlRoot
				?? throw new InvalidOperationException("The current headless window does not have a XamlRoot yet. Set WindowContent and wait for it to load first.");
			set => _xamlRoot = value;
		}

		public static UIElement? WindowContent
		{
			get => CurrentTestWindow.Content;
			set
			{
				Initialize();
				CurrentTestWindow.Content = value;
				_xamlRoot = value?.XamlRoot;
			}
		}

		public static void Initialize(Window? window = null)
		{
			CurrentTestWindow = window ?? Window.Current ?? throw new InvalidOperationException("No current Uno window is available for the headless test.");

			if (!CurrentTestWindow.Visible)
			{
				CurrentTestWindow.Activate();
			}

			_xamlRoot ??= CurrentTestWindow.Content?.XamlRoot;
		}

		public static async Task WaitForIdle()
		{
			Initialize();
			await CurrentTestWindow.RenderAndDrainAsync();
			_xamlRoot ??= CurrentTestWindow.Content?.XamlRoot;
		}

		public static async Task WaitForLoaded(FrameworkElement element, Func<FrameworkElement, bool>? isLoaded = null, int timeoutMS = 1000)
		{
			ArgumentNullException.ThrowIfNull(element);

			bool DefaultIsLoaded()
				=> element.XamlRoot is not null
					&& element.ActualWidth > 0
					&& element.ActualHeight > 0
					&& element.IsLoaded;

			await WaitFor(
				() => isLoaded?.Invoke(element) ?? DefaultIsLoaded(),
				timeoutMS,
				$"Timeout waiting for {element} to be loaded.");

			_xamlRoot = element.XamlRoot ?? _xamlRoot;
			await WaitForIdle();
		}

		public static async Task WaitFor(
			Func<bool> condition,
			int timeoutMS = 1000,
			string? message = null)
		{
			ArgumentNullException.ThrowIfNull(condition);

			if (condition())
			{
				return;
			}

			var stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < timeoutMS)
			{
				await WaitForIdle();

				if (condition())
				{
					return;
				}

				await Task.Delay(16);
			}

			throw new TimeoutException(message ?? "Timed out waiting for the headless UI condition to be satisfied.");
		}

		public static async Task SwapWindowContentAsync(UIElement? content)
		{
			WindowContent = content;
			await WaitForIdle();
		}

		public static async Task ResetWindowContentAndWaitForIdle()
		{
			WindowContent = null;
			await WaitForIdle();
		}
	}
}
