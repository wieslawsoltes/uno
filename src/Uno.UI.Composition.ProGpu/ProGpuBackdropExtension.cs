#nullable enable

using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Uno.UI.Composition.ProGpu;

internal sealed unsafe class ProGpuBackdropExtension(ICompositorExtension native) : ICompositorExtension
{
	internal static void Draw(DrawingContext context, BackdropMaterialBrush brush, Rect bounds, Matrix4x4 transform) =>
		context.DrawExtension(CompositorBuiltInExtensions.BackdropMaterial, dataParam: new Material(brush, bounds), transform: transform);

	internal static BackdropMaterialParams? GetParameters(object? data) => data switch
	{
		Material material => BackdropMaterialParams.FromBrush(material.Brush, material.Bounds, Vector4.Zero, Vector4.Zero),
		BackdropMaterialParams parameters => parameters,
		_ => null,
	};

	public void Compile(Compositor compositor, IRenderDataProvider? provider, Matrix4x4 transform, ref RenderCommand command)
	{
		if (command.DataParam is Material material)
		{
			var parameters = GetParameters(material)!;
			// Compilation sees outer retained-picture and DPI transforms; recording does not.
			var scaleX = new Vector2(transform.M11, transform.M12).Length();
			var scaleY = new Vector2(transform.M21, transform.M22).Length();
			parameters.BlurRadius *= System.Math.Max(scaleX, scaleY);
			command.DataParam = parameters;
		}
		native.Compile(compositor, provider, transform, ref command);
	}

	public void Render(Compositor compositor, void* renderPassEncoder, bool isOffscreen, in Compositor.CompositorDrawCall drawCall) =>
		native.Render(compositor, renderPassEncoder, isOffscreen, in drawCall);

	// The compositor still owns and calls lifecycle hooks on the registered native extension.
	private sealed record Material(BackdropMaterialBrush Brush, Rect Bounds);
}
