shader_type canvas_item;

// 局内解法提示：起点暖色 → 终点冷色，透明度热衰减；
// 亮带从 S 向 E 循环流动。UV.x 由 Line2D(texture_mode=STRETCH) 提供。

uniform vec4 hot_color : source_color = vec4(1.0, 0.82, 0.40, 1.0);
uniform vec4 mid_color : source_color = vec4(1.0, 0.54, 0.36, 1.0);
uniform vec4 cool_color : source_color = vec4(0.35, 0.82, 0.78, 1.0);
uniform float alpha_start : hint_range(0.0, 1.0) = 0.95;
uniform float alpha_end : hint_range(0.0, 1.0) = 0.25;
uniform float flow_speed : hint_range(0.0, 3.0) = 0.55;
uniform float band_width : hint_range(0.01, 0.5) = 0.16;

void fragment() {
	float t = clamp(UV.x, 0.0, 1.0);
	vec3 base = t < 0.5
		? mix(hot_color.rgb, mid_color.rgb, t * 2.0)
		: mix(mid_color.rgb, cool_color.rgb, (t - 0.5) * 2.0);
	float base_alpha = mix(alpha_start, alpha_end, t);
	float flow = fract(t - TIME * flow_speed);
	float band = smoothstep(0.0, band_width, flow)
		* smoothstep(0.0, band_width, 1.0 - flow);
	vec3 col = mix(base, hot_color.rgb, band * 0.55);
	float alpha = clamp(base_alpha + band * 0.25, 0.0, 1.0);
	COLOR = vec4(col, alpha);
}
