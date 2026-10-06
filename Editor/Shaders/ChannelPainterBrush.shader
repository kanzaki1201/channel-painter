Shader "Hidden/ChannelPainter/Brush"
{
    Properties
    {
        _Source ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZTest Always
        ZWrite Off
        Blend Off

        HLSLINCLUDE
        #include "HLSLSupport.cginc"

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
        };

        float4x4 _BrushMatrix;

        Varyings Vert(Attributes input)
        {
            Varyings output;
            float2 clip = input.uv * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
            clip.y = -clip.y;
            #endif
            output.positionCS = float4(clip, 0.0, 1.0);
            output.uv = input.uv;
            output.positionWS = mul(_BrushMatrix, input.positionOS).xyz;
            return output;
        }
        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Brush

            sampler2D _Source;
            float3 _BrushCenter;
            float _BrushRadius;
            float _BrushHardness;
            float _BrushStrength;
            float _BrushValue;
            float4 _ChannelMask;

            float4 Brush(Varyings input) : SV_Target
            {
                float4 source = tex2D(_Source, input.uv);
                float distanceToBrush = distance(input.positionWS, _BrushCenter);
                float inner = _BrushHardness * _BrushRadius;
                float falloff = _BrushHardness >= 1.0
                    ? step(distanceToBrush, _BrushRadius)
                    : 1.0 - smoothstep(inner, _BrushRadius, distanceToBrush);
                return lerp(source, float4(_BrushValue, _BrushValue, _BrushValue, _BrushValue),
                    saturate(_BrushStrength * falloff) * _ChannelMask);
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Coverage

            float4 Coverage(Varyings input) : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
