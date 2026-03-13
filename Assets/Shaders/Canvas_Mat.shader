Shader "Painting/Canvas"
{
    Properties
    {
        _MainTex ("Canvas RT", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,0)
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        Blend One Zero
        ZWrite On

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
                float4 canvas = tex2D(_MainTex, i.uv);

                return clamp(canvas, 0, 1);
            }
            ENDCG
        }
    }
}