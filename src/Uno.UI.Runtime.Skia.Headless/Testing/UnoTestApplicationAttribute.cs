#nullable enable

using System;

namespace Uno.UI.Runtime.Skia.Headless.Testing;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class UnoTestApplicationAttribute : Attribute
{
	public UnoTestApplicationAttribute(Type applicationType)
	{
		ApplicationType = applicationType ?? throw new ArgumentNullException(nameof(applicationType));
	}

	public Type ApplicationType { get; }
}
