---
uid: Uno.Testing.Headless.XUnit
---

<!-- markdownlint-disable MD013 -->

# Headless xUnit Testing

Uno Platform provides an xUnit layer on top of the Skia headless host so UI tests can run in a normal test process, without `SamplesApp` and without a visible desktop window.

This is the recommended workflow when you want:

- a plain `dotnet test` inner loop for desktop Skia UI tests
- screenshot artifacts written directly from the test process
- programmatic pointer and keyboard input
- deterministic off-screen rendering in CI

## What the xUnit layer adds

The xUnit integration is intentionally thin. It reuses the headless runtime and adds:

- `UnoHeadlessTestBase`
- `UnoFact`
- `UnoTheory`
- assembly-level `UnoTestApplication`
- assembly-level `UnoTestIsolation`
- shared helpers from `Uno.UI.RuntimeTests.Testing`

The complete in-repo sample is here:

- [Uno.UI.Runtime.Skia.Headless.XUnit.Tests](https://github.com/unoplatform/uno/tree/master/src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests)

## Project setup

Reference the headless runtime and xUnit integration artifacts that match your Uno version. Inside the Uno repository, the test app references:

- `Uno.UI.Runtime.Skia.Headless`
- `Uno.UI.Runtime.Skia.Headless.XUnit`
- `Uno.UI.RuntimeTests.Testing`

The test project also needs to behave like a Uno Skia head. The essential MSBuild properties are:

```xml
<PropertyGroup>
    <IsTestProject>true</IsTestProject>
    <OutputType>Exe</OutputType>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <IsUnoHead>true</IsUnoHead>
    <UnoRuntimeIdentifier>Skia</UnoRuntimeIdentifier>
</PropertyGroup>
```

If you are working inside the Uno repository, use the sample project as the exact reference:

- [Uno.UI.Runtime.Skia.Headless.XUnit.Tests.csproj](https://github.com/unoplatform/uno/blob/master/src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests/Uno.UI.Runtime.Skia.Headless.XUnit.Tests.csproj)

## Registering the test application

Each test assembly should declare the Uno `Application` type used for the headless session:

```csharp
using Uno.UI.Runtime.Skia.Headless.Testing;

[assembly: UnoTestApplication(typeof(HeadlessTestApplication))]
[assembly: UnoTestIsolation(UnoTestIsolationLevel.PerAssembly)]
```

`UnoTestIsolation` is optional. If it is omitted, the default isolation level is `PerTest`.

## Creating the test application

Your test application should load the resources your controls need. For WinUI controls this usually means merging `XamlControlsResources`.

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal sealed class HeadlessTestApplication : Application
{
    public HeadlessTestApplication()
    {
        if (!Resources.MergedDictionaries.OfType<XamlControlsResources>().Any())
        {
            Resources.MergedDictionaries.Insert(0, new XamlControlsResources());
        }
    }
}
```

If your test suite renders text on desktop Skia, also make sure ICU data is available. The in-repo sample shows how the test application initializes ICU packages for desktop runs:

- [HeadlessTestApplication.cs](https://github.com/unoplatform/uno/blob/master/src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests/HeadlessTestApplication.cs)

## Writing a test

Inherit from `UnoHeadlessTestBase` and run UI code through `RunOnUIThreadAsync`.

```csharp
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Runtime.Skia.Headless;
using Uno.UI.Runtime.Skia.Headless.XUnit;
using Uno.UI.RuntimeTests.Helpers;
using Xunit;

public sealed class CounterTests : UnoHeadlessTestBase
{
    [UnoFact]
    public Task Button_Click_Updates_State()
        => RunOnUIThreadAsync(async () =>
        {
            var clicked = 0;
            var button = new Button
            {
                Content = "Run",
                Width = 160,
                Height = 44
            };

            button.Click += (_, _) => clicked++;

            await UITestHelper.Load(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Headless xUnit" },
                    button
                }
            });

            var bounds = button.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(
                    0,
                    0,
                    button.ActualWidth,
                    button.ActualHeight));
            var clickPoint = new Windows.Foundation.Point(
                bounds.X + (bounds.Width / 2),
                bounds.Y + (bounds.Height / 2));

            CurrentWindow.Click(clickPoint);
            await CurrentWindow.RenderAndDrainAsync();

            Assert.Equal(1, clicked);

            await UITestHelper.SaveScreenShot();
        });
}
```

## The core test APIs

`UnoHeadlessTestBase` provides:

- `RunOnUIThreadAsync(...)`
- `CurrentWindow`
- `CreateAdditionalWindowAsync()`

Use `RunOnUIThreadAsync` for any code that touches XAML objects, rendering, or headless input. The headless runtime enforces UI-thread access for these operations.

## Loading UI and waiting for layout

`Uno.UI.RuntimeTests.Testing` provides the same style of helpers used by existing runtime tests:

- `UITestHelper.Load`
- `UITestHelper.WaitForLoaded`
- `UITestHelper.WaitFor`
- `UITestHelper.WaitForIdle`
- `TestServices.WindowHelper.WindowContent`

These helpers are useful for:

- swapping the current window content
- waiting for XAML roots to attach
- waiting for layout to settle before capture or assertions
- resetting the window between tests

## Capturing screenshots

Use `HeadlessWindowExtensions` directly when you want the raw `SKBitmap`, or use `UITestHelper` when you want the bitmap saved to disk.

```csharp
await CurrentWindow.RenderAndDrainAsync();

