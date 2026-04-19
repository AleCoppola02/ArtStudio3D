Shader "Painting/LivePreview"
{
    Properties
    {
        _MainTex ("Screen Space InkLayer", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendColor ("Source Blend Color", Float) = 5 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendColor ("Destination Blend Color", Float) = 10 
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Source Blend Alpha", Float) = 5 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Destination Blend Alpha", Float) = 10 
    }
    SubShader
    {
        ZWrite Off
        ZTest Always // Ensure it draws perfectly over the SVT Canvas
        Tags { "RenderType"="Transparent" "Queue"="Transparent+1" } 
        
        Blend [_SrcBlendColor] [_DstBlendColor], [_SrcBlendAlpha] [_DstBlendAlpha]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { 
                float4 vertex : SV_POSITION; 
                float4 screenPos : TEXCOORD0; // <--- NEW: Track the physical screen position!
            };

            v2f vert (appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.vertex); // <--- NEW: Project the Camera lens
                return o;
            }

            sampler2D _MainTex;
            float _Opacity;

            float4 frag (v2f i) : SV_Target {
                // THE MAGIC: Sample the 1920x1080 RT using the Camera's monitor coordinates!
                float2 screenUV = i.screenPos.xy;
                float4 stroke = tex2D(_MainTex, screenUV);
    
                // (The exact same straight-alpha math from your original InkLayer)
                float strokeAlpha = stroke.a * _Opacity; 
                float3 straightRGB = stroke.rgb / max(stroke.a, 0.0001);
                float finalAlpha = min(strokeAlpha, _Opacity);
                float3 premultipliedRGB = straightRGB * finalAlpha;
                
                return float4(premultipliedRGB, finalAlpha);
            }
            ENDCG
        }
    }
}