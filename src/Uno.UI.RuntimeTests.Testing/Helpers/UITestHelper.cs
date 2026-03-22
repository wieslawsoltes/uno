#nullable enable

using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;
using Private.Infrastructure;
using Uno.UI.Runtime.Skia.Headless;

namespace Uno.UI.RuntimeTests.Helpers;

public static class UITestHelper
{
	public static async Task<Rect> Load<T>(T element, Func<T, bool>? isLoaded = null, int timeoutMS = 1000) where T : FrameworkElement
	{
		TestServices.WindowHelper.WindowContent = element;
		await WaitForLoaded(element, isLoaded, timeoutMS);
		return element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
	}

	public static Task WaitForLoaded<T>(T element, Func<T, bool>? isLoaded = null, int timeoutMS = 1000) where T : FrameworkElement
		=> TestServices.WindowHelper.WaitForLoaded(
				element,
				isLoaded is null ? null : new Func<FrameworkElement, bool>(frameworkElement => isLoaded((T)frameworkElement)),
				timeoutMS);

	public static Task WaitFor(Func<bool> condition, int timeoutMS = 1000, string? message = null)
		=> TestServices.WindowHelper.WaitFor(condition, timeoutMS, message);

	public static Task WaitForIdle()
		=> TestServices.WindowHelper.WaitForIdle();

	public static async Task<SKBitmap> ScreenShot()
	{
		await WaitForIdle();
		return TestServices.WindowHelper.CurrentTestWindow.CaptureRenderedFrame();
	}

	public static async Task<SKBitmap> ScreenShot(FrameworkElement _)
		=> await ScreenShot();

	public static (SKBitmap Bitmap, string Path) CaptureAndSaveFrame(Window window, string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		ArgumentNullException.ThrowIfNull(window);

		var frame = window.CaptureRenderedFrame();
		return (frame, SaveBitmap(frame, fileName, callerName));
	}

	public static async Task<(SKBitmap Bitmap, string Path)> CaptureAndSaveFrameAsync(Window window, string? fileName = null, bool waitForIdle = true, [CallerMemberName] string? callerName = null)
	{
		ArgumentNullException.ThrowIfNull(window);

		if (waitForIdle)
		{
			await window.RenderAndDrainAsync();
		}

		var frame = window.GetLastRenderedFrame();
		return (frame, SaveBitmap(frame, fileName, callerName));
	}

	public static async Task<(SKBitmap Bitmap, string Path)> CaptureAndSaveFrame(string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		var frame = await ScreenShot();
		return (frame, SaveBitmap(frame, fileName, callerName));
	}

	public static string SaveBitmap(SKBitmap bitmap, string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		ArgumentNullException.ThrowIfNull(bitmap);

		var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? callerName : fileName;

		if (string.IsNullOrWhiteSpace(resolvedFileName))
		{
			throw new ArgumentException("A file name is required.", nameof(fileName));
		}

		var artifactsDirectory = GetArtifactsDirectory();
		Directory.CreateDirectory(artifactsDirectory);

		var pngFileName = Path.GetExtension(resolvedFileName).Length == 0
			? $"{resolvedFileName}.png"
			: resolvedFileName;
		var path = Path.Combine(artifactsDirectory, pngFileName);

		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 100);
		using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
		data.SaveTo(stream);

		return path;
	}

	public static async Task<string> SaveScreenShot(string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		var bitmap = await ScreenShot();
		return SaveBitmap(bitmap, fileName, callerName);
	}

	public static void CloseAllPopups()
	{
		foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(TestServices.WindowHelper.XamlRoot))
		{
			popup.IsOpen = false;
		}
	}

	public static async Task OpenPopup(Popup popup)
	{
		ArgumentNullException.ThrowIfNull(popup);
		popup.XamlRoot = TestServices.WindowHelper.XamlRoot;
		popup.IsOpen = true;
		await WaitForIdle();
	}

	public static async Task<IAsyncOperation<ContentDialogResult>> ShowDialogAsync(ContentDialog dialog)
	{
		ArgumentNullException.ThrowIfNull(dialog);
		dialog.XamlRoot = TestServices.WindowHelper.XamlRoot;
		var operation = dialog.ShowAsync(ContentDialogPlacement.Popup);
		await WaitForIdle();
		return operation;
	}

	private static string GetArtifactsDirectory()
	{
		var explicitDirectory = Environment.GetEnvironmentVariable("UNO_HEADLESS_TEST_OUTPUT_DIR");

		return string.IsNullOrWhiteSpace(explicitDirectory)
			? Path.Combine(AppContext.BaseDirectory, "artifacts")
			: explicitDirectory;
	}
}
