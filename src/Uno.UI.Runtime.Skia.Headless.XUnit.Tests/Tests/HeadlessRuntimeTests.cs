#nullable enable

using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Private.Infrastructure;
using SkiaSharp;
using Uno.UI.Runtime.Skia.Headless.XUnit;
using Uno.UI.RuntimeTests.Helpers;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace Uno.UI.Runtime.Skia.Headless.XUnit.Tests.Tests;

public sealed class HeadlessRuntimeTests : UnoHeadlessTestBase
{
	private const int SurfaceLoadTimeoutMS = 10000;

	[UnoFact]
	public Task Dispatch_Runs_On_Uno_UI_Thread()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var bounds = await LoadSampleSurface(CreateSampleSurface(
				Colors.SteelBlue,
				"Dispatcher sample",
				new Button
				{
					Content = "Refresh",
					Width = 160,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				new CheckBox
				{
					Content = "Live sync",
					IsChecked = true
				},
				new ProgressBar
				{
					Width = 220,
					Minimum = 0,
					Maximum = 100,
					Value = 35
				}));
			Assert.True(Window.Current?.DispatcherQueue.HasThreadAccess);
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, bounds, Colors.SteelBlue);
		});

	[UnoFact]
	public Task Await_Resumes_On_Uno_Dispatcher()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var bounds = await LoadSampleSurface(CreateSampleSurface(
				Colors.Teal,
				"Await sample",
				new TextBox
				{
					Header = "Project",
					Text = "Uno Headless",
					Width = 240
				},
				new ToggleSwitch
				{
					Header = "Capture frames",
					IsOn = true
				},
				new Slider
				{
					Width = 220,
					Minimum = 0,
					Maximum = 10,
					Value = 7
				}));
			Assert.True(Window.Current?.DispatcherQueue.HasThreadAccess);
			await Task.Yield();
			Assert.True(Window.Current?.DispatcherQueue.HasThreadAccess);
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, bounds, Colors.Teal);
		});

	[UnoFact]
	public Task Initial_Window_Uses_Default_Headless_Size()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var bounds = await LoadSampleSurface(CreateSampleSurface(
				Colors.SlateBlue,
				"Window sizing",
				new ComboBox
				{
					Width = 220,
					SelectedIndex = 1,
					ItemsSource = new[] { "Compact", "Default", "Wide" }
				},
				new Button
				{
					Content = "Open secondary",
					Width = 180,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				new ProgressBar
				{
					Width = 220,
					Minimum = 0,
					Maximum = 100,
					Value = 60
				}));
			Assert.Equal(1024, CurrentWindow.Bounds.Width);
			Assert.Equal(768, CurrentWindow.Bounds.Height);
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, bounds, Colors.SlateBlue);
		});

	[UnoFact]
	public Task Secondary_Window_Can_Be_Created_And_Closed()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var secondary = HeadlessWindowFactory.CreateAndActivateWindow();
			var secondaryRoot = CreateSampleSurface(
				Colors.CornflowerBlue,
				"Secondary window",
				new TextBox
				{
					Header = "Search",
					Text = "Secondary document",
					Width = 240
				},
				new CheckBox
				{
					Content = "Pin window",
					IsChecked = true
				},
				new Button
				{
					Content = "Secondary action",
					Width = 160,
					HorizontalAlignment = HorizontalAlignment.Left
				});
			secondary.Content = secondaryRoot;

			await UITestHelper.WaitFor(
				() => secondaryRoot.XamlRoot is not null && secondaryRoot.ActualWidth > 0 && secondaryRoot.ActualHeight > 0,
				timeoutMS: SurfaceLoadTimeoutMS,
				message: "Secondary window content did not finish layout.");
			await EnsureDeferredContentAttachedAsync(secondaryRoot);
			await UITestHelper.WaitForIdle();
			var secondaryBounds = secondaryRoot.TransformToVisual(null).TransformBounds(new Rect(0, 0, secondaryRoot.ActualWidth, secondaryRoot.ActualHeight));
			var (frame, _) = await CaptureFrameAsync(secondary);
			Assert.Equal(1024, frame.Width);
			Assert.Equal(768, frame.Height);
			AssertFrameHasForegroundPixels(frame, secondaryBounds, Colors.CornflowerBlue);

			secondary.Close();
			Assert.False(secondary.Visible);
		});

	[UnoFact]
	public Task Pointer_Click_Raises_Button_Click()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var clicked = 0;
			var button = new Button
			{
				Content = "Click sample button",
				Width = 180,
				Height = 50
			};
			var stayOpenToggle = new ToggleSwitch
			{
				Header = "Persist screenshot",
				IsOn = true
			};
			var root = CreateSampleSurface(
				Colors.DarkOliveGreen,
				"Pointer sample",
				button,
				stayOpenToggle);

			button.Click += (_, _) =>
			{
				clicked++;
			};

			var rootBounds = await LoadSampleSurface(root);
			await UITestHelper.WaitFor(
				() => button.XamlRoot is not null && button.ActualWidth > 0 && button.ActualHeight > 0,
				timeoutMS: SurfaceLoadTimeoutMS,
				message: "Button was not attached to the headless XamlRoot before pointer injection.");

			var buttonBounds = button.TransformToVisual(null).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
			var clickPoint = new Point(buttonBounds.X + (buttonBounds.Width / 2), buttonBounds.Y + (buttonBounds.Height / 2));
			CurrentWindow.MovePointer(clickPoint);
			await CurrentWindow.DrainDispatcherAsync();
			CurrentWindow.MouseDown();
			await CurrentWindow.DrainDispatcherAsync();
			CurrentWindow.MouseUp();
			await CurrentWindow.RenderAndDrainAsync();
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, rootBounds, Colors.DarkOliveGreen);
			await UITestHelper.WaitFor(() => clicked == 1, message: "Pointer click did not reach the button.");
			Assert.Equal(1, clicked);
		});

	[UnoFact]
	public Task Keyboard_Input_Updates_TextBox()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var textBox = new TextBox
			{
				Header = "Name",
				Width = 240,
				Height = 40
			};
			var saveButton = new Button
			{
				Content = "Save",
				Width = 120
			};
			var diagnosticsCheckBox = new CheckBox
			{
				Content = "Include diagnostics",
				IsChecked = true
			};
			var root = CreateSampleSurface(
				Colors.DarkSlateGray,
				"Keyboard sample",
				textBox,
				saveButton,
				diagnosticsCheckBox);

			var rootBounds = await LoadSampleSurface(root);
			await UITestHelper.WaitFor(
				() => textBox.XamlRoot is not null,
				timeoutMS: SurfaceLoadTimeoutMS,
				message: "TextBox was not attached to the headless XamlRoot before keyboard injection.");
			await UITestHelper.WaitForIdle();
			textBox.Focus(FocusState.Programmatic);
			await UITestHelper.WaitFor(
				() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot) == textBox,
					message: "Programmatic focus did not focus the TextBox.");
			CurrentWindow.TypeText("Uno");
			await UITestHelper.WaitFor(() => textBox.Text == "Uno", message: "Keyboard input did not update the TextBox.");
			Assert.Equal("Uno", textBox.Text);
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, rootBounds, Colors.DarkSlateGray);
		});

	[UnoFact]
	public Task Popup_Rendering_Appears_In_Captured_Frame()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			await LoadSampleSurface(CreateSampleSurface(
				Colors.Maroon,
				"Popup host",
				new Button
				{
					Content = "Open popup",
					Width = 140,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				new ToggleSwitch
				{
					Header = "Remember choice",
					IsOn = true
				}));

			var popupCheckBox = new CheckBox
			{
				Content = "Auto save",
				IsChecked = true
			};
			var popupButton = new Button
			{
				Content = "Continue",
				Width = 120
			};

			var popupChild = new Grid
			{
				Width = 220,
				Height = 140,
				Background = new SolidColorBrush(Colors.LimeGreen)
			};
			popupChild.Children.Add(new StackPanel
			{
				Margin = new Thickness(12),
				Spacing = 8,
				Children =
				{
					new TextBlock { Text = "Popup tools", FontSize = 20 },
					popupCheckBox,
					popupButton
				}
			});

			var popup = new Popup
			{
				Child = popupChild,
				HorizontalOffset = 40,
				VerticalOffset = 50
			};

			await UITestHelper.OpenPopup(popup);
			await UITestHelper.WaitFor(
				() => popupChild.XamlRoot is not null,
				timeoutMS: SurfaceLoadTimeoutMS,
				message: "Popup child did not attach to a XamlRoot.");
			await UITestHelper.WaitForIdle();
			var popupBounds = popupChild.TransformToVisual(null).TransformBounds(new Rect(0, 0, popupChild.ActualWidth, popupChild.ActualHeight));
			var (frame, savedFramePath) = await CaptureCurrentFrameAsync();
			Assert.True(VisualTreeHelper.GetOpenPopupsForXamlRoot(TestServices.WindowHelper.XamlRoot).Count > 0);
			Assert.True(frame.Width > 0);
			Assert.True(frame.Height > 0);
			AssertFrameHasForegroundPixels(frame, popupBounds, Colors.LimeGreen, minimumNonBackgroundPixels: 50);
			Assert.False(string.IsNullOrWhiteSpace(savedFramePath));

			popup.IsOpen = false;
			await UITestHelper.WaitForIdle();
		});

	[UnoFact]
	public Task ContentDialog_Rendering_Can_Be_Captured()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			await LoadSampleSurface(CreateSampleSurface(
				Colors.Black,
				"Dialog host",
				new Button
				{
					Content = "Open dialog",
					Width = 140,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				new CheckBox
				{
					Content = "Track generated files",
					IsChecked = true
				}));

			var commentTextBox = new TextBox
			{
				Header = "Comment",
				Text = "Saved from headless xUnit."
			};
			var annotationsCheckBox = new CheckBox
			{
				Content = "Include annotations",
				IsChecked = true
			};
			var saveProgressBar = new ProgressBar
			{
				Minimum = 0,
				Maximum = 100,
				Value = 70,
				Width = 220
			};

			var dialog = new ContentDialog
			{
				Title = "Headless",
				Content = new StackPanel
				{
					Spacing = 8,
					Children =
					{
						new TextBlock { Text = "Review the generated frame." },
						commentTextBox,
						annotationsCheckBox,
						saveProgressBar
					}
				},
				PrimaryButtonText = "OK"
			};

			var showOperation = await UITestHelper.ShowDialogAsync(dialog);
			await UITestHelper.WaitFor(
					() => VisualTreeHelper.GetOpenPopupsForXamlRoot(TestServices.WindowHelper.XamlRoot).Count > 0,
					message: "ContentDialog popup was not opened.");
			var dialogPopup = VisualTreeHelper.GetOpenPopupsForXamlRoot(TestServices.WindowHelper.XamlRoot)[0];
			Assert.NotNull(dialogPopup.Child);
			await UITestHelper.WaitForIdle();
			var dialogBounds = ((FrameworkElement)dialogPopup.Child!).TransformToVisual(null).TransformBounds(
				new Rect(0, 0, ((FrameworkElement)dialogPopup.Child!).ActualWidth, ((FrameworkElement)dialogPopup.Child!).ActualHeight));

			var (frame, _) = await CaptureCurrentFrameAsync();
			Assert.True(frame.Width > 0);
			Assert.True(frame.Height > 0);
			AssertFrameHasForegroundPixels(frame, dialogBounds, Colors.Black, minimumNonBackgroundPixels: 50);

			dialog.Hide();
			await showOperation.AsTask();
		});

	[UnoFact]
	public Task Theme_And_Background_Changes_Affect_Rendered_Frame()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var previewButton = new Button
			{
				Content = "Preview light",
				Width = 140,
				HorizontalAlignment = HorizontalAlignment.Left
			};
			var themeToggle = new ToggleSwitch
			{
				Header = "Dark mode",
				IsOn = false
			};
			var progressBar = new ProgressBar
			{
				Width = 220,
				Minimum = 0,
				Maximum = 100,
				Value = 30
			};
			var root = CreateSampleSurface(
				Colors.White,
				"Theme preview",
				previewButton,
				themeToggle,
				progressBar);
			root.RequestedTheme = ElementTheme.Light;
			var bounds = await LoadSampleSurface(root);
			await UITestHelper.WaitForIdle();
			var (lightFrame, _) = await CaptureCurrentFrameAsync($"{nameof(Theme_And_Background_Changes_Affect_Rendered_Frame)}.Light.png");
			AssertFrameHasForegroundPixels(lightFrame, bounds, Colors.White);

			root.RequestedTheme = ElementTheme.Dark;
			root.Background = new SolidColorBrush(Colors.DarkSlateBlue);
			previewButton.Content = "Preview dark";
			themeToggle.IsOn = true;
			progressBar.Value = 80;
			await UITestHelper.WaitForIdle();
			var (darkFrame, _) = await CaptureCurrentFrameAsync($"{nameof(Theme_And_Background_Changes_Affect_Rendered_Frame)}.Dark.png");
			AssertFrameHasForegroundPixels(darkFrame, bounds, Colors.DarkSlateBlue);

			Assert.Equal(ElementTheme.Dark, root.RequestedTheme);
			Assert.Equal(lightFrame.Width, darkFrame.Width);
			Assert.Equal(lightFrame.Height, darkFrame.Height);
		});

	[UnoFact]
	public Task Frame_Capture_Returns_Stable_Bitmap()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var slider = new Slider
			{
				Width = 220,
				Minimum = 0,
				Maximum = 10,
				Value = 6
			};
			var root = CreateSampleSurface(
				Colors.DeepPink,
				"Frame capture",
				new Button
				{
					Content = "Capture",
					Width = 120,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				slider,
				new ProgressBar
				{
					Width = 220,
					Minimum = 0,
					Maximum = 100,
					Value = 55
				});
			var bounds = await LoadSampleSurface(root);
			await UITestHelper.WaitForIdle();
			var (frame, _) = await CaptureCurrentFrameAsync();
			Assert.Equal(1024, frame.Width);
			Assert.Equal(768, frame.Height);
			AssertFrameHasForegroundPixels(frame, bounds, Colors.DeepPink);
		});

	[UnoFact]
	public Task Unsupported_Windowing_Feature_Fails_Clearly()
		=> RunOnUIThreadAsync(async () =>
		{
			AssertFluentResourcesAvailable();
			var bounds = await LoadSampleSurface(CreateSampleSurface(
				Colors.IndianRed,
				"Unsupported feature",
				new Button
				{
					Content = "Attempt presenter change",
					Width = 220,
					HorizontalAlignment = HorizontalAlignment.Left
				},
				new CheckBox
				{
					Content = "Expect NotSupportedException",
					IsChecked = true
				}));
			var (frame, _) = await CaptureCurrentFrameAsync();
			AssertFrameHasForegroundPixels(frame, bounds, Colors.IndianRed);
			Assert.Throws<NotSupportedException>(() => CurrentWindow.AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay));
		});

	private static Grid CreateSampleSurface(Color background, string title, params UIElement[] content)
	{
		var panel = new StackPanel
		{
			Spacing = 12,
			Width = 320,
			Height = 220,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(24),
			Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF8, 0xF6, 0xF1))
		};
		panel.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 22,
			Margin = new Thickness(12, 12, 12, 0)
		});

		foreach (var element in content)
		{
			if (element is FrameworkElement frameworkElement)
			{
				frameworkElement.Margin = new Thickness(12, 0, 12, 0);
			}

			panel.Children.Add(element);
		}

		var root = new Grid
		{
			Width = 420,
			Height = 280,
			Tag = panel,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Background = new SolidColorBrush(background)
		};
		return root;
	}

	private static async Task<Rect> LoadSampleSurface(FrameworkElement root)
	{
		var bounds = await UITestHelper.Load(
			root,
			element => element.XamlRoot is not null && element.ActualWidth > 0 && element.ActualHeight > 0,
			SurfaceLoadTimeoutMS);

		await EnsureDeferredContentAttachedAsync(root);
		return bounds;
	}

	private static async Task EnsureDeferredContentAttachedAsync(FrameworkElement root)
	{
		if (root is not Grid { Tag: FrameworkElement deferredPanel } grid)
		{
			return;
		}

		grid.Tag = null;
		grid.Children.Add(deferredPanel);
		grid.UpdateLayout();
		deferredPanel.UpdateLayout();
		await UITestHelper.WaitFor(
			() => deferredPanel.XamlRoot is not null && deferredPanel.ActualWidth > 0 && deferredPanel.ActualHeight > 0,
			timeoutMS: SurfaceLoadTimeoutMS,
			message: "Deferred sample panel did not finish layout after being attached.");
	}

	private static async Task<(SKBitmap Bitmap, string Path)> CaptureFrameAsync(Window window, string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		var capture = await UITestHelper.CaptureAndSaveFrameAsync(window, fileName: fileName, callerName: callerName);
		AssertSavedFrame(capture.Path);
		return capture;
	}

	private static async Task<(SKBitmap Bitmap, string Path)> CaptureCurrentFrameAsync(string? fileName = null, [CallerMemberName] string? callerName = null)
	{
		var capture = await UITestHelper.CaptureAndSaveFrameAsync(TestServices.WindowHelper.CurrentTestWindow, fileName, callerName: callerName);
		AssertSavedFrame(capture.Path);
		return capture;
	}

	private static void AssertSavedFrame(string path)
	{
		Assert.True(File.Exists(path), $"Expected screenshot to be written to '{path}'.");
	}

	private static void AssertFrameHasForegroundPixels(SKBitmap frame, Rect region, Color background, int minimumNonBackgroundPixels = 150)
	{
		var startX = Math.Max(0, (int)Math.Floor(region.X));
		var startY = Math.Max(0, (int)Math.Floor(region.Y));
		var endX = Math.Min(frame.Width, (int)Math.Ceiling(region.X + region.Width));
		var endY = Math.Min(frame.Height, (int)Math.Ceiling(region.Y + region.Height));
		var backgroundColor = new SKColor(background.R, background.G, background.B, background.A);
		var nonBackgroundPixels = 0;

		for (var y = startY; y < endY; y++)
		{
			for (var x = startX; x < endX; x++)
			{
				var pixel = frame.GetPixel(x, y);
				if (pixel.Alpha == 0)
				{
					continue;
				}

				if (pixel != backgroundColor)
				{
					nonBackgroundPixels++;
				}
			}
		}

		Assert.True(
			nonBackgroundPixels >= minimumNonBackgroundPixels,
			$"Expected at least {minimumNonBackgroundPixels} non-background pixels in {region}, but found {nonBackgroundPixels}.");
	}

	private static void AssertFluentResourcesAvailable()
	{
		Assert.Contains(Application.Current.Resources.MergedDictionaries, dictionary => dictionary is XamlControlsResources);
	}
}
