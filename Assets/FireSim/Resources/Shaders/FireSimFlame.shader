// Procedural flame tongue for a billboard particle. Two layers of tileable noise scroll up the
// quad and push the sides of a tapered silhouette around; the tongue then erodes away as the
// particle ages, tips first, which is what real flame licks do. Colour comes from a black-body
// style ramp indexed by how hot each pixel is, so cores read white-yellow and edges deep red.
Shader "FireSim/Flame"
{
    Properties
    {
        _MainTex ("Noise (tileable)", 2D) = "gray" {}
        _Ramp ("Fire Ramp", 2D) = "white" {}
        _ScrollSpeed ("Scroll Speed", Float) = 1.3
        _Distort ("Side Distortion", Range(0, 1)) = 0.5
        _Sharpness ("Edge Sharpness", Float) = 5.5
        _Intensity ("Intensity", Float) = 1.7
        _Occlusion ("Occlusion", Range(0, 1)) = 0.3
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
            TEXTURE2D(_Ramp);    SAMPLER(sampler_Ramp);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _ScrollSpeed;
                float _Distort;
                float _Sharpness;
                float _Intensity;
                float _Occlusion;
            CBUFFER_END

            half4 frag(FireSimVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                float age = input.texcoord.z;
                float rnd = input.texcoord.w;

                // Two noise layers moving upward (toward uv.y = 1) at different rates, offset by
                // the particle's random so no two tongues flicker in step.
                float t = _Time.y * _ScrollSpeed;
                float2 uvA = uv * float2(1.0, 0.65) + float2(rnd * 3.17, -t + rnd * 4.0);
                float2 uvB = uv * float2(1.9, 1.25) + float2(-rnd * 5.3, -t * 1.55 + rnd * 7.0);
                float n = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvA).r * 0.6 +
                          SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvB).g * 0.4;

                // Tapered silhouette, wide at the base and narrow at the tip, with the noise
                // dragging its edges sideways more the higher up the tongue it is.
                float taper = lerp(0.46, 0.07, uv.y);
                float x = abs(uv.x - 0.5) + (n - 0.5) * _Distort * (0.25 + uv.y);
                float body = saturate(1.0 - x / taper);
                body *= smoothstep(0.0, 0.16, uv.y);        // soft base so it sits into the surface
                body *= 1.0 - smoothstep(0.5, 1.0, uv.y);   // thins out toward the tip

                // Erosion: the threshold climbs with age (and height), burning the tongue away.
                float threshold = lerp(0.06, 0.78, age) + uv.y * 0.22;
                float heat = saturate((body * (0.5 + n * 0.95) - threshold) * _Sharpness);

                half3 ramp = SAMPLE_TEXTURE2D(_Ramp, sampler_Ramp, float2(heat, 0.5)).rgb;
                half a = heat * input.color.a;
                half3 rgb = ramp * input.color.rgb * (_Intensity * a);
                return half4(rgb, a * _Occlusion);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
