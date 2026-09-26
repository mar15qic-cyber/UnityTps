Shader "Hidden/UnityFps/EnemySilhouetteOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.55, 0.04, 0.06, 0.85)
        _OutlinePixels ("Outline Pixels", Range(1, 2)) = 1.5
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+10" }
        Pass
        {
            Name "DepthTestedOutline"
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _OutlineColor;
            float _OutlinePixels;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(input.normalOS);
                float3 viewNormal = TransformWorldToViewDir(worldNormal, true);
                float2 direction = normalize(viewNormal.xy + float2(0.00001, 0.00001));
                output.positionCS.xy += direction * (2.0 * _OutlinePixels / _ScreenParams.xy) * output.positionCS.w;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target { return _OutlineColor; }
            ENDHLSL
        }
    }
}
