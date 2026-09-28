// Soft smoke / steam puff. A radial falloff eroded by slowly drifting noise, rotated per particle,
// with a touch of top-lighting so puffs read as volumes rather than flat discs. Vertex colour
// supplies the grey level (dark for smoke, near-white for steam and foam mist) and the alpha.
Shader "FireSim/Smoke"
{
    Properties
    {
        _MainTex ("Noise (tileable)", 2D) = "gray" {}
        _Erode ("Erosion", Range(0, 1)) = 0.55
        _Softness ("Softness", Range(0.01, 1)) = 0.45
        _Drift ("Noise Drift", Float) = 0.12
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

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Erode;
                float _Softness;
                float _Drift;
            CBUFFER_END

            half4 frag(FireSimVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                float age = input.texcoord.z;
                float rnd = input.texcoord.w;

                float2 ruv = FireSimRotateUV(uv, rnd * 6.2831853);
                float t = _Time.y * _Drift;
                float n = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, ruv * 1.4 + float2(rnd * 2.7, t)).r * 0.65 +
                          SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, ruv * 2.9 - float2(t * 0.7, rnd * 4.1)).g * 0.35;

                float radial = FireSimRadial(uv);
                // Puffs thin out with age: the noise eats into the disc from the outside in.
                float threshold = _Erode * (0.35 + age * 0.9);
                float density = smoothstep(threshold, threshold + _Softness, radial * (0.6 + n * 0.8));

                // Fake volume: brighter on top, darker underneath.
                float shade = lerp(0.72, 1.12, saturate(uv.y + (n - 0.5) * 0.3));

                half a = density * input.color.a;
                half3 rgb = input.color.rgb * shade * a;
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
