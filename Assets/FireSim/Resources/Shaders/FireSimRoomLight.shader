// Fire light on the real room. Applied to the MRUK effect meshes (the invisible planes and boxes
// that stand in for the scanned walls, floor and furniture). The mesh itself never shows: this
// pass only ADDS the warm light every burning patch throws onto it, so in passthrough the actual
// table and floor appear lit by the flames. Light data arrives as global arrays set each frame
// by FireLightField, which keeps the effect independent of URP's per-object light limit.
Shader "FireSim/RoomLight"
{
    Properties
    {
        _Gain ("Gain", Float) = 1.0
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

            Blend One One, Zero One
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define FIRESIM_MAX_LIGHTS 16

            // Globals, not per-material: set via Shader.SetGlobalVectorArray.
            float4 _FireSimLightPos[FIRESIM_MAX_LIGHTS];    // xyz world position, w range (m)
            float4 _FireSimLightColor[FIRESIM_MAX_LIGHTS];  // rgb colour * intensity
            float  _FireSimLightCount;

            CBUFFER_START(UnityPerMaterial)
                float _Gain;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 n = normalize(input.normalWS);
                int count = (int)_FireSimLightCount;
                float3 acc = 0;

                for (int k = 0; k < FIRESIM_MAX_LIGHTS; k++)
                {
                    if (k >= count) break;
                    float4 l = _FireSimLightPos[k];
                    float3 d = l.xyz - input.positionWS;
                    float dist = max(length(d), 0.04);
                    float att = saturate(1.0 - dist / max(l.w, 0.01));
                    att *= att;
                    // Wrapped so the surface the fire sits ON (where the light is nearly
                    // grazing) still picks up a warm pool around the base.
                    float ndl = saturate((dot(n, d / dist) + 0.4) / 1.4);
                    acc += _FireSimLightColor[k].rgb * (att * ndl);
                }

                return half4(acc * _Gain, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