var bitmap = CurrentWindow.CaptureRenderedFrame();
var path = await UITestHelper.SaveScreenShot("my-test-frame");
```

Helpful APIs:

- `CurrentWindow.CaptureRenderedFrame()`
- `CurrentWindow.GetLastRenderedFrame()`
- `UITestHelper.CaptureAndSaveFrameAsync(...)`
- `UITestHelper.SaveScreenShot(...)`

By default, screenshots are written to:

- `AppContext.BaseDirectory/artifacts`

To override the output directory, set:

- `UNO_HEADLESS_TEST_OUTPUT_DIR`

## Popups and dialogs

For popup and `ContentDialog` tests, make sure the surface is attached to a valid `XamlRoot` first.

The helper layer already includes:

- `UITestHelper.OpenPopup(...)`
- `UITestHelper.ShowDialogAsync(...)`
- `UITestHelper.CloseAllPopups()`

These helpers assign the current `XamlRoot` before showing the UI, which is required for correct headless rendering.

## Additional windows

You can create and validate secondary windows in headless tests:

```csharp
var secondary = await CreateAdditionalWindowAsync();
secondary.Content = new TextBlock { Text = "Secondary window" };

await secondary.RenderAndDrainAsync();
```

If you need direct control, you can also use `HeadlessWindowFactory.CreateAndActivateWindow()`.

## Isolation modes

Two isolation modes are supported:

- `PerTest`
- `PerAssembly`

`PerTest` creates a fresh headless runtime for each test. It gives the strongest isolation and is the default.

`PerAssembly` keeps a shared runtime alive for the whole assembly and resets its state between tests. This is usually faster for larger suites.

State reset clears:

- popup state
- secondary windows
- current window content
- current window background
- keyboard state

## Running from the CLI

These tests run with xUnit v3 on Microsoft.Testing.Platform. That matters for filtering.

List tests:

```bash
dotnet test --project path/to/Your.Tests.csproj -- --list-tests
```

Run a single test method:

```bash
dotnet test --project path/to/Your.Tests.csproj \
  -- \
  --filter-method "MyNamespace.MyTests.Button_Click_Updates_State"
```

Run a whole class:

```bash
dotnet test --project path/to/Your.Tests.csproj \
  -- \
  --filter-class "MyNamespace.MyTests"
```

> [!IMPORTANT]
> `--filter` is a VSTest switch and does not work with this xUnit v3 Microsoft.Testing.Platform setup. Use `--filter-method`, `--filter-class`, or `--filter-query`.

The in-repo validation suite can be executed from the Uno repository root with:

```bash
dotnet test --project src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests/Uno.UI.Runtime.Skia.Headless.XUnit.Tests.csproj \
  -p:UnoTargetFrameworkOverride=net10.0 \
  -- \
  --filter-class "Uno.UI.Runtime.Skia.Headless.XUnit.Tests.Tests.HeadlessRuntimeTests"
```

## When to choose headless xUnit vs. existing runtime tests

Choose headless xUnit when you want:

- `dotnet test` execution without `SamplesApp`
- deterministic off-screen rendering and screenshot output
- desktop Skia validation in a normal test runner

Choose [platform-runtime unit tests](xref:Uno.Contributing.CreateRuntimeTests) when you want:

- reuse of the existing `SamplesApp` runtime-test infrastructure
- broader platform execution across current runtime-test lanes
- the closest match to existing Uno internal runtime-test conventions

## Related articles

- [Using the Skia Headless Platform](xref:Uno.Skia.Headless)
- [Getting Started With Tests](xref:Uno.Authoring.Tests)
- [Creating unit tests in Uno.UI.RuntimeTests](xref:Uno.Contributing.CreateRuntimeTests)
