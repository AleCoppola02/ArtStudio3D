Shader "Painting/InkLayer"
{
    Properties
    {
        _MainTex ("InkLayer", 2D) = "white" {}
        _CanvasTex("Canvas Texture", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,0)
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        ZWrite On
        // Safe transparent rendering tags
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        
        Blend SrcAlpha OneMinusSrcAlpha, One One
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
            float4 _Color;
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
                float4 canvas = tex2D(_CanvasTex, i.uv);
                float addedAlpha= stroke.a + canvas.a;
                
                //where there is no stroke (stroke.a is 0), set addedAlpha to 0 to avoid affecting the canvas
                addedAlpha = lerp(0, addedAlpha, step(0.001, stroke.a));
                float finalAlpha = min(addedAlpha, _Opacity);
    

                return float4(_Color.rgb, finalAlpha);
            }
            ENDCG
        }
    }
}