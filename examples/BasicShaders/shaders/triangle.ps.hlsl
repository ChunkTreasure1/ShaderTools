#include "colors.hlsli"

float4 main(float4 position : SV_Position, float3 color : COLOR0) : SV_Target0
{
    return float4(ExampleTint(color), 1.0);
}
