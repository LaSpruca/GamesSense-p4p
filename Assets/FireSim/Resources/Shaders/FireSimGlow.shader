// Plain soft glow dot for sparks and embers: radial falloff, additive (writes almost no alpha).
Shader "FireSim/Glow"
{
    Properties
    {
        _Intensity ("Intensity", Float) = 2.0
        _Occlusion ("Occlusion", Range(0, 1)) = 0.0
        _Hardness ("Hardness", Range(0.5, 4)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex FireSimVert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "FireSimCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
                float _Occlusion;
                float _Hardness;
            CBUFFER_END

            half4 frag(FireSimVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float f = pow(FireSimRadial(input.texcoord.xy), _Hardness);
                half a = f * input.color.a;
                // A hot white core in the very middle, the particle's colour around it.
                half3 rgb = lerp(input.color.rgb, half3(1, 0.95, 0.85), f * f) * (_Intensity * a);
                return half4(rgb, a * _Occlusion);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
