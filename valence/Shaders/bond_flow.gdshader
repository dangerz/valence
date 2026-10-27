// Bonds between systems. The cylinder's local Y runs from atom A (-0.5) to atom B (+0.5).
// Pulses travel in the dependency direction (dependent -> dependency); mutual bonds pulse both ways.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

instance uniform vec4 bond_color : source_color = vec4(0.3, 0.8, 0.95, 1.0);
instance uniform float bond_length = 1.0;
instance uniform float flow_sign = 1.0;
instance uniform float two_way = 0.0;
instance uniform float highlight = 0.0;

uniform float dash_density = 1.1;
uniform float speed = 0.9;

// View ▸ Brightness.
uniform float brightness = 1.0;

varying float along;

void vertex() {
	along = VERTEX.y + 0.5;
}

void fragment() {
	float d = along * bond_length * dash_density;
	float forward = smoothstep(0.6, 0.95, fract(d * flow_sign - TIME * speed));
	float backward = smoothstep(0.6, 0.95, fract(-d * flow_sign - TIME * speed));
	float pulse = mix(forward, max(forward, backward), two_way);
	float base = 0.22 + 0.35 * highlight;
	ALBEDO = bond_color.rgb;
	ALPHA = clamp((base + pulse * (0.7 + 0.6 * highlight)) * bond_color.a * brightness, 0.0, 1.0);
}
