#ifndef FIRESIM_COMMON_INCLUDED
#define FIRESIM_COMMON_INCLUDED

// Shared plumbing for the FireSim particle shaders.
//
// Every one of these shaders is built for a passthrough MR eye buffer: the cameras clear to
// (0,0,0,0) and the compositor lays the eye buffer over the real room using PREMULTIPLIED alpha.
// So all of them output premultiplied colour with "Blend One OneMinusSrcAlpha": the RGB written is
// added to the room, and the alpha written is how much of the room behind is hidden. A flame that
// writes bright RGB with a small alpha is therefore additive glow that barely occludes; foam writes
// its colour with a high alpha and covers what is behind it.
//
// The particle systems feed four floats in TEXCOORD0 via custom vertex streams:
//   xy = quad UV, z = age (0..1 over the particle's life), w = a per-particle stable random.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

struct FireSimAttributes
{
    float4 positionOS : POSITION;
    half4  color      : COLOR;
    float4 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct FireSimVaryings
{
    float4 positionCS : SV_POSITION;
    half4  color      : COLOR;
    float4 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

FireSimVaryings FireSimVert(FireSimAttributes input)
{
    FireSimVaryings output = (FireSimVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.color = input.color;
    output.texcoord = input.texcoord;
    return output;
}

// Soft radial falloff from the centre of the quad: 1 in the middle, 0 at the edge of the circle.
float FireSimRadial(float2 uv)
{
    float2 p = (uv - 0.5) * 2.0;
    float r = length(p);
    float f = saturate(1.0 - r);
    return f * f;
}

// 2D rotation about the quad centre, so a shared noise texture reads differently per particle.
float2 FireSimRotateUV(float2 uv, float angle)
{
    float s, c;
    sincos(angle, s, c);
    float2 p = uv - 0.5;
    return float2(p.x * c - p.y * s, p.x * s + p.y * c) + 0.5;
}

#endif
