#nullable enable

using System;

namespace Uno.UI.Runtime.Skia.Headless.Testing;

public enum UnoTestIsolationLevel
{
	PerAssembly,
	PerTest
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class UnoTestIsolationAttribute : Attribute
{
	public UnoTestIsolationAttribute(UnoTestIsolationLevel isolationLevel)
	{
		IsolationLevel = isolationLevel;
	}

	public UnoTestIsolationLevel IsolationLevel { get; }
}
