Shader "Painting/InkLayer"
{
    Properties
    {
        _MainTex ("InkLayer", 2D) = "white" {}
        _CanvasTex("Canvas Texture", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        ZWrite Off
        // Safe transparent rendering tags
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        
        //Blend SrcAlpha OneMinusSrcAlpha, One One
        Blend One OneMinusSrcAlpha
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

                /*
                float4 stroke = tex2D(_MainTex, i.uv);
                // Cap the stroke's strength, but do NOT add the canvas here.
                float finalAlpha = min(stroke.a, _Opacity);
                
                // If stroke.a is 0, finalAlpha is 0. 
                // The hardware blender will naturally ignore the canvas where alpha is 0!
                return float4(stroke.rgb, finalAlpha);
                */
                float4 stroke = tex2D(_MainTex, i.uv);
                
                // 2. CAP THE ALPHA
                float finalAlpha = min(stroke.a, _Opacity);
                
                // 3. SCALE THE RGB (The Magic Fix)
                // Calculate the ratio between the new capped alpha and the original alpha.
                // We use max() to prevent a mathematical Divide-By-Zero error on empty pixels.
                float alphaRatio = finalAlpha / max(stroke.a, 0.0001); 
                
                // Scale the premultiplied RGB down so it matches the new capped alpha.
                float3 finalRGB = stroke.rgb * alphaRatio; 

                return float4(finalRGB, finalAlpha);


            }
            ENDCG
        }
    }
}