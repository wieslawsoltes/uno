#nullable enable

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.UI.Runtime.Skia.Headless.Input;
using Uno.UI.Runtime.Skia.Headless.UI.Xaml.Window;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Uno.UI.Runtime.Skia.Headless;

public static class HeadlessWindowExtensions
{
	public static SKBitmap CaptureRenderedFrame(this Window window)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		wrapper.RenderNow();
		return wrapper.GetLastFrame();
	}

	public static SKBitmap GetLastRenderedFrame(this Window window)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		return wrapper.GetLastFrame();
	}

	public static void RenderFrame(this Window window)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		wrapper.RenderNow();
	}

	public static Task RenderAndDrainAsync(this Window window, CancellationToken cancellationToken = default)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		return RenderAndDrainCoreAsync(window.DispatcherQueue, wrapper, cancellationToken);
	}

	public static Task DrainDispatcherAsync(this Window window, CancellationToken cancellationToken = default)
	{
		return DrainCoreAsync(window.DispatcherQueue, cancellationToken);
	}

	public static void MovePointer(this Window window, Point position, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		InjectMouseInput(wrapper, modifiers, CreateMove(wrapper, position));
	}

	public static void Click(this Window window, Point position, HeadlessMouseButton button = HeadlessMouseButton.Left, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		InjectMouseInput(
			wrapper,
			modifiers,
			CreateMove(wrapper, position),
			new InjectedInputMouseInfo { MouseOptions = GetDownOptions(button) },
			new InjectedInputMouseInfo { MouseOptions = GetUpOptions(button) });
	}

	public static void MouseDown(this Window window, HeadlessMouseButton button = HeadlessMouseButton.Left, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		InjectMouseInput(wrapper, modifiers, new InjectedInputMouseInfo { MouseOptions = GetDownOptions(button) });
	}

	public static void MouseUp(this Window window, HeadlessMouseButton button = HeadlessMouseButton.Left, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		InjectMouseInput(wrapper, modifiers, new InjectedInputMouseInfo { MouseOptions = GetUpOptions(button) });
	}

	public static void MouseWheel(this Window window, Point position, int deltaY, int deltaX = 0, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		InjectMouseInput(
			wrapper,
			modifiers,
			CreateMove(wrapper, position),
			new InjectedInputMouseInfo
			{
				MouseOptions = deltaX == 0 ? InjectedInputMouseOptions.Wheel : InjectedInputMouseOptions.HWheel,
				DeltaY = deltaY,
				DeltaX = deltaX
			});
	}

	public static void KeyDown(this Window window, VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None, char? unicodeKey = null)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		wrapper.KeyboardInputSource.InjectKeyDown(key, modifiers, unicodeKey);
	}

	public static void KeyUp(this Window window, VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None, char? unicodeKey = null)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		wrapper.KeyboardInputSource.InjectKeyUp(key, modifiers, unicodeKey);
	}

	public static void TypeText(this Window window, string text)
	{
		var wrapper = GetWrapper(window);
		wrapper.AssertThreadAccess();
		wrapper.KeyboardInputSource.TypeText(text);
	}

	private static HeadlessWindowWrapper GetWrapper(Window window)
	{
		ArgumentNullException.ThrowIfNull(window);

		if (window.NativeWrapper is not HeadlessWindowWrapper wrapper)
		{
			throw new InvalidOperationException("The window is not hosted by the Uno headless Skia runtime.");
		}

		return wrapper;
	}

	private static void InjectMouseInput(HeadlessWindowWrapper wrapper, VirtualKeyModifiers modifiers, params InjectedInputMouseInfo[] inputs)
	{
		var injector = wrapper.GetInputInjector();
		injector.InjectMouseInput(inputs.Select(input => (input, modifiers)));
	}

	private static InjectedInputMouseInfo CreateMove(HeadlessWindowWrapper wrapper, Point position)
	{
		var current = wrapper.GetInputInjector().Mouse.Position;
		return new InjectedInputMouseInfo
		{
			MouseOptions = InjectedInputMouseOptions.Move,
			DeltaX = (int)Math.Round(position.X - current.X),
			DeltaY = (int)Math.Round(position.Y - current.Y)
		};
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

	private static async Task DrainCoreAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue, CancellationToken cancellationToken)
	{
		await EnqueueAsync(dispatcherQueue, cancellationToken);
		await EnqueueAsync(dispatcherQueue, cancellationToken);
	}

	private static Task EnqueueAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue, CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		cancellationToken.ThrowIfCancellationRequested();

		if (!dispatcherQueue.TryEnqueue(() => tcs.TrySetResult()))
		{
			throw new InvalidOperationException("Failed to enqueue work on the headless dispatcher queue.");
		}

		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
		}

		return tcs.Task;
	}

	private static async Task RenderAndDrainCoreAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue, HeadlessWindowWrapper wrapper, CancellationToken cancellationToken)
	{
		// The headless host renders on demand, so captures need to let queued layout/input work settle
		// before painting, then give any frame-render callbacks one more chance to run.
		await DrainCoreAsync(dispatcherQueue, cancellationToken);
		wrapper.RenderNow();
		await DrainCoreAsync(dispatcherQueue, cancellationToken);
		wrapper.RenderNow();
		await DrainCoreAsync(dispatcherQueue, cancellationToken);
	}
}
