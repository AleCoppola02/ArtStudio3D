Shader "Painting/TextureBrush"
{
    Properties
    {
        _MainTex ("Brush Shape", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,1)
        _Flow ("Flow", Range(0,1)) = 0.1
        _Softness ("Edge Softness", Range(0.001, 1.0)) = 0.1
        // Expose Source and Destination Blend Modes to the inspector/code
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendColor ("Source Blend Color", Float) = 5 // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendColor ("Destination Blend Color", Float) = 10 // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1 // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Destination Blend Alpha", Float) = 10 // OneMinusSrcAlpha
    }
    SubShader
    {
        // Safe transparent rendering tags
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        
        // Ignores the scene depth entirely
        ZWrite Off
        ZTest Always
        Cull Off


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
            float4 _Color;
            float _Flow;
            float _Softness;

            v2f vert (appdata v)
            {
                v2f o;
                // GL.LoadOrtho() makes this perfectly map to the RenderTexture
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4  frag (v2f i) : SV_Target
            {
                // 1. Find the exact center of the UV coordinates
                float2 center = float2(0.5, 0.5);
                
                // 2. Calculate the distance from the pixel to the center
                float dist = distance(i.uv, center);
                
                // 3. Define the radius of the brush tip (0.5 touches the edges of the quad)
                float radius = 0.5;

                // 4. Calculate perfect 1-pixel anti-aliasing based on screen/texture resolution
                float aa = fwidth(dist);

                // 5. Combine the perfect anti-aliasing with your user's _Softness slider.
                // We use max() to ensure the edge never gets sharper than 1 physical pixel, 
                // preventing it from ever looking jagged.
                float edgeBlur = max(aa, _Softness);

                // 6. Generate the shape using smoothstep!
                // smoothstep(outerEdge, innerEdge, value)
                // If the pixel is outside the radius, it returns 0.
                // If it's inside (radius - edgeBlur), it returns 1. 
                // In between, it creates a perfectly smooth gradient.
                float shape = smoothstep(radius, radius - edgeBlur, dist);

                float alpha = _Flow * shape; 

                return float4(_Color.rgb, alpha);
            }
            
            ENDCG
        }
    }
}

