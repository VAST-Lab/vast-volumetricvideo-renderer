// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        _SrcBlend("Src Blend", Float) = 8 // OneMinusDstAlpha
        _DstBlend("Dst Blend", Float) = 1 // One
        _ZWrite("ZWrite", Float) = 0 // Off
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_local __ SPLAT_REPROJECTED
#pragma multi_compile_local __ SPLAT_VERTEX_PROJECT

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
float2 _SplatScreenSize;
uint _SplatOffset;
uint _UseOrderBuffer;

#if defined(SPLAT_VERTEX_PROJECT)
float4x4 _MatrixObjectToWorld;
float4x4 _MatrixWorldToObject;
float4x4 _MatrixMV;
float4x4 _MatrixP;
float4x4 _MatrixVP;
float4 _VecScreenParams;
float4 _VecWorldSpaceCameraPos;
float _SplatScale;
float _SplatOpacityScale;
uint _SHOrder;
uint _SHOnly;
#else
StructuredBuffer<SplatViewData> _SplatViewData;
#endif

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    nointerpolation uint idx : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

// Separate constant buffer avoids Unity's DX12 stale-uniform bug (IN-99220).
cbuffer SplatGlobalUniforms // match struct SplatGlobalUniforms in C#
{
    uint sgu_transparencyMode;
    uint sgu_frameOffset;
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    if (_UseOrderBuffer != 0 || sgu_transparencyMode == 0)
        instID = _OrderBuffer[instID] + _SplatOffset;
    else
        instID = instID + _SplatOffset;
    o.idx = instID + sgu_frameOffset;

#if defined(SPLAT_VERTEX_PROJECT)
    SplatData splat = LoadSplatGeometry(instID);
    float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(splat.pos, 1)).xyz;
    float4 centerClipPos = mul(_MatrixVP, float4(centerWorldPos, 1));
    if (centerClipPos.w <= 0)
    {
        o.vertex = asfloat(0x7fc00000);
        return o;
    }
    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(splat.rot, splat.scale);
    float3 cov3d0, cov3d1;
    CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
    float splatScale2 = _SplatScale * _SplatScale;
    cov3d0 *= splatScale2;
    cov3d1 *= splatScale2;
    float3 cov2d = CalcCovariance2D(splat.pos, cov3d0, cov3d1, _MatrixMV, _MatrixP, _VecScreenParams);
    float2 axis1, axis2;
    DecomposeCovariance(cov2d, axis1, axis2);
    float2 extentClip = 4 * (abs(axis1) + abs(axis2)) / _VecScreenParams.xy * centerClipPos.w;
    if (any(abs(centerClipPos.xy) > centerClipPos.w + extentClip))
    {
        o.vertex = asfloat(0x7fc00000);
        return o;
    }
    LoadSplatColor(instID, splat);
    float3 worldViewDir = _VecWorldSpaceCameraPos.xyz - centerWorldPos;
    float3 objViewDir = normalize(mul((float3x3)_MatrixWorldToObject, worldViewDir));
    o.col.rgb = ShadeSH(splat.sh, objViewDir, _SHOrder, _SHOnly != 0);
    o.col.a = min(splat.opacity * _SplatOpacityScale, 65000);

    uint idx = vtxID;
    float2 quadPos = (float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0) * 2.0;
    o.pos = quadPos;
    float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _SplatScreenSize;
    o.vertex = float4(centerClipPos.xyz / centerClipPos.w, 1);
    o.vertex.xy += deltaScreenPos;
#else
    SplatViewData view = _SplatViewData[instID];
    // test "is this +inf" sentinel bits exactly
    bool culled = asuint(view.pos.x) == 0x7f800000u;
    if (culled)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
    }
    else
    {
        o.col.r = f16tof32(view.color.x >> 16);
        o.col.g = f16tof32(view.color.x);
        o.col.b = f16tof32(view.color.y >> 16);
        o.col.a = f16tof32(view.color.y);

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;

        float2 axis1 = float2(f16tof32(view.axis1), f16tof32(view.axis1 >> 16));
        float2 axis2 = float2(f16tof32(view.axis2), f16tof32(view.axis2 >> 16));
        float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _SplatScreenSize;
        o.vertex = float4(view.pos, 1);
        o.vertex.xy += deltaScreenPos;
    }
#endif
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

// Hash Functions for GPU Rendering
// https://jcgt.org/published/0009/03/02/
uint3 pcg3d16(uint3 v)
{
    v = v * 12829u + 47989u;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

    v >>= 16u;
    return v;
}

struct FragOutput
{
    half4 color : SV_Target0;
#if SPLAT_REPROJECTED
    float depth : SV_Target1;
#endif
};

FragOutput frag (v2f i)
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);
    alpha = saturate(alpha * i.col.a);

    if (alpha < 1.0/255.0)
        discard;

    if (sgu_transparencyMode == 0)
    {
        i.col.rgb *= alpha;
    }
    else
    {
        // "Hashed Alpha Testing", Wyman, McGuire 2017
        // https://casual-effects.com/research/Wyman2017Hashed/index.html
        uint3 hash = pcg3d16(uint3(uint2(i.vertex.xy), i.idx));
        float cutoff = hash.x * (1.0 / 65536.0);
        if (alpha <= cutoff)
            discard;
        alpha = 1;
    }
    FragOutput res;
    res.color = half4(i.col.rgb, alpha);
#if SPLAT_REPROJECTED
    res.depth = i.vertex.z;
#endif
    return res;
}
ENDCG
        }
    }
}
