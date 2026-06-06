Shader "Painting/LivePreview"
{
    Properties
    {
        _MainTex ("World Space InkLayer", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5

        // Fixed to ONE since we are outputting Premultiplied RGB at the end!
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendColor ("Source Blend Color", Float) = 1 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendColor ("Destination Blend Color", Float) = 10 
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Destination Blend Alpha", Float) = 10 
    }
    SubShader
    {
        ZWrite Off
        ZTest Always // Ensure it draws perfectly over the SVT Canvas
        Cull Off // Ensure we can see it from any angle
        Tags { "RenderType"="Transparent" "Queue"="Transparent+1" } 
        
        Blend [_SrcBlendColor] [_DstBlendColor], [_SrcBlendAlpha] [_DstBlendAlpha]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { 
                float4 vertex : POSITION; 
                float2 uv : TEXCOORD0; // <--- 1. Grab the physical UVs of the 3D Quad
            };
            
            struct v2f { 
                float4 vertex : SV_POSITION; 
                float2 uv : TEXCOORD0; // <--- 2. Pass them to the fragment shader
            };

            v2f vert (appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; // <--- 3. Standard 0..1 Mapping! No more ScreenPos projection!
                return o;
            }

            sampler2D _MainTex;
            float _Opacity;

            float4 frag (v2f i) : SV_Target {
                // 4. THE MAGIC: Sample the texture using the Quad's physical geometry!
                float4 stroke = tex2D(_MainTex, i.uv);
    
                // Un-premultiply (safe straight alpha fix)
                float3 straightRGB = stroke.a > 0.001 ? (stroke.rgb / stroke.a) : float3(0,0,0);
                
                // Cap opacity
                float finalAlpha = min(stroke.a, _Opacity);
                
                // Re-premultiply
                float3 premultipliedRGB = straightRGB * finalAlpha;
                
                return float4(premultipliedRGB, finalAlpha);
            }
            ENDCG
        }
    }
}