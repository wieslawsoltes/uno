#nullable enable

using Uno.UI.Helpers.WinUI;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Skia.Headless.Input;

internal sealed class HeadlessKeyboardInputSource : IUnoKeyboardInputSource
{
	private readonly UI.Xaml.Window.HeadlessWindowWrapper _window;

	public HeadlessKeyboardInputSource(UI.Xaml.Window.HeadlessWindowWrapper window)
	{
		_window = window;
	}

	public event TypedEventHandler<object, KeyEventArgs>? KeyDown;

	public event TypedEventHandler<object, KeyEventArgs>? KeyUp;

	public void InjectKeyDown(VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None, char? unicodeKey = null)
		=> KeyDown?.Invoke(this, CreateArgs(key, modifiers, unicodeKey, isReleased: false));

	public void InjectKeyUp(VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None, char? unicodeKey = null)
		=> KeyUp?.Invoke(this, CreateArgs(key, modifiers, unicodeKey, isReleased: true));

	public void TypeText(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		foreach (var character in text)
		{
			var modifiers = char.IsUpper(character) ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None;
			var key = character switch
			{
				'\r' or '\n' => VirtualKey.Enter,
				'\t' => VirtualKey.Tab,
				_ => SharedHelpers.GetVirtualKeyFromChar(character)
			};

			InjectKeyDown(key, modifiers, character);
			InjectKeyUp(key, modifiers, character);
		}
	}

	private static KeyEventArgs CreateArgs(VirtualKey key, VirtualKeyModifiers modifiers, char? unicodeKey, bool isReleased)
		=> new(
			deviceId: "headless-keyboard",
			virtualKey: key,
			modifiers: modifiers,
			keyStatus: new CorePhysicalKeyStatus
			{
				ScanCode = (uint)key,
				RepeatCount = 1,
				IsKeyReleased = isReleased
			},
			unicodeKey: unicodeKey);
}
