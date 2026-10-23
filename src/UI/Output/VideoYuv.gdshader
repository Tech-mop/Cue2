shader_type canvas_item;

// TEXTURE is the luma plane (L8, so .r is Y). chroma_tex is Rg8 at half size, R = U, G = V.
// Output is gamma-encoded RGB. Do not convert to linear on the Compatibility renderer.
// In fragment(), Godot's COLOR is the modulate already multiplied by TEXTURE (the luma),
// so the modulate is taken from vertex() instead. Fades and opacity still apply.
uniform sampler2D chroma_tex;
uniform float range_limited = 1.0;
uniform float matrix_id = 1.0;

varying vec4 v_modulate;

void vertex() {
	v_modulate = COLOR;
}

void fragment() {
	float y = texture(TEXTURE, UV).r;
	vec2 chroma = texture(chroma_tex, UV).rg;
	float cb;
	float cr;
	if (range_limited > 0.5) {
		y = (y - 0.06274509804) * 1.164383562;
		cb = (chroma.r - 0.5) * 1.138392857;
		cr = (chroma.g - 0.5) * 1.138392857;
	} else {
		cb = chroma.r - 0.5;
		cr = chroma.g - 0.5;
	}

	float kr;
	float kb;
	if (matrix_id < 0.5) {
		kr = 0.299;
		kb = 0.114;
	} else if (matrix_id > 1.5) {
		kr = 0.2627;
		kb = 0.0593;
	} else {
		kr = 0.2126;
		kb = 0.0722;
	}
	float kg = 1.0 - kr - kb;
	float r = y + 2.0 * (1.0 - kr) * cr;
	float b = y + 2.0 * (1.0 - kb) * cb;
	float g = y - (2.0 * kb * (1.0 - kb) / kg) * cb - (2.0 * kr * (1.0 - kr) / kg) * cr;

	COLOR = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0) * v_modulate;
}
