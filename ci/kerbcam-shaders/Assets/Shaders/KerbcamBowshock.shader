// kerbcam atmospheric-FX bowshock shader. Drawn additively over the near
// render (CommandBuffer at AfterForwardAlpha) so it composites against that
// render's depth — correct occlusion against the vessel hull, no second camera.
//
// Look: a hollow shock cone in front of the vessel along the velocity vector.
// The silhouette/edge of the cone reads brightest (fresnel falloff so 1 - |n·v|
// is near 1 at grazing angles, ~0 looking straight through the slant surface).
// Cull Off draws both sides — the |n·v| (abs) keeps the rim glow correct on
// either face. Colour ramp matches the plasma shader convention:
// _WindColor → _PlasmaColor via smoothstep(_PlasmaOnset, 1.0, _Intensity).
// Animation: a slow rearward scroll (toward the vessel, toward -Z in local
// space) so the surface implies air rushing past the shock front.
Shader "Kerbcam/Bowshock"
{
    Properties
    {
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.85, 0.92, 1.0, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.45, 0.15, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _RimPower    ("Rim Power", Range(0.5,8)) = 2.5
        _ScrollSpeed ("Scroll Speed", Float) = 1.5
        // Plasma colour only blends in above this intensity (reserved for
        // heavy reentry); below it stays wind-white. Matches KerbcamPlasma.
        _PlasmaOnset ("Plasma Onset", Range(0,1)) = 0.85
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Blend One One   // additive overlay
            ZWrite Off
            ZTest LEqual    // occlude against the near render's depth buffer
            Cull Off        // hollow cone — show both sides

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _WindColor;
            float4 _PlasmaColor;
            float  _Intensity;
            float  _RimPower;
            float  _ScrollSpeed;
            float  _PlasmaOnset;

            // KSP FXCamera globals — published process-wide by the stock
            // aero-FX system. Provide the same tuned plasma noise texture
            // and real-heating colour stock plasma uses, so the bowshock
            // visually agrees with KSP's own FX in colour and animation.
            sampler2D _FXMainTex;
            float4    _FXColor;     // .rgb stock heating colour, .a heating hint

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                float3 localPos   : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.localPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float3 n = normalize(i.worldNormal);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Fresnel-style silhouette glow. abs(dot()) keeps the rim
                // correct on the backside since Cull Off draws both faces;
                // flipped backface normals would otherwise punch out the
                // interior view.
                float ndv = abs(dot(n, viewDir));
                float rim = pow(1.0 - saturate(ndv), _RimPower);

                // Sample KSP's tuned plasma noise texture, scrolled along the
                // cone axis toward -Z (toward the vessel — air rushing through
                // the shock front). Visual continuity with stock plasma.
                float2 fxUv = float2(i.localPos.x * 0.3 + i.localPos.y * 0.2,
                                     i.localPos.z * 0.4 - _Time.y * _ScrollSpeed * 0.15);
                float fxNoise = tex2D(_FXMainTex, fxUv).r;
                float shimmer = 0.7 + 0.6 * fxNoise;

                // Soft interior glow so the cone isn't pure silhouette; the
                // rim still dominates by a wide margin.
                float baseGlow = 0.18;
                float glow = (baseGlow + rim) * shimmer * _Intensity;

                // Wind→plasma colour ramp, then tinted toward KSP's stock
                // heating colour (_FXColor) at high real heating. Stays
                // wind-white when stock heating is cold.
                float plasmaShift = smoothstep(_PlasmaOnset, 1.0, _Intensity);
                float3 baseCol = lerp(_WindColor.rgb, _PlasmaColor.rgb, plasmaShift);
                float fxHeat = saturate(_FXColor.a);
                float3 col = lerp(baseCol, baseCol * (0.4 + _FXColor.rgb * 1.6), fxHeat * plasmaShift);
                return fixed4(col * glow, 1.0);
            }
            ENDCG
        }
    }
}
