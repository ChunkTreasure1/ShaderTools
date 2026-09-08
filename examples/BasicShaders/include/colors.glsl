#ifndef SHADER_TOOLS_COLORS_GLSL
#define SHADER_TOOLS_COLORS_GLSL

vec3 exampleTint(vec3 color)
{
#ifdef USE_WARM_TINT
    return color * vec3(1.0, 0.82, 0.68);
#else
    return color;
#endif
}

#endif
