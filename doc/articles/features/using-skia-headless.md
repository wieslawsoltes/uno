---
uid: Uno.Skia.Headless
---

<!-- markdownlint-disable MD013 -->

# Using the Skia Headless Platform

Uno Platform includes a Skia-based headless host for automated UI validation. It runs a real Uno visual tree, dispatcher, and `Microsoft.UI.Xaml.Window` without creating a visible native OS window.

The headless host is intended for:

- automated desktop UI tests that run with `dotnet test`
- off-screen frame capture and screenshot comparison
- programmatic pointer and keyboard input
- windowing, popup, and dialog scenarios that need a real XAML tree

The headless host is not a replacement for platform-backed desktop heads or mobile/browser heads. It is an off-screen runtime designed for automation.

## What the headless host provides

- A dedicated Uno UI thread and dispatcher queue
- An off-screen `Window` implementation
- On-demand Skia rendering into `SKBitmap`
- Pointer and keyboard input injection
- Multiple virtual windows
- Reusable test-session integration for xUnit

## When to use it

Use the headless host when you want to validate Uno UI behavior from a normal test process and you do not need native OS window integration.

Typical scenarios include:

- rendering a control tree and saving a PNG artifact
- asserting layout, theme, popup, and `ContentDialog` behavior
- simulating pointer clicks or keyboard input in CI
- testing secondary-window flows on desktop Skia

## When not to use it

The headless host intentionally does not try to emulate every OS integration. Keep using a platform-backed head when you need behavior that fundamentally depends on a real system window or native surface.

Examples include:

- `WebView` or other native embedded surfaces
- file pickers and other shell-owned dialogs
- drag-and-drop with the OS shell
- media surfaces that require a native compositor target
- browser-specific or mobile-specific hosting behavior

## Configuring the host

The headless runtime plugs into `UnoPlatformHostBuilder` through `UseHeadless`:

```csharp
using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Headless;
using Windows.Foundation;

var host = UnoPlatformHostBuilder.Create()
    .App(() => new App())
    .UseHeadless(options =>
    {
        options.WindowSize = new Size(1440, 900);
        options.RasterizationScale = 1.0;
        options.FrameRate = 60;
        options.UseSoftwareRenderer = true;
        options.SupportsMultipleWindows = true;
    })
    .Build();

host.Run();
```

## `HeadlessPlatformOptions`

`HeadlessPlatformOptions` controls the behavior of the off-screen host.

| Option | Default | Description |
| --- | --- | --- |
| `WindowSize` | `1024 x 768` | Initial size of each headless window |
| `RasterizationScale` | `1.0` | XAML root rasterization scale used for rendering |
| `FrameRate` | `60` | Preferred frame pacing for render callbacks |
| `UseSoftwareRenderer` | `true` | Uses the software renderer for deterministic automation by default |
| `SupportsMultipleWindows` | `true` | Allows creating additional virtual windows |

## Working with windows

The headless host creates a normal Uno `Window`, so your app code keeps using the WinUI windowing APIs you already know.

- `Window.Current` returns the initial window for the headless session
- `window.Activate()` makes the window visible to the headless host
- `window.Close()` closes the virtual window and releases its captured frame

To create an additional window:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Runtime.Skia.Headless;

var secondary = HeadlessWindowFactory.CreateAndActivateWindow();
secondary.Content = new TextBlock { Text = "Secondary window" };
```

If `SupportsMultipleWindows` is `false`, creating a second window throws `InvalidOperationException`.

## Rendering and frame capture

The headless host renders on demand. Use `HeadlessWindowExtensions` on the Uno UI thread to pump layout and capture frames.

```csharp
using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.UI.Runtime.Skia.Headless;

await window.RenderAndDrainAsync();

using SKBitmap frame = window.CaptureRenderedFrame();
```

Available helpers:

- `RenderFrame()`
- `RenderAndDrainAsync()`
- `DrainDispatcherAsync()`
- `CaptureRenderedFrame()`
- `GetLastRenderedFrame()`

`CaptureRenderedFrame()` triggers a render before returning the latest `SKBitmap`. `GetLastRenderedFrame()` returns the most recently captured frame without forcing a new render.

## Injecting input

The headless runtime exposes pointer and keyboard helpers directly on `Window`.

```csharp
using Windows.Foundation;
using Uno.UI.Runtime.Skia.Headless;

window.MovePointer(new Point(120, 80));
window.MouseDown();
window.MouseUp();

window.TypeText("Uno");
```

Available input helpers:

- `MovePointer`
- `Click`
- `MouseDown`
- `MouseUp`
- `MouseWheel`
- `KeyDown`
- `KeyUp`
- `TypeText`

These APIs use Uno's injected input path, so controls receive normal pointer and keyboard events rather than test-only callbacks.

## UI-thread requirement

All headless window operations must run on the Uno UI thread. The runtime will throw if you call rendering or input APIs from the wrong thread.

When you use the xUnit integration, `RunOnUIThreadAsync` on `UnoHeadlessTestBase` is the standard way to execute code safely on the headless dispatcher.

## Supported scenarios

The current headless coverage is aimed at desktop Skia automation and currently includes:

- initial and secondary windows
- layout and theme changes
- `Popup`
- `ContentDialog`
- pointer and keyboard input
- frame capture for diagnostic artifacts

The validation suite for the initial implementation is in the Uno repository:

- [Headless runtime sample tests](https://github.com/unoplatform/uno/tree/master/src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests)

## Limitations

The headless host is currently designed for Skia-based desktop automation.

- It is not a WinAppSDK head
- It does not emulate browser or mobile hosts
- Native OS integrations are intentionally out of scope
- Features that require a real platform-owned window or native compositor target may fail with `NotSupportedException`

If you need broad platform coverage or parity with the existing SamplesApp runtime infrastructure, continue using [platform-runtime unit tests](xref:Uno.Contributing.CreateRuntimeTests) and [Uno.UITest-based UI tests](xref:Uno.Contributing.CreateUITests) where appropriate.

## Related articles

- [Using the Skia Desktop](xref:Uno.Skia.Desktop)
- [Headless xUnit testing](xref:Uno.Testing.Headless.XUnit)
- [Windowing](xref:Uno.Features.WinUIWindow)
