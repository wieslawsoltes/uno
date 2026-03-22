#nullable enable

using Microsoft.UI.Xaml;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;

namespace Uno.UI.Runtime.Skia.Headless.Input;

public enum HeadlessMouseButton
{
	Left,
	Middle,
	Right,
	X1
}

internal sealed class HeadlessPointerInputSource : IUnoCorePointerInputSource
{
	private readonly UI.Xaml.Window.HeadlessWindowWrapper _window;
	private readonly InjectedInputState _mouse = new(PointerDeviceType.Mouse);
	private bool _isPointerOver;
	private CoreCursor _pointerCursor = new(CoreCursorType.Arrow, 0);

	public HeadlessPointerInputSource(UI.Xaml.Window.HeadlessWindowWrapper window)
	{
		_window = window;
		_mouse.Position = new Point(window.Bounds.Width / 2, window.Bounds.Height / 2);
	}

	public event TypedEventHandler<object, PointerEventArgs>? PointerCaptureLost;
	public event TypedEventHandler<object, PointerEventArgs>? PointerEntered;
	public event TypedEventHandler<object, PointerEventArgs>? PointerExited;
	public event TypedEventHandler<object, PointerEventArgs>? PointerMoved;
	public event TypedEventHandler<object, PointerEventArgs>? PointerPressed;
	public event TypedEventHandler<object, PointerEventArgs>? PointerReleased;
	public event TypedEventHandler<object, PointerEventArgs>? PointerWheelChanged;
	public event TypedEventHandler<object, PointerEventArgs>? PointerCancelled;

	public bool HasCapture { get; private set; }

	public CoreCursor PointerCursor
	{
		get => _pointerCursor;
		set => _pointerCursor = value;
	}

	public Point PointerPosition => _mouse.Position;

	public void Move(Point position, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		EnsureEnterExit(position);
		Dispatch(new InjectedInputMouseInfo
		{
			MouseOptions = InjectedInputMouseOptions.Move,
			DeltaX = (int)Math.Round(position.X - _mouse.Position.X),
			DeltaY = (int)Math.Round(position.Y - _mouse.Position.Y)
		}, modifiers);
	}

	public void ButtonDown(HeadlessMouseButton button, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
		=> Dispatch(new InjectedInputMouseInfo { MouseOptions = GetDownOptions(button) }, modifiers);

	public void ButtonUp(HeadlessMouseButton button, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
		=> Dispatch(new InjectedInputMouseInfo { MouseOptions = GetUpOptions(button) }, modifiers);

	public void Click(Point position, HeadlessMouseButton button = HeadlessMouseButton.Left, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		Move(position, modifiers);
		ButtonDown(button, modifiers);
		ButtonUp(button, modifiers);
	}

	public void Cancel(VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
		=> PointerCancelled?.Invoke(this, CreateCurrentEventArgs(modifiers));

	public void Wheel(Point position, int deltaY, int deltaX = 0, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		Move(position, modifiers);
		Dispatch(new InjectedInputMouseInfo
		{
			MouseOptions = deltaX == 0 ? InjectedInputMouseOptions.Wheel : InjectedInputMouseOptions.HWheel,
			DeltaY = deltaY,
			DeltaX = deltaX
		}, modifiers);
	}

	public void ReleasePointerCapture(PointerIdentifier pointer)
	{
		HasCapture = false;
		PointerCaptureLost?.Invoke(this, CreateCurrentEventArgs(VirtualKeyModifiers.None));
	}

	public void SetPointerCapture(PointerIdentifier pointer) => HasCapture = true;

	public void ReleasePointerCapture()
	{
		HasCapture = false;
		PointerCaptureLost?.Invoke(this, CreateCurrentEventArgs(VirtualKeyModifiers.None));
	}

	public void SetPointerCapture() => HasCapture = true;

	private void Dispatch(InjectedInputMouseInfo info, VirtualKeyModifiers modifiers)
	{
		_mouse.StartNewSequence();
		var args = info.ToEventArgs(_mouse, modifiers);
		_mouse.Update(args);

		if (info.MouseOptions.HasFlag(InjectedInputMouseOptions.Wheel)
			|| info.MouseOptions.HasFlag(InjectedInputMouseOptions.HWheel))
		{
			PointerWheelChanged?.Invoke(this, args);
			return;
		}

		var updateKind = args.CurrentPoint.Properties.PointerUpdateKind;
		if (updateKind is Windows.UI.Input.PointerUpdateKind.LeftButtonPressed
			or Windows.UI.Input.PointerUpdateKind.MiddleButtonPressed
			or Windows.UI.Input.PointerUpdateKind.RightButtonPressed
			or Windows.UI.Input.PointerUpdateKind.XButton1Pressed)
		{
			PointerPressed?.Invoke(this, args);
			return;
		}

		if (updateKind is Windows.UI.Input.PointerUpdateKind.LeftButtonReleased
			or Windows.UI.Input.PointerUpdateKind.MiddleButtonReleased
			or Windows.UI.Input.PointerUpdateKind.RightButtonReleased
			or Windows.UI.Input.PointerUpdateKind.XButton1Released)
		{
			PointerReleased?.Invoke(this, args);
			return;
		}

		PointerMoved?.Invoke(this, args);
	}

	private PointerEventArgs CreateCurrentEventArgs(VirtualKeyModifiers modifiers)
		=> new InjectedInputMouseInfo { MouseOptions = InjectedInputMouseOptions.Move }.ToEventArgs(_mouse, modifiers);

	private void EnsureEnterExit(Point position)
	{
		var isInside = position.X >= 0
			&& position.Y >= 0
			&& position.X <= _window.Bounds.Width
			&& position.Y <= _window.Bounds.Height;

		if (isInside && !_isPointerOver)
		{
			_isPointerOver = true;
			PointerEntered?.Invoke(this, CreateCurrentEventArgs(VirtualKeyModifiers.None));
		}
		else if (!isInside && _isPointerOver)
		{
			_isPointerOver = false;
			PointerExited?.Invoke(this, CreateCurrentEventArgs(VirtualKeyModifiers.None));
		}
	}

	private static InjectedInputMouseOptions GetDownOptions(HeadlessMouseButton button) => button switch
	{
		HeadlessMouseButton.Left => InjectedInputMouseOptions.LeftDown,
		HeadlessMouseButton.Middle => InjectedInputMouseOptions.MiddleDown,
		HeadlessMouseButton.Right => InjectedInputMouseOptions.RightDown,
		HeadlessMouseButton.X1 => InjectedInputMouseOptions.XDown,
		_ => throw new ArgumentOutOfRangeException(nameof(button))
	};

	private static InjectedInputMouseOptions GetUpOptions(HeadlessMouseButton button) => button switch
	{
		HeadlessMouseButton.Left => InjectedInputMouseOptions.LeftUp,
		HeadlessMouseButton.Middle => InjectedInputMouseOptions.MiddleUp,
		HeadlessMouseButton.Right => InjectedInputMouseOptions.RightUp,
		HeadlessMouseButton.X1 => InjectedInputMouseOptions.XUp,
		_ => throw new ArgumentOutOfRangeException(nameof(button))
	};
}
