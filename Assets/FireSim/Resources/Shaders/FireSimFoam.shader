// Extinguisher foam blob. Each particle is shaded as a small lit sphere (fixed view-space light
// from the upper left, wrapped diffuse, a soft highlight and a cool tint in the shadowed half)
// with its rim roughened by noise so clumps are not perfect circles. Written premultiplied with
// a high alpha, so foam genuinely covers the room and the fire behind it.
Shader "FireSim/Foam"
{
    Properties
    {
        _MainTex ("Noise (tileable)", 2D) = "gray" {}
        _Roughness ("Rim Roughness", Range(0, 1)) = 0.35
        _ShadowTint ("Shadow Tint", Color) = (0.72, 0.78, 0.88, 1)
        _Specular ("Specular", Range(0, 1)) = 0.35
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
                float _Roughness;
                half4 _ShadowTint;
                float _Specular;
            CBUFFER_END

            half4 frag(FireSimVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                float rnd = input.texcoord.w;

                float2 p = (uv - 0.5) * 2.0;
                float n = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv * 0.9 + rnd * 3.3).r;
                float r2 = dot(p, p) + (n - 0.5) * _Roughness;

                // Anti-aliased disc edge.
                float a = 1.0 - smoothstep(0.72, 1.0, r2);
                if (a <= 0.001) return half4(0, 0, 0, 0);

                float3 normal = normalize(float3(p.x, p.y, sqrt(saturate(1.0 - r2))));
                float3 lightDir = float3(-0.451, 0.702, 0.551);   // pre-normalised
                float3 viewDir = float3(0, 0, 1);

                float ndl = dot(normal, lightDir);
                float diffuse = saturate(ndl * 0.6 + 0.4);            // wrapped: foam is very scattering
                float3 h = normalize(lightDir + viewDir);
                float spec = pow(saturate(dot(normal, h)), 28.0) * _Specular;

                half3 albedo = lerp(_ShadowTint.rgb, half3(1, 1, 1), diffuse) * input.color.rgb;
                half3 rgb = albedo * (0.55 + 0.5 * diffuse) + spec;

                a *= input.color.a;
                return half4(rgb * a, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
