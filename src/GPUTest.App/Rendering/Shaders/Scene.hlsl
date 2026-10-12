cbuffer Camera : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 CubeWorld;
    float4 Eye;
    float4 Right;
    float4 Up;
};

struct Ball { float3 position; float radius; float4 orientation; };
StructuredBuffer<Ball> Balls : register(t0);

struct SphereVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation float4 centerRadius : TEXCOORD1;
    nointerpolation uint identity : TEXCOORD2;
};

SphereVertex SphereVS(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    static const float2 corners[6] = {
        float2(-1, -1), float2(-1, 1), float2(1, 1),
        float2(-1, -1), float2(1, 1), float2(1, -1)
    };
    Ball ball = Balls[instanceId];
    SphereVertex output;
    output.uv = corners[vertexId];
    output.centerRadius = float4(ball.position, ball.radius);
    output.identity = instanceId;
    float3 position = ball.position + ball.radius * (Right.xyz * output.uv.x + Up.xyz * output.uv.y);
    output.position = mul(float4(position, 1), ViewProjection);
    return output;
}

struct SpherePixel { float4 color : SV_Target; float depth : SV_Depth; };
SpherePixel SpherePS(SphereVertex input)
{
    float radial = dot(input.uv, input.uv);
    clip(1 - radial);
    float3 forward = normalize(cross(Right.xyz, Up.xyz));
    float3 normal = normalize(input.uv.x * Right.xyz + input.uv.y * Up.xyz + sqrt(saturate(1 - radial)) * forward);
    float3 position = input.centerRadius.xyz + input.centerRadius.w * normal;
    float4 projected = mul(float4(position, 1), ViewProjection);
    static const float3 palette[5] = {
        float3(0.16, 0.72, 0.60), float3(0.94, 0.37, 0.31),
        float3(0.97, 0.74, 0.26), float3(0.36, 0.62, 0.90), float3(0.83, 0.86, 0.84)
    };
    float3 light = normalize(float3(-0.4, 0.8, 0.5));
    float3 halfway = normalize(light + normalize(Eye.xyz - position));
    float diffuse = saturate(dot(normal, light));
    float specular = pow(saturate(dot(normal, halfway)), 64);
    SpherePixel output;
    output.color = float4(palette[input.identity % 5] * (0.28 + 0.72 * diffuse) + specular * 0.7, 1);
    output.depth = projected.z / projected.w;
    return output;
}

float4 CubeVS(uint vertexId : SV_VertexID) : SV_Position
{
    static const uint edgeCorners[24] = {0,1, 1,3, 3,2, 2,0, 4,5, 5,7, 7,6, 6,4, 0,4, 1,5, 2,6, 3,7};
    uint corner = edgeCorners[vertexId];
    float3 local = float3((corner & 1) ? 5 : -5, (corner & 2) ? 5 : -5, (corner & 4) ? 5 : -5);
    return mul(mul(float4(local, 1), CubeWorld), ViewProjection);
}

float4 CubePS() : SV_Target { return float4(0.72, 0.80, 0.81, 1); }