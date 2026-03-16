Shader "UI/HueGradient"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {} 
        // 0 = Horizontal Slider, 1 = Vertical Slider
        [Toggle] _IsVertical ("Is Vertical", Float) = 1 
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            float _IsVertical;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 HSVtoRGB(float3 hsv)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(hsv.xxx + K.xyz) * 6.0 - K.www);
                return hsv.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), hsv.y);
            }

            float4 frag (v2f i) : SV_Target
            {
                // If vertical, use the Y axis for the hue gradient. If horizontal, use X.
                float currentHue = lerp(i.uv.x, i.uv.y, _IsVertical);
                
                // Return the pure hue (Saturation = 1, Value/Brightness = 1)
                float3 gradientColor = HSVtoRGB(float3(currentHue, 1.0, 1.0));
                
                return float4(gradientColor, 1.0);
            }
            ENDCG
        }
    }
}