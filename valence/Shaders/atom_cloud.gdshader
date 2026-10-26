// Faint fresnel "electron cloud" around each system; brightens when focused.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_back;

instance uniform vec4 cloud_color : source_color = vec4(0.35, 0.6, 1.0, 1.0);
instance uniform float highlight = 0.0;

void fragment() {
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
	ALBEDO = cloud_color.rgb;
	ALPHA = rim * (0.10 + 0.45 * highlight) * cloud_color.a;
}
