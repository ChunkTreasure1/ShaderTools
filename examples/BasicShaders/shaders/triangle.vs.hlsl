struct VertexOutput
{
    float4 position : SV_Position;
    float3 color : COLOR0;
};

VertexOutput main(uint vertexId : SV_VertexID)
{
    static const float2 positions[3] = {
        float2(0.0, -0.6),
        float2(0.6, 0.6),
        float2(-0.6, 0.6)
    };
    static const float3 colors[3] = {
        float3(1.0, 0.2, 0.2),
        float3(0.2, 1.0, 0.2),
        float3(0.2, 0.4, 1.0)
    };

    VertexOutput output;
    output.position = float4(positions[vertexId], 0.0, 1.0);
    output.color = colors[vertexId];
    return output;
}
