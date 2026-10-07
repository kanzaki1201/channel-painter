Shader "Hidden/ChannelPainter/Brush"
{
    Properties
    {
        // Graphics.Blit binds its source to the main texture, which the Blit passes read as _MainTex.
        [MainTexture] _MainTex ("Blit Source", 2D) = "white" {}
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
        #include "UnityCG.cginc"

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            float4 color : COLOR;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
            float4 color : COLOR;
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
            output.color = input.color;
            return output;
        }
        ENDHLSL

        Pass
        {
            Conservative True
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
            float _BrushSpace;
            float2 _BrushCenterUV;
            float2 _CanvasSize;
            float _BrushMode;
            float4x4 _ScreenViewProj;
            float4x4 _ScreenView;
            float2 _CursorViewport;
            float2 _ScreenSize;
            sampler2D _ScreenDepth;

            float4 Brush(Varyings input) : SV_Target
            {
                float4 source = tex2D(_Source, input.uv);
                float distanceToBrush;
                if (_BrushSpace > 1.5)
                {
                    float4 world = float4(input.positionWS, 1.0);
                    float4 projected = mul(_ScreenViewProj, world);
                    clip(projected.w);
                    float2 viewport = projected.xy / projected.w * 0.5 + 0.5;
                    clip(min(min(viewport.x, viewport.y), min(1.0 - viewport.x, 1.0 - viewport.y)));
                    float eyeDepth = -mul(_ScreenView, world).z;
                    float storedDepth = tex2D(_ScreenDepth, viewport).r;
                    clip(storedDepth + max(0.001, 0.002 * eyeDepth) - eyeDepth);
                    distanceToBrush = length((viewport - _CursorViewport) * _ScreenSize);
                }
                else if (_BrushSpace > 0.5)
                    distanceToBrush = length((input.uv - _BrushCenterUV) * _CanvasSize);
                else
                    distanceToBrush = distance(input.positionWS, _BrushCenter);
                float inner = _BrushHardness * _BrushRadius;
                float falloff = _BrushHardness >= 1.0
                    ? step(distanceToBrush, _BrushRadius)
                    : 1.0 - smoothstep(inner, _BrushRadius, distanceToBrush);
                float weight = saturate(_BrushStrength * falloff);
                // Overlapping UV triangles would otherwise write source back over fresh paint.
                clip(weight - 1e-5);
                float4 delta = weight * _ChannelMask;
                if (_BrushMode > 1.5)
                    return saturate(source - _BrushValue * delta);
                if (_BrushMode > 0.5)
                    return saturate(source + _BrushValue * delta);
                return lerp(source, float4(_BrushValue, _BrushValue, _BrushValue, _BrushValue), delta);
            }
            ENDHLSL
        }

        Pass
        {
            Conservative True
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Coverage

            float4 Coverage(Varyings input) : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Conservative True
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment VertexColors

            float _HasVertexColor;

            float4 VertexColors(Varyings input) : SV_Target
            {
                return _HasVertexColor > 0.5 ? input.color : float4(1.0, 1.0, 1.0, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment DilateColor

            sampler2D _MainTex;
            sampler2D _Coverage;
            float4 _MainTex_TexelSize;

            float4 DilateColor(v2f_img input) : SV_Target
            {
                float4 current = tex2D(_MainTex, input.uv);
                if (tex2D(_Coverage, input.uv).r > 0.5)
                    return current;

                float4 sum = 0;
                float count = 0;
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0)
                        continue;
                    float2 neighbor = input.uv + float2(x, y) * _MainTex_TexelSize.xy;
                    if (any(neighbor < 0.0) || any(neighbor > 1.0))
                        continue;
                    if (tex2D(_Coverage, neighbor).r <= 0.5)
                        continue;
                    sum += tex2D(_MainTex, neighbor);
                    count += 1.0;
                }
                return count > 0.0 ? sum / count : current;
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment DilateCoverage

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 DilateCoverage(v2f_img input) : SV_Target
            {
                if (tex2D(_MainTex, input.uv).r > 0.5)
                    return 1.0;

                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0)
                        continue;
                    float2 neighbor = input.uv + float2(x, y) * _MainTex_TexelSize.xy;
                    if (any(neighbor < 0.0) || any(neighbor > 1.0))
                        continue;
                    if (tex2D(_MainTex, neighbor).r > 0.5)
                        return 1.0;
                }
                return 0.0;
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment SampleVertex

            sampler2D _MainTex;
            sampler2D _VertexUV;

            float4 SampleVertex(v2f_img input) : SV_Target
            {
                float2 uv = tex2D(_VertexUV, input.uv).rg;
                return tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
            }
            ENDHLSL
        }

        Pass
        {
            Cull Off
            ZTest LEqual
            ZWrite On
            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment

            float4x4 _DepthViewProj;
            float4x4 _ScreenView;

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float eyeDepth : TEXCOORD0;
            };

            DepthVaryings DepthVertex(Attributes input)
            {
                DepthVaryings output;
                float4 world = mul(_BrushMatrix, input.positionOS);
                output.positionCS = mul(_DepthViewProj, world);
                output.eyeDepth = -mul(_ScreenView, world).z;
                return output;
            }

            float DepthFragment(DepthVaryings input) : SV_Target
            {
                return input.eyeDepth;
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment CopyChannel

            sampler2D _MainTex;
            float4 _FromMask;
            float4 _ToMask;

            float4 CopyChannel(v2f_img input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv);
                float value = dot(color, _FromMask);
                return lerp(color, float4(value, value, value, value), _ToMask);
            }
            ENDHLSL
        }
    }
}
