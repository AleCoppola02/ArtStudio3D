Shader "Painting/CompositeShader"
{
    Properties
    {
        _MainTex ("Ink Layer (Auto)", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float _Opacity;

            v2f vert (appdata_base v) 
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord; 
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // 1. Get the accumulated ink strength from your brush buffer
                float4 stroke = tex2D(_MainTex, i.uv);

                return float4(stroke);

            }
            ENDCG
        }
    }
}