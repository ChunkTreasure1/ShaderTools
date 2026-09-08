#version 450
#extension GL_GOOGLE_include_directive : require

#include "colors.glsl"

layout(location = 0) in vec3 vertexColor;
layout(location = 0) out vec4 outputColor;

void main()
{
    outputColor = vec4(exampleTint(vertexColor), 1.0);
}
