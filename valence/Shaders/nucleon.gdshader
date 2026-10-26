// Nucleons (one MultiMesh for every class). Instance color rgb = base color,
// instance color alpha = glow amount (recently changed / selected).
shader_type spatial;

uniform float glow_strength = 2.6;
uniform float rim_strength = 0.35;

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 0.45;
	METALLIC = 0.1;
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.0);
	EMISSION = COLOR.rgb * (COLOR.a * glow_strength + rim * rim_strength);
}
