shader_type canvas_item;


void fragment() {
    vec4 input_color = texture(TEXTURE, UV);
    vec4 output_color = vec4(input_color.r,input_color.g,input_color.b,input_color.a);
    COLOR = output_color;

}
