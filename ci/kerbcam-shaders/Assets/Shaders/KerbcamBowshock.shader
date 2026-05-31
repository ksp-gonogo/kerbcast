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
//
// Critical fix vs. the original: use a smoothed (radial-from-axis) normal at
// the fragment instead of per-face flat normals on the cone mesh. Per-face
// flat normals were giving discrete (16-segment) rim brightness "rings" when
// viewed near-axis from inside the cone — the concentric-polygon "disc"
// artifact. Smoothed normals make fresnel continuous around the cone.
//
// Plus a near-plane distance fade and a face-on extra-dampener so the cone
// stays subtle when the camera is inside / very close to it — face-on
// rendering used to dominate the entire frame at high _Intensity.
Shader "Kerbcam/Bowshock"
{
    Properties
    {
        // Wind colour shifted toward blue-violet — the leading edge of a
        // hypersonic shock front in ionised air glows ultraviolet/blue at
        // the highest temperatures. Stays subtle visually because the
        // bowshock output is dominated by rim glow only.
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.55, 0.65, 1.0, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.40, 0.20, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _RimPower    ("Rim Power", Range(0.5,12)) = 1.5
        _ScrollSpeed ("Scroll Speed", Float) = 1.5
        // Plasma colour only blends in above this intensity (reserved for
        // heavy reentry); below it stays wind-blue. Matches KerbcamPlasma.
        _PlasmaOnset ("Plasma Onset", Range(0,1)) = 0.85
        // Near-camera distance over which the cone fades to zero. Stops the
        // cone from being a wall when the camera is inside or very close.
        _NearFadeDist ("Near Fade Distance (m)", Range(0.1, 5.0)) = 1.5
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
            float  _NearFadeDist;

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

                // Smoothed (radial-from-axis) normal in world space. Local +Z
                // is the cone axis; the inward radial direction in local
                // space is (lpXY)/|lpXY| which we transform back to world.
                // Using this instead of i.worldNormal removes the discrete
                // per-face brightness rings that produced the polygonal
                // "disc" artifact viewed near-axis.
                //
                // Near the apex (lpLen → 0) the smoothed normal is degenerate
                // — at the literal tip there is no well-defined radial
                // direction. apexFade smoothly suppresses rim contribution
                // in that region so the cone tip doesn't blow out.
                // Smoothed normal in local space. The shader supports two
                // mesh shapes:
                //   dome — unit oblate hemisphere, z in [0, 1], normal at
                //     each fragment is the position vector itself (sphere).
                //   cone — legacy 6 m tall cone, z in [-3, +3], normal at
                //     each fragment is the radial direction from the
                //     cone axis (cylindrical).
                // Heuristic: total |localPos| ≤ 1.3 → dome, otherwise cone.
                float2 lpXY = i.localPos.xy;
                float lpLen = length(lpXY);
                float lenLocal = length(i.localPos);
                float3 nLocalCyl = float3(lpXY / max(lpLen, 1e-3), 0.0);
                float3 nLocalSph = i.localPos / max(lenLocal, 1e-3);
                float isDome = step(lenLocal, 1.3);
                float3 nLocal = lerp(nLocalCyl, nLocalSph, isDome);
                float3 n = normalize(mul((float3x3)unity_ObjectToWorld, nLocal));
                // apexFade: fades the rim near the cone tip (lpXY ~ 0)
                // where the smoothed radial normal is degenerate.
                // baseFade: fades the wide base ring (local z near -3)
                // so the cone has no hard outer outline — fragments at
                // the very back of the cone smoothly dissolve.
                // Together these make the whole mesh fade to zero at its
                // boundaries — what the user was asking for ("where's
                // the alpha drop-off"). Half-length of cone is 3 in mesh
                // local; fading begins at the outer ~17 % of the cone.
                float apexFade = saturate(lpLen / 0.4);
                // Base fade — fragments near the open base of the mesh
                // dissolve into transparency. For the dome mesh, base is
                // at local z=0 (fades over 0..0.3). For the legacy cone
                // mesh, base is at local z=-3 (handled by the negative
                // branch below). The smoothstep edges are chosen so the
                // mesh's outer-most ring fades smoothly to nothing.
                float baseFade = i.localPos.z >= 0.0
                    ? smoothstep(0.0, 0.3, i.localPos.z)        // dome: fade near z=0
                    : smoothstep(-3.0, -1.5, i.localPos.z);     // cone: fade near z=-3
                float endsFade = apexFade * baseFade;

                float3 toCam = _WorldSpaceCameraPos - i.worldPos;
                float distToCam = length(toCam);
                float3 viewDir = toCam / max(distToCam, 1e-3);

                // Bowshock output is essentially RIM ONLY — we read the
                // shock as a thin curved arc at the silhouette of the cone,
                // not as a filled surface. Interior contribution is reduced
                // to a very faint hint (baseGlow * 0.03). This matches the
                // real-world reference: actual bowshocks are mostly
                // invisible volumes with only the highest-energy leading
                // edge faintly glowing.
                float ndv = abs(dot(n, viewDir));
                float rim = pow(1.0 - saturate(ndv), _RimPower);

                // Near-camera distance fade — when a fragment is closer than
                // _NearFadeDist the contribution drops to 0 by the time it
                // crosses the near clip. Without this, looking into the cone
                // from inside or just outside paints the whole frame.
                float nearFade = saturate(distToCam / _NearFadeDist);

                // Sample KSP's tuned plasma noise texture, scrolled along the
                // cone axis toward -Z (toward the vessel — air rushing through
                // the shock front). Visual continuity with stock plasma.
                float2 fxUv = float2(i.localPos.x * 0.3 + i.localPos.y * 0.2,
                                     i.localPos.z * 0.4 - _Time.y * _ScrollSpeed * 0.15);
                float fxNoise = tex2D(_FXMainTex, fxUv).r;
                float shimmer = 0.7 + 0.6 * fxNoise;

                // Rim-dominant glow with a sharp brightness cap. baseGlow is
                // tiny on purpose — the interior should NOT be visible as a
                // surface. endsFade kills the rim contribution at both the
                // cone tip AND the base ring, so the visible mesh has soft
                // boundaries instead of hard polygon outlines.
                float baseGlow = 0.02;
                float raw = (baseGlow + rim * endsFade) * shimmer * nearFade;
                // Brightness cap dropped from 0.45 → 0.30 (≈33 % less peak)
                // so the bowshock reads as a wide diffuse haze rather than
                // a concentrated glow.
                float glow = saturate(raw * 0.30) * _Intensity;

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
