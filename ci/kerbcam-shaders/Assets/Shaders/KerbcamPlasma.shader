// kerbcam atmospheric-FX core sheath shader. Drawn additively over a
// procedural shell mesh in the near pass (CommandBuffer at AfterForwardAlpha)
// — composites against the near render's depth, so vessel parts in front of
// the shell correctly occlude it.
//
// The shell is shaped and shaded to telegraph wind direction by glance:
//   - the *base shape* bulges forward (windward) and compresses behind, via
//     vertex displacement biased by wind alignment;
//   - the *glow* is concentrated on the windward face and falls off sharply
//     to dark on the leeward side;
//   - *streaks* are scrolled boldly along the wind axis, gated by the
//     windward term so they read as pulling backward off the front.
//
// Per-vertex noise displacement gives lots of independent variation across
// the ~800-vert proxy mesh — the silhouette doesn't reveal the vessel.
Shader "Kerbcam/Plasma"
{
    Properties
    {
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.65, 0.75, 0.85, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.5, 0.2, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _WindDirWorld("Wind Dir (world)", Vector) = (0,0,1,0)
        _NoiseAmount ("Noise Displacement (m)", Range(0,4)) = 1.5
        _NoiseSpeed  ("Noise Speed", Float) = 2.0
        _StreakSpeed ("Streak Speed", Float) = 5.0
        _PlasmaOnset ("Plasma Onset", Range(0,1)) = 0.85
        _RimPower    ("Rim Power", Range(0.5,8)) = 2.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Pass
        {
            Blend One One   // additive overlay
            ZWrite Off
            ZTest LEqual    // occlude against the near render's depth buffer
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _WindColor;
            float4 _PlasmaColor;
            float  _Intensity;
            float4 _WindDirWorld;
            float  _NoiseAmount;
            float  _NoiseSpeed;
            float  _StreakSpeed;
            float  _PlasmaOnset;
            float  _RimPower;

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                float  windAlign  : TEXCOORD2; // dot(normal, wind): +1 windward, -1 leeward
            };

            // Multi-octave layered noise — many vertices on the proxy mesh, so
            // adjacent verts land at independent phases and the displacement
            // reads as turbulent cloud rather than smooth waves.
            float noise3(float3 p, float t)
            {
                return sin(p.x * 1.1 + t) * 0.50
                     + sin(p.y * 1.7 + t * 0.8 + 1.3) * 0.40
                     + sin(p.z * 1.5 - t * 0.6) * 0.35
                     + sin(p.x * 3.1 + p.z * 2.3 + t * 1.3) * 0.25
                     + sin(p.y * 2.7 - p.z * 1.9 - t * 0.9 + 0.4) * 0.20;
            }

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 wind = normalize(_WindDirWorld.xyz);

                float windAlign = dot(worldNormal, wind); // +1 facing wind, -1 trailing

                // Wind-biased shape: bulge forward (windward face puffs out) and
                // compress behind. This is what makes the *silhouette* telegraph
                // the direction of motion.
                float asymmetry = windAlign * 0.4 * _NoiseAmount;

                // Layered noise displacement, amplified on the windward face
                // (more turbulence where air piles up) and quieter behind.
                float n = noise3(worldPos, _Time.y * _NoiseSpeed);
                float noiseAmp = _NoiseAmount * lerp(0.15, 0.5, saturate(windAlign * 0.5 + 0.5));

                worldPos += worldNormal * (asymmetry + n * noiseAmp);

                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.worldNormal = worldNormal;
                o.worldPos = worldPos;
                o.windAlign = windAlign;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float3 n = normalize(i.worldNormal);
                float3 wind = normalize(_WindDirWorld.xyz);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Windward face dominates — leeward side fades to dark, so the
                // visual mass is biased forward and "wind direction" is obvious.
                float windFront = saturate(i.windAlign);
                windFront = pow(windFront, 1.5);

                // Silhouette rim accent — subtle.
                float rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                // Streaks: a lateral high-frequency pattern (narrow ridges across
                // the flow) modulated by a scrolling along-wind envelope. The
                // sin(along + t) term flows the envelope toward -wind over time,
                // so streaks visibly *pull backward* off the windward face.
                float3 helper = abs(wind.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 latAxis = normalize(cross(wind, helper));
                float along = dot(i.worldPos, wind);
                float lat = dot(i.worldPos, latAxis);
                float t = _Time.y * _StreakSpeed;
                float streaks = sin(lat * 5.0) + sin(lat * 9.3 + 1.1) * 0.6;
                streaks = saturate(streaks * 0.5 + 0.5);
                streaks *= sin(along * 0.5 + t) * 0.5 + 0.5;
                streaks = pow(streaks, 1.3);

                // Streaks live primarily on the windward face (gated by windFront)
                // — reads as the air "catching" on the leading surface and being
                // pulled rearward.
                float base = windFront * 0.5 + rim * 0.25;
                float glow = (base + streaks * 0.4 * windFront) * _Intensity * 0.5;

                // Stay wind-white through moderate intensities; plasma-orange
                // only blends in above _PlasmaOnset (reserved for hard reentry).
                float plasmaShift = smoothstep(_PlasmaOnset, 1.0, _Intensity);
                float3 col = lerp(_WindColor.rgb, _PlasmaColor.rgb, plasmaShift);
                return fixed4(col * glow, 1.0);
            }
            ENDCG
        }
    }
}
