Shader "Painting/InkLayer"
{
    Properties
    {
        _MainTex ("InkLayer", 2D) = "white" {}
        _CanvasTex("Canvas Texture", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source Blend", Float) = 1 // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination Blend", Float) = 10 // OneMinusSrcAlpha
    }
    SubShader
    {
        ZWrite Off
        // Safe transparent rendering tags
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        
        Blend SrcAlpha OneMinusSrcAlpha
        //Blend SrcAlpha OneMinusSrcAlpha
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
                // GL.LoadOrtho() makes this perfectly map to the RenderTexture
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 stroke = tex2D(_MainTex, i.uv);
    
                // 1. UN-PREMULTIPLY THE RGB (The Magic Straight-Alpha Fix)
                // The hardware blender already multiplied the RGB by the Alpha when the brush stamped.
                // We divide it back out to restore the pure, original brush color.
                // (Using max() prevents a mathematical Divide-By-Zero error on empty pixels)
                float3 straightRGB = stroke.rgb / max(stroke.a, 0.0001);
    
                // 2. APPLY LAYER OPACITY
                // Cap the alpha based on your slider
                float finalAlpha = min(stroke.a, _Opacity);
    
                // 3. OUTPUT STRAIGHT ALPHA
                return float4(straightRGB, finalAlpha);
            }
            ENDCG
        }
    }
}
