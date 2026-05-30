// kerbcam atmospheric-FX core sheath shader, v6.
//
// Drawn additively over a procedural ~800-vert proxy shell in the near pass
// (CommandBuffer at AfterForwardAlpha) so it composites against the near
// render's depth — vessel parts correctly occlude the back of the shell.
//
// Samples KSP's FXCamera globals (published process-wide by the stock aero-FX
// system) so the look is physics-driven rather than estimated:
//
//   _LightDirection0   wind direction from the game's aero state (replaces
//                      our C#-derived wind direction when present)
//   _FXDepthMap        vessel depth as seen from upstream of the wind (an
//                      orthographic depth render fired by FXCamera); per
//                      fragment, comparing the projected fragment depth to
//                      this gives "how far downstream of the windward
//                      surface" — the per-pixel quantity that gives stock
//                      plasma its characteristic wrap-around-the-vessel
//                      shape.
//   _FXDepthCamMatrix  world → velocityCam view (for the projection)
//   _FXDepthProjMatrix velocityCam view → clip
//   _FXProjectionNear/Far  near/far planes (linearise the diff to metres)
//   _FXColor.a         a soft hint of real heating intensity
//
// KerbcamCore keeps FXCamera alive even during ThrottleMainScreen when
// EnableAtmosphericFx is on, so these globals stay fresh.
//
// Falls back to C#-driven _WindDirWorld when _LightDirection0 is degenerate
// (e.g. stationary on the pad), so ForceAtmosphericFx + DebugWindDirection
// still exercise the directional code paths for visual iteration.
Shader "Kerbcam/Plasma"
{
    Properties
    {
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.65, 0.75, 0.85, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.5, 0.2, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _WindDirWorld("Wind Dir Fallback (world)", Vector) = (0,0,1,0)
        _NoiseAmount ("Noise Displacement (m)", Range(0,4)) = 1.5
        _NoiseSpeed  ("Noise Speed", Float) = 2.0
        _StreakSpeed ("Streak Speed", Float) = 5.0
        _PlasmaOnset ("Plasma Onset", Range(0,1)) = 0.85
        _RimPower    ("Rim Power", Range(0.5,8)) = 2.0
        _WrapFalloff ("Wrap Falloff", Range(0,2)) = 0.6
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

            // Material properties (per-instance / C#-driven).
            float4 _WindColor;
            float4 _PlasmaColor;
            float  _Intensity;
            float4 _WindDirWorld;
            float  _NoiseAmount;
            float  _NoiseSpeed;
            float  _StreakSpeed;
            float  _PlasmaOnset;
            float  _RimPower;
            float  _WrapFalloff;

            // KSP FXCamera globals (published process-wide by the stock aero-FX
            // system every frame FXCamera renders). All come "free" — we just
            // declare them and sample.
            sampler2D _FXDepthMap;
            float4x4  _FXDepthCamMatrix;
            float4x4  _FXDepthProjMatrix;
            float     _FXProjectionNear;
            float     _FXProjectionFar;
            float4    _LightDirection0; // direction (and possibly magnitude) of airflow
            float4    _FXColor;         // .a hints at real heating intensity

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                float  windAlign  : TEXCOORD2; // dot(normal, wind): +1 windward, -1 leeward
            };

            // Multi-octave layered noise — many vertices on the proxy mesh, so
            // adjacent verts land at independent phases.
            float noise3(float3 p, float t)
            {
                return sin(p.x * 1.1 + t) * 0.50
                     + sin(p.y * 1.7 + t * 0.8 + 1.3) * 0.40
                     + sin(p.z * 1.5 - t * 0.6) * 0.35
                     + sin(p.x * 3.1 + p.z * 2.3 + t * 1.3) * 0.25
                     + sin(p.y * 2.7 - p.z * 1.9 - t * 0.9 + 0.4) * 0.20;
            }

            // Pick the effective wind direction: prefer the game's aero
            // _LightDirection0 (physics-driven) when it has magnitude; fall
            // back to the C#-driven _WindDirWorld (which respects
            // DebugWindDirection) when stationary so the pad preview still
            // exercises the directional code paths.
            float3 effectiveWind()
            {
                float3 ld = _LightDirection0.xyz;
                float ldLen = length(ld);
                if (ldLen > 0.01)
                    return ld / ldLen;
                float3 fb = _WindDirWorld.xyz;
                float fbLen = length(fb);
                if (fbLen > 0.01)
                    return fb / fbLen;
                return float3(0, 0, 1);
            }

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 wind = effectiveWind();

                float windAlign = dot(worldNormal, wind);

                // Asymmetric shape: bulge forward, compress behind — silhouette
                // telegraphs direction of motion.
                float asymmetry = windAlign * 0.4 * _NoiseAmount;
                float n = noise3(worldPos, _Time.y * _NoiseSpeed);
                float noiseAmp = _NoiseAmount * lerp(0.15, 0.5, saturate(windAlign * 0.5 + 0.5));

                worldPos += worldNormal * (asymmetry + n * noiseAmp);

                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.worldNormal = worldNormal;
                o.worldPos = worldPos;
                o.windAlign = windAlign;
                return o;
            }

            // Sample the FXCamera depth map and return a "wrap" term in [0,1]:
            // 1 at/near the vessel's windward surface, decaying downstream.
            // Returns 0 when the fragment isn't within FXCamera's view (no
            // useful depth data — e.g. stationary on the pad).
            float wrapFromDepthMap(float3 worldPos)
            {
                float4 viewPos = mul(_FXDepthCamMatrix, float4(worldPos, 1.0));
                float4 clipPos = mul(_FXDepthProjMatrix, viewPos);
                float w = (abs(clipPos.w) < 1e-5) ? 1.0 : clipPos.w;
                float2 ndc = clipPos.xy / w;
                if (any(abs(ndc) > 1.0)) return 0.0;
                float2 uv = ndc * 0.5 + 0.5;
                float sampled = tex2D(_FXDepthMap, uv).r;
                // Bail if the depth map looks unwritten (sky / far plane).
                if (sampled > 0.999) return 0.0;

                float ourDepth01 = clipPos.z / w;
                #if !defined(UNITY_REVERSED_Z) && !defined(SHADER_API_D3D11) && !defined(SHADER_API_D3D12) && !defined(SHADER_API_METAL) && !defined(SHADER_API_VULKAN)
                    ourDepth01 = ourDepth01 * 0.5 + 0.5;
                #endif

                float range = max(_FXProjectionFar - _FXProjectionNear, 0.001);
                float metresDownstream = (ourDepth01 - sampled) * range;
                // Plasma only on the downstream (trailing) side of the windward
                // surface. Exponential-ish falloff into the wake.
                float wrap = saturate(1.0 - max(metresDownstream, 0.0) * _WrapFalloff);
                wrap *= step(-0.5, metresDownstream); // hide a touch in front of the surface
                return wrap;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float3 n = normalize(i.worldNormal);
                float3 wind = effectiveWind();
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Windward face dominates — leeward fades to dark.
                float windFront = saturate(i.windAlign);
                windFront = pow(windFront, 1.5);

                // Silhouette rim accent.
                float rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                // Streaks: ridges that run PARALLEL to the wind axis (along
                // the direction of motion), as actual airflow does. For
                // ridges to run along wind on a surface that goes AROUND the
                // wind axis, the pattern must vary AZIMUTHALLY around that
                // axis — at any constant azimuth angle, the surface
                // coordinate runs parallel to wind. So we project the world
                // normal into the plane perpendicular to wind and read its
                // angle in that plane, then sin(angle * N) gives N ridges
                // spaced evenly around the axis.
                float3 helper = abs(wind.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 e1 = normalize(cross(wind, helper));
                float3 e2 = cross(wind, e1);
                float3 nPerp = n - dot(n, wind) * wind;
                float angle = atan2(dot(nPerp, e2), dot(nPerp, e1));

                // Scroll envelope along the wind axis so streaks visibly flow
                // toward -wind (rearward, relative to motion) as t advances.
                float along = dot(i.worldPos, wind);
                float t = _Time.y * _StreakSpeed;
                float streaks = sin(angle * 8.0) + sin(angle * 13.0 + 1.1) * 0.5;
                streaks = saturate(streaks * 0.5 + 0.5);
                streaks *= sin(along * 0.6 + t) * 0.5 + 0.5;
                streaks = pow(streaks, 1.2);

                // Wrap heat from KSP's depth map — per-fragment "how far
                // downstream of the vessel's windward surface am I?" — gives
                // the characteristic plasma wrap that pad noise can't fake.
                // Zero when FXCamera isn't usefully running (e.g. on the pad).
                float wrap = wrapFromDepthMap(i.worldPos);

                // Combined glow. Wrap term is the dominant contribution when
                // FXCamera is publishing useful data; otherwise the shader
                // falls back to the windFront+rim+streak look for pad iteration.
                float base = windFront * 0.55 + rim * 0.3;
                float surface = base + streaks * 0.8 * windFront;
                float fxHeating = saturate(_FXColor.a); // soft hint
                float wrapContribution = wrap * (0.7 + 0.5 * fxHeating);

                // Brighter overall — prior 0.5 multiplier washed too much.
                float glow = (surface + wrapContribution) * _Intensity * 0.85;

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
