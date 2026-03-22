#nullable enable

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using SkiaSharp;

namespace Uno.UI.Runtime.Skia.Headless.Rendering;

internal sealed class HeadlessRenderer : IDisposable
{
	private readonly UI.Xaml.Window.HeadlessWindowWrapper _window;
	private readonly TimeSpan _renderDelay;
	private readonly object _gate = new();

	private SKSurface? _surface;
	private SKBitmap? _lastFrame;
	private bool _disposed;
	private bool _renderScheduled;
	private bool _renderRequestedWhilePending;
	private SKColor _background = SKColors.Transparent;

	public HeadlessRenderer(UI.Xaml.Window.HeadlessWindowWrapper window, HeadlessPlatformOptions options)
	{
		_window = window;
		_renderDelay = options.FrameRate > 0
			? TimeSpan.FromSeconds(1d / options.FrameRate)
			: TimeSpan.Zero;
	}

	public void SetBackgroundColor(SKColor color)
	{
		_background = color;
		InvalidateRender();
	}

	public void InvalidateRender()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			if (_renderScheduled)
			{
				_renderRequestedWhilePending = true;
				return;
			}

			_renderScheduled = true;
		}

		ScheduleRender();
	}

	public void RenderNow()
	{
		_window.AssertThreadAccess();
		RenderCore();
	}

	public SKBitmap GetLastFrame()
	{
		_window.AssertThreadAccess();

		if (_lastFrame is null)
		{
			throw new InvalidOperationException("No rendered frame is available yet. Render the window first.");
		}

		return _lastFrame.Copy();
	}

	private void ScheduleRender()
	{
		if (_renderDelay == TimeSpan.Zero)
		{
			_window.Host.Post(RenderScheduledCallback);
			return;
		}

		_ = Task.Delay(_renderDelay).ContinueWith(
			_ => _window.Host.Post(RenderScheduledCallback),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private void RenderScheduledCallback()
	{
		_window.AssertThreadAccess();

		RenderCore();

		var shouldScheduleAgain = false;
		lock (_gate)
		{
			_renderScheduled = false;

			if (_renderRequestedWhilePending)
			{
				_renderRequestedWhilePending = false;
				_renderScheduled = true;
				shouldScheduleAgain = true;
			}
		}

		if (shouldScheduleAgain)
		{
			ScheduleRender();
		}
	}

	private void RenderCore()
	{
		if (_disposed)
		{
			return;
		}

		var compositionTarget = _window.ManagedXamlRoot.VisualTree.ContentRoot.CompositionTarget;
		compositionTarget.RenderForImmediateCapture();
		compositionTarget.OnNativePlatformFrameRequested(_surface?.Canvas, size =>
		{
			_surface?.Dispose();
			var width = Math.Max(1, (int)Math.Ceiling(size.Width));
			var height = Math.Max(1, (int)Math.Ceiling(size.Height));
			_surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
			_surface.Canvas.Clear(_background);
			return _surface.Canvas;
		});

		if (_surface is null)
		{
			return;
		}

		_surface.Canvas.Flush();

		using var image = _surface.Snapshot();
		_lastFrame?.Dispose();
		_lastFrame = image.ToSKBitmap();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
		}

		_surface?.Dispose();
		_lastFrame?.Dispose();
	}
}
