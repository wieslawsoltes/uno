#nullable enable

namespace Uno.UI.Composition.ProGpu;

internal static class ProGpuTiledImageShader
{
	// Extending each bilinear tap independently preserves wrap seams and transparent edges.
	internal const string Source = """
		fn progpu_tile_index(index: i32, size: i32, mode: u32) -> i32 {
			if (mode == 0u) {
				return select(-1, index, index >= 0 && index < size);
			}
			if (mode == 1u) {
				return clamp(index, 0, size - 1);
			}
			let period = select(size, size * 2, mode == 3u);
			let wrapped = ((index % period) + period) % period;
			return select(wrapped, min(wrapped, period - 1 - wrapped), mode == 3u);
		}

		fn progpu_tile_sample(index: vec2<i32>, size: vec2<i32>, modes: vec2<u32>) -> vec4<f32> {
			let x = progpu_tile_index(index.x, size.x, modes.x);
			let y = progpu_tile_index(index.y, size.y, modes.y);
			if (x < 0 || y < 0) {
				return vec4<f32>(0.0);
			}
			return textureLoad(sourceTexture0, vec2<i32>(x, y), 0);
		}

		fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
			let parameters = wpf_constant(0u);
			let position = uv * parameters.xy - vec2<f32>(0.5);
			let origin = vec2<i32>(floor(position));
			let fraction = fract(position);
			let size = vec2<i32>(textureDimensions(sourceTexture0));
			let modes = vec2<u32>(parameters.zw);
			let top = mix(
				progpu_tile_sample(origin, size, modes),
				progpu_tile_sample(origin + vec2<i32>(1, 0), size, modes),
				fraction.x);
			let bottom = mix(
				progpu_tile_sample(origin + vec2<i32>(0, 1), size, modes),
				progpu_tile_sample(origin + vec2<i32>(1, 1), size, modes),
				fraction.x);
			return mix(top, bottom, fraction.y);
		}
		""";
}
