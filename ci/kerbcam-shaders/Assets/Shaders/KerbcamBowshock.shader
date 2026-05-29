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

                // Slow rearward scroll along the cone axis. Local +Z is the
                // apex direction (apex points along velocity); subtracting
                // _Time.y * speed moves the wave toward -Z, i.e. toward the
                // vessel — air rushing through the shock front.
                float scroll = sin(i.localPos.z * 1.5 - _Time.y * _ScrollSpeed);
                float shimmer = 0.85 + 0.15 * scroll;

                // Soft interior glow so the cone isn't pure silhouette; the
                // rim still dominates by a wide margin.
                float baseGlow = 0.18;
                float glow = (baseGlow + rim) * shimmer * _Intensity;

                float plasmaShift = smoothstep(_PlasmaOnset, 1.0, _Intensity);
                float3 col = lerp(_WindColor.rgb, _PlasmaColor.rgb, plasmaShift);
                return fixed4(col * glow, 1.0);
            }
            ENDCG
        }
    }
}
