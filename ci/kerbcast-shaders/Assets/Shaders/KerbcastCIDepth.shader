// CI-ONLY replacement shader: writes linear01 view depth into the red
// channel. Used by RenderFxPreviews' windward depth prepass to build a
// real _FXDepthMap for the proxy vessel — KSP publishes one from its
// FXCamera every frame, and the plasma shader's wrap term (the white
// wind sheath hugging the windward surfaces) is ZERO without it; the old
// flat placeholder silently deleted that whole layer from every preview.
//
// Deliberately has NO committed .meta: Unity regenerates one per CI run
// with no assetBundle tag, so this never ships in the kerbcast-shaders
// bundle.
Shader "Kerbcast/CIDepth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float  viewZ : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.viewZ = -UnityObjectToViewPos(vertex.xyz).z;
                return o;
            }

            // _ProjectionParams: y = near, z = far (of the rendering camera).
            // Linear01 between near and far — the same scale the plasma
            // shader's wrapFromDepthMap assumes when it converts a depth
            // difference into metres downstream.
            fixed4 frag(v2f i) : SV_Target
            {
                float d = saturate((i.viewZ - _ProjectionParams.y)
                                   / max(_ProjectionParams.z - _ProjectionParams.y, 0.001));
                return fixed4(d, d, d, 1);
            }
            ENDCG
        }
    }
}
