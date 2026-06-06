Shader "Painting/InkLayer"
{
    Properties
    {
        _MainTex ("InkLayer", 2D) = "white" {}
        _CanvasTex("Canvas Texture", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5
        
        // Outputting Premultiplied RGB means Source MUST be 1 (One).
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendColor ("Source Blend Color", Float) = 1 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendColor ("Destination Blend Color", Float) = 10 
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Destination Blend Alpha", Float) = 10 
    }
    SubShader
    {
        ZWrite Off
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off
        ZTest Always
        
        Blend [_SrcBlendColor] [_DstBlendColor], [_SrcBlendAlpha] [_DstBlendAlpha]
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            sampler2D _CanvasTex;
            float _Opacity;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 stroke = tex2D(_MainTex, i.uv);
    
                // Safe Straight-Alpha Recovery
                // If alpha is functionally 0, force RGB to 0 to prevent glowing halos.
                float3 straightRGB = stroke.a > 0.001 ? (stroke.rgb / stroke.a) : float3(0, 0, 0);
    
                // Photoshop-style Opacity Max Capping
                // Allows the brush to build up organically, but places a hard cap.
                float finalAlpha = min(stroke.a, _Opacity);
                
                // Repremultiply for final output
                float3 premultipliedRGB = straightRGB * finalAlpha;
                
                return float4(premultipliedRGB, finalAlpha);
            }
            ENDCG
        }
    }
}