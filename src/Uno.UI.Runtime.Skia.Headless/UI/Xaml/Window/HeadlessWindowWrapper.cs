#nullable enable

using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Headless.Input;
using Uno.UI.Runtime.Skia.Headless.Rendering;
using Uno.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;

namespace Uno.UI.Runtime.Skia.Headless.UI.Xaml.Window;

internal sealed class HeadlessWindowWrapper : NativeWindowWrapperBase, IXamlRootHost, IDisposable
{
	private readonly IDisposable _backgroundSubscription;
	private SKColor _background = SKColors.Transparent;
	private InputInjector? _inputInjector;

	internal HeadlessWindowWrapper(Microsoft.UI.Xaml.Window window, XamlRoot xamlRoot, Hosting.HeadlessHost host, HeadlessPlatformOptions options)
		: base(window, xamlRoot)
	{
		Host = host;
		RasterizationScale = (float)options.RasterizationScale;
		PointerInputSource = new HeadlessPointerInputSource(this);
		KeyboardInputSource = new HeadlessKeyboardInputSource(this);
		Renderer = new HeadlessRenderer(this, options);

		UpdateBounds(options.WindowSize);
		UpdateBackground();
		_backgroundSubscription = window.RegisterBackgroundChangedEvent((_, _) => UpdateBackground());
	}

	internal Hosting.HeadlessHost Host { get; }

	internal Microsoft.UI.Xaml.Window ManagedWindow => Window!;

	internal XamlRoot ManagedXamlRoot => XamlRoot!;

	internal HeadlessRenderer Renderer { get; }

	internal HeadlessPointerInputSource PointerInputSource { get; }

	internal HeadlessKeyboardInputSource KeyboardInputSource { get; }

	internal InputInjector GetInputInjector()
	{
		AssertThreadAccess();
		_inputInjector ??= InputInjector.TryCreate()
			?? throw new InvalidOperationException("Failed to create the Uno InputInjector for the headless Skia host.");
		return _inputInjector;
	}

	public override object? NativeWindow => null;

	internal UIElement? RootElement => Window?.RootElement;

	UIElement? IXamlRootHost.RootElement => RootElement;

	void IXamlRootHost.InvalidateRender() => Renderer.InvalidateRender();

	internal void AssertThreadAccess()
	{
		var hasThreadAccess = Window?.DispatcherQueue?.HasThreadAccess == true
			|| CoreDispatcher.Main.HasThreadAccess;

		if (!hasThreadAccess)
		{
			throw new InvalidOperationException("Headless window operations must run on the Uno UI thread.");
		}
	}

	internal void RenderNow() => Renderer.RenderNow();

	internal SKBitmap GetLastFrame() => Renderer.GetLastFrame();

	protected override void ShowCore()
	{
		IsVisible = true;
		Host.BringToFront(this);
		Renderer.InvalidateRender();
	}

	internal protected override void Activate()
	{
		Host.ActivateWindow(this);
		Renderer.InvalidateRender();
	}

	protected override void CloseCore()
	{
		Host.CloseWindow(this);
	}

	public override void Move(PointInt32 position)
	{
		Position = position;
	}

	public override void Resize(SizeInt32 size)
	{
		UpdateBounds(new Size(size.Width / RasterizationScale, size.Height / RasterizationScale));
		Renderer.InvalidateRender();
	}

	internal void SetActivationState(bool isActive)
	{
		ActivationState = isActive ? CoreWindowActivationState.CodeActivated : CoreWindowActivationState.Deactivated;
	}

	internal void SetFocus(bool isFocused) => SetActivationState(isFocused);

	private void UpdateBounds(Size logicalSize)
	{
		var bounds = new Rect(0, 0, logicalSize.Width, logicalSize.Height);
		SetBoundsAndVisibleBounds(bounds, bounds);

		var physicalSize = new SizeInt32(
			(int)Math.Ceiling(logicalSize.Width * RasterizationScale),
			(int)Math.Ceiling(logicalSize.Height * RasterizationScale));
		SetSizes(physicalSize, physicalSize);
	}

	private void UpdateBackground()
	{
		if (Window?.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
		{
			_background = new SKColor(brush.Color.AsUInt32());
		}
		else if (Window?.Background is null)
		{
			_background = SKColors.Transparent;
		}

		Renderer.SetBackgroundColor(_background);
	}

	public void Dispose()
	{
		_backgroundSubscription.Dispose();
		Renderer.Dispose();
	}
}
