// Scorch decal that sits flat on the burning surface. Two effects share the quad:
//   * charring: a sooty disc that darkens the real surface (alpha only, no colour) and builds up
//     the longer the patch burns, then stays behind as the mark the fire leaves;
//   * embers: glowing cracks in the middle of the char, additive, driven by _Glow so they pulse
//     with the flames and keep smouldering after the flames are knocked down.
Shader "FireSim/Scorch"
{
    Properties
    {
        _MainTex ("Noise (tileable)", 2D) = "gray" {}
        _Char ("Char", Range(0, 1)) = 0.0
        _Glow ("Ember Glow", Float) = 0.0
        _EmberColor ("Ember Colour", Color) = (1.0, 0.36, 0.08, 1)
        _Fade ("Fade", Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Char;
                float _Glow;
                half4 _EmberColor;
                float _Fade;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 p = (input.uv - 0.5) * 2.0;
                float r = length(p);
                float n1 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * 1.3).r;
                float n2 = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * 3.1 + 0.37).g;

                // Char: an irregular sooty disc, dense in the middle and ragged at the edge.
                float edge = 1.0 - smoothstep(0.35, 1.0, r + (n1 - 0.5) * 0.55);
                float soot = edge * (0.55 + 0.45 * n2);
                float charA = saturate(soot * _Char) * 0.85;

                // Embers: the brightest veins of the fine noise, only near the centre, flickering
                // via _Glow which the patch drives every frame.
                float veins = pow(saturate(n2 * 1.25 - 0.15), 3.0);
                float core = 1.0 - smoothstep(0.1, 0.8, r + (n1 - 0.5) * 0.3);
                half3 glow = _EmberColor.rgb * (veins * core * _Glow);

                return half4(glow * _Fade, charA * _Fade);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
