#version 450
layout(push_constant) uniform OverlaySize { vec4 size; } overlay;
layout(location = 0) out vec2 uv;
void main()
{
    vec2 xy = vec2((gl_VertexIndex == 1 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? overlay.size.x : 0.0,
                   (gl_VertexIndex == 2 || gl_VertexIndex == 3 || gl_VertexIndex == 5) ? overlay.size.y : 0.0);
    gl_Position = vec4(xy.x / overlay.size.z * 2.0 - 1.0,
                       xy.y / overlay.size.w * 2.0 - 1.0, 0.0, 1.0);
    uv = xy / overlay.size.xy;
}
