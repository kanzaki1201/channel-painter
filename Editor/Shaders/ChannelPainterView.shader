Shader "Hidden/ChannelPainter/View"
{
    Properties
    {
        _MainTex ("Canvas", 2D) = "white" {}
    }
    SubShader
    {
        // Transparent queue so the mask always draws after the opaque target it overlays.
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        float4x4 _MaskMatrix;
        float _UseVertexColor;
        float _ViewChannel;

        half4 ViewChannel(half4 value)
        {
            if (_ViewChannel < 0.5)
                return half4(value.rgb, 1);
            half channel = _ViewChannel < 1.5 ? value.r
                : _ViewChannel < 2.5 ? value.g
                : _ViewChannel < 3.5 ? value.b : value.a;
            return half4(channel, channel, channel, 1);
        }
        ENDHLSL

        Pass
        {
            Name "Mask"
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex MaskVertex
            #pragma fragment MaskFragment

            struct MaskAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct MaskVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            MaskVaryings MaskVertex(MaskAttributes input)
            {
                MaskVaryings output;
                float4 positionWS = mul(_MaskMatrix, input.positionOS);
                output.positionCS = mul(UNITY_MATRIX_VP, positionWS);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 MaskFragment(MaskVaryings input) : SV_Target
            {
                half4 value = _UseVertexColor > 0.5
                    ? input.color : SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                return ViewChannel(value);
            }
            ENDHLSL
        }

        Pass
        {
            Name "View2D"
            Tags { "LightMode" = "ChannelPainter2D" }
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex ViewVertex
            #pragma fragment ViewFragment

            struct ViewAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct ViewVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            ViewVaryings ViewVertex(ViewAttributes input)
            {
                ViewVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 ViewFragment(ViewVaryings input) : SV_Target
            {
                return ViewChannel(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv));
            }
            ENDHLSL
        }
    }
}
