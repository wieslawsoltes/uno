#nullable enable

using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uno.UI.Runtime.Skia.Headless.XUnit.Tests;

internal sealed class HeadlessTestApplication : Application
{
	static HeadlessTestApplication()
	{
		InitializeIcuData();
	}

	public HeadlessTestApplication()
	{
		if (Resources.MergedDictionaries.OfType<XamlControlsResources>().Any())
		{
			return;
		}

		Resources.MergedDictionaries.Insert(0, new XamlControlsResources());
	}

	private static void InitializeIcuData()
	{
		var icuType = Type.GetType("Microsoft.UI.Xaml.Documents.UnicodeText+ICU, Uno.UI");
		var setMethod = icuType?.GetMethod("SetDataAssembly", BindingFlags.Public | BindingFlags.Static);
		setMethod?.Invoke(null, [typeof(HeadlessTestApplication).Assembly]);
	}
}
