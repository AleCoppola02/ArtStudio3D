Shader "UI/ColorPalette"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Hue ("Hue", Range(0,1)) = 0.0
        
        // --- NEW CURSOR PROPERTIES ---
        _CursorPos ("Cursor Position", Vector) = (1, 1, 0, 0)
        _CursorColor ("Cursor Color", Color) = (1, 1, 1, 1)
        _CursorRadius ("Cursor Radius", Range(0.01, 0.1)) = 0.03
        _CursorThickness ("Cursor Thickness", Range(0.001, 0.05)) = 0.008
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

            float _Hue;
            float4 _CursorPos;
            float4 _CursorColor;
            float _CursorRadius;
            float _CursorThickness;

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
                // 1. Calculate the base HSV color
                float saturation = i.uv.x;
                float value = i.uv.y;
                float3 baseColor = HSVtoRGB(float3(_Hue, saturation, value));
                
                // 2. Calculate distance from this pixel to the cursor position
                float dist = distance(i.uv, _CursorPos.xy);
                
                // 3. Draw the ring using smoothstep for clean, anti-aliased edges
                float halfThickness = _CursorThickness * 0.5;
                
                // Creates the outer and inner boundaries of the ring
                float outerEdge = smoothstep(_CursorRadius + halfThickness, _CursorRadius + halfThickness - 0.002, dist);
                float innerEdge = smoothstep(_CursorRadius - halfThickness, _CursorRadius - halfThickness + 0.002, dist);
                
                // Multiply them together: this equals 1.0 ONLY inside the ring's thickness
                float ringMask = outerEdge * innerEdge;
                
                // 4. To make sure the cursor is visible even on white/bright backgrounds, 
                // we can add a tiny dark drop-shadow by shifting the distance check slightly
                float shadowDist = distance(i.uv, _CursorPos.xy + float2(0.005, -0.005));
                float shadowMask = smoothstep(_CursorRadius + halfThickness + 0.01, _CursorRadius - halfThickness, shadowDist) * (1.0 - ringMask);
                
                // Apply the shadow, then apply the white ring on top
                float3 finalColor = lerp(baseColor, float3(0,0,0), shadowMask * 0.5); // 50% opacity shadow
                finalColor = lerp(finalColor, _CursorColor.rgb, ringMask * _CursorColor.a);
                
                return float4(finalColor, 1.0);
            }
            ENDCG
        }
    }
}