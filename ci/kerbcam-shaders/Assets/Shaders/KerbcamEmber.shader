// kerbcam atmospheric-FX embers shader. Additive billboard sprite drawn by a
// ParticleSystem at heavy reentry. No texture — the spark sprite is a
// procedural soft circle (radial smoothstep on the UV). Per-particle colour
// (driven by the ParticleSystem's ColorOverLifetime gradient) modulates the
// final pixel so each spark cools from white-hot through orange to ash. The
// `_Intensity` uniform is set C#-side from the heavy-reentry gate so the
// overall brightness scales with how hard the vessel is heating.
//
// Render state mirrors a standard Unity Particles/Additive: Blend One One,
// ZTest LEqual, ZWrite Off, Cull Off (sprites are double-sided billboards).
Shader "Kerbcam/Ember"
{
    Properties
    {
        _Color     ("Tint", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Range(0,4)) = 1
        _SoftEdge  ("Soft Edge Start", Range(0,0.5)) = 0.35
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Pass
        {
            Blend One One   // additive
            ZWrite Off
            ZTest LEqual
            Cull Off
            Lighting Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float  _Intensity;
            float  _SoftEdge;

            // KSP FXCamera global — RGB is the stock heating colour, alpha
            // hints at real heating intensity. Lightly tints the per-particle
            // colour so embers warm visibly with stock heating.
            float4 _FXColor;

            // Particle-system shaders need the per-particle COLOR semantic
            // (appdata_base does NOT include it). We declare appdata_t
            // explicitly with the standard particle vertex layout and pass
            // colour through to the fragment so ColorOverLifetime drives the
            // sprite appearance.
            struct appdata_t
            {
                float4 vertex   : POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Soft-circle sprite: distance from UV centre → alpha falloff
                // via smoothstep. _SoftEdge controls where the fade begins;
                // it always reaches zero at the unit-disc boundary (r=0.5).
                float r = length(i.uv - 0.5);
                float a = saturate(1.0 - smoothstep(_SoftEdge, 0.5, r));

                // Per-particle colour (from ColorOverLifetime) tinted by the
                // material's _Color and lightly warmed by KSP's stock heating
                // colour (_FXColor) so sparks read hotter as real heating
                // climbs. Additive blend means the final RGB is the emitted
                // brightness; we encode the alpha-shaped falloff into RGB
                // rather than relying on the alpha channel (Blend One One
                // ignores src alpha).
                float fxHeat = saturate(_FXColor.a);
                fixed3 tint = lerp(fixed3(1, 1, 1), _FXColor.rgb * 1.4, fxHeat * 0.6);
                fixed3 rgb = i.color.rgb * _Color.rgb * tint * (a * i.color.a * _Intensity);
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
