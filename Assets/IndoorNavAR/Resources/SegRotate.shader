// Guardar como: Assets/IndoorNavAR/Resources/SegRotate.shader
// ⚠️  DEBE estar en una carpeta llamada exactamente "Resources"
//     para que Shader.Find("Hidden/SegRotate") funcione en builds Android.
//
// ============================================================================
//  CAMBIOS v2
// ============================================================================
//  FIX — _FlipX añadido
//    ARCore en algunos dispositivos entrega el frame con MirrorX además de
//    MirrorY. Con solo _FlipY=1 la máscara queda espejada horizontalmente.
//    Ahora _FlipX y _FlipY son independientes.
//
//    Combinaciones para calibrar en runtime (usar ContextMenu en Controller):
//      Máscara espejada en X  → activar _FlipX=1
//      Máscara invertida en Y → activar _FlipY=1
//      Ambos flip se aplican ANTES de la rotación.

Shader "Hidden/SegRotate"
{
    Properties
    {
        _MainTex  ("Texture",  2D)    = "white" {}
        _Rotation ("Rotation", Float) = 0
        _FlipY    ("Flip Y",   Float) = 1
        _FlipX    ("Flip X",   Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv     : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float     _Rotation;
            float     _FlipY;
            float     _FlipX;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                return o;
            }

            float2 RotateUV(float2 uv, float deg)
            {
                uv -= 0.5;
                float rad = deg * (3.14159265 / 180.0);
                float s   = sin(rad);
                float c   = cos(rad);
                uv = float2(c * uv.x - s * uv.y, s * uv.x + c * uv.y);
                uv += 0.5;
                return uv;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                if (_FlipX > 0.5) uv.x = 1.0 - uv.x;
                uv = clamp(RotateUV(uv, _Rotation), 0.0, 1.0);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
