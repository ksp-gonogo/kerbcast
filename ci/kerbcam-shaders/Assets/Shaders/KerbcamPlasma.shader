// kerbcam atmospheric-FX core sheath shader, v7 — geometry-shader extrusion
// + two-preset (Condensation / ReentryHeat) blend.
//
// Run as an additive overlay on the vessel's own part renderers in the near
// pass (CommandBuffer at AfterForwardAlpha) so the result composites against
// the near render's depth — vessel parts in front correctly occlude the trail
// behind them. ZTest LEqual / ZWrite Off / Blend One One.
//
// Pipeline shape:
//
//   vert : per part-renderer vertex
//          → worldPos, worldNormal, uv
//
//   geom : per input triangle
//          For each edge (i,j) on a windward-facing triangle:
//            emit 6 vertices forming a trailing strip from the edge along the
//            airflow direction:
//              b0,b1   base   (on vessel surface)
//              m0,m1   middle (along extrusion, with width spread + wobble)
//              t0,t1   tip    (far end of trail, alpha = 0)
//            trailUV runs (0..1, 0..1) where .y is 0 at base → 1 at tip,
//            .x is 0 at vertex i side → 1 at vertex j side.
//          Per-vertex noise modulates extrusion length so the trail is
//          ragged not slab-sided. Total emission per triangle is bounded by
//          [maxvertexcount(9)] — enough for one 6-vert strip with headroom.
//
//   frag : sample KSP's _FXMainTex along scrolled trailUV for the streak
//          texture (instead of procedural sines), combine with the
//          depth-map wrap from KSP's _FXDepthMap, blend a pair of presets
//          (Condensation / ReentryHeat) by _FxState, output additive RGB.
//
// KSP FXCamera globals we sample (all published process-wide by the stock
// aero-FX system every frame — free):
//   _LightDirection0   airflow direction (= -vesselVelocity)
//   _FXMainTex         tuned plasma noise texture
//   _FXColor           .a hints at real heating intensity
//   _FXDepthMap        depth as seen from upstream; gives the per-fragment
//                      "how far downstream of the windward surface?" used by
//                      wrapFromDepthMap()
//   _FXDepthCamMatrix  world → velocityCam view
//   _FXDepthProjMatrix velocityCam view → clip
//   _FXProjectionNear/Far  near/far planes
//   _FXFalloff         pre-tuned wrap falloff coefficient (mach-blended)
//   _FxLength          pre-tuned trail length multiplier (mach-blended)
//   _FXWobble          pre-tuned vertex perturbation amount (mach-blended)
//
// Falls back to C#-driven _WindDirWorld when _LightDirection0 is degenerate
// (e.g. stationary on the pad). Provides reasonable defaults for the
// _FXFalloff / _FxLength / _FXWobble uniforms so the shader doesn't visibly
// break when KSP isn't publishing yet.
Shader "Kerbcam/Plasma"
{
    Properties
    {
        _CondensationColor ("Condensation Colour (low mach)",  Color) = (0.78, 0.86, 0.98, 1)
        _ReentryColor      ("Reentry Heat Colour (high mach)", Color) = (1.00, 0.45, 0.15, 1)
        _Intensity         ("Intensity",        Range(0, 4))   = 0
        _FxState           ("FxState (cond->reentry)", Range(0, 1)) = 0
        _WindDirWorld      ("Wind Dir Fallback (world; vessel velocity dir)", Vector) = (0, 0, 1, 0)
        // Multiplier on the strip-side spread (sideMid / sideTip in emitStrip).
        // 1.0 = original tight ribbons; >1 puffs the trail out laterally so it
        // reads more as a sheath than a line. Default 1.6 gives a clearly
        // visible "wind around the vessel" without losing trail definition.
        _FxRadiusMul       ("FX Radius Multiplier (lateral spread)", Range(0.5, 4.0)) = 1.6
        // NOTE: _FxLength / _FXWobble / _FXFalloff are KSP-published globals.
        // Deliberately NOT in Properties — putting a uniform in Properties
        // gives it per-material storage that overrides Shader.SetGlobal* on
        // this material, pinning it at the property default. See trail and
        // bowshock shaders: they declare _FXMainTex / _FXColor only inside
        // CGPROGRAM for the same reason.
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+10" "IgnoreProjector"="True" }

        Pass
        {
            Blend One One   // additive
            ZWrite Off
            ZTest LEqual    // occlude against near render's depth buffer
            Cull Off        // edge-windward filter handles which trim emits

            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            // Material properties (per-instance / C#-driven).
            float4 _CondensationColor;
            float4 _ReentryColor;
            float  _Intensity;
            float  _FxState;
            float4 _WindDirWorld;
            float  _FxLength;
            float  _FXWobble;
            float  _FXFalloff;
            float  _FxRadiusMul;

            // KSP FXCamera globals (published process-wide by the stock aero-FX
            // system every frame FXCamera renders). All come "free".
            sampler2D _FXMainTex;
            sampler2D _FXDepthMap;
            float4x4  _FXDepthCamMatrix;
            float4x4  _FXDepthProjMatrix;
            float     _FXProjectionNear;
            float     _FXProjectionFar;
            float4    _LightDirection0; // airflow direction (= -vesselVelocity)
            float4    _FXColor;         // .a hints at real heating intensity

            // ----- vertex stage -----

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2g
            {
                float3 worldPos    : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            };

            v2g vert(appdata v)
            {
                v2g o;
                o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.uv          = v.uv;
                return o;
            }

            // ----- geometry stage -----

            struct g2f
            {
                float4 pos       : SV_POSITION;
                float3 worldPos  : TEXCOORD0;
                float2 trailUV   : TEXCOORD1; // .y = 0 at base → 1 at tip; .x = 0..1 across width
                float  windFront : TEXCOORD2; // [0..1], windward dot product at emission
            };

            // Cheap pseudo-random scalar from a 3D position. No texture lookup,
            // no preshader; just enough to break up the trail per-triangle.
            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            // Effective extrusion direction = airflow direction.
            // _LightDirection0 IS the airflow direction (KSP publishes it as
            // -vesselVelocity); use it when published. Otherwise fall back to
            // -_WindDirWorld (the C# fallback carries vessel-velocity dir, so
            // negate to get airflow dir).
            float3 extrudeDir()
            {
                float3 ld = _LightDirection0.xyz;
                float ldLen = length(ld);
                if (ldLen > 0.01)
                    return ld / ldLen;
                float3 fb = _WindDirWorld.xyz;
                float fbLen = length(fb);
                if (fbLen > 0.01)
                    return -fb / fbLen;
                return float3(0, 0, 1);
            }

            // Sample KSP's depth map to get "how far downstream of the windward
            // surface" this fragment is, mapped to a [0..1] wrap term.
            // 1 at the windward surface, decaying into the wake.
            float wrapFromDepthMap(float3 worldPos)
            {
                float4 viewPos = mul(_FXDepthCamMatrix, float4(worldPos, 1.0));
                float4 clipPos = mul(_FXDepthProjMatrix, viewPos);
                float w = (abs(clipPos.w) < 1e-5) ? 1.0 : clipPos.w;
                float2 ndc = clipPos.xy / w;
                if (any(abs(ndc) > 1.0)) return 0.0;
                float2 uv = ndc * 0.5 + 0.5;
                float sampled = tex2D(_FXDepthMap, uv).r;
                if (sampled > 0.999) return 0.0;

                float ourDepth01 = clipPos.z / w;
                #if !defined(UNITY_REVERSED_Z) && !defined(SHADER_API_D3D11) && !defined(SHADER_API_D3D12) && !defined(SHADER_API_METAL) && !defined(SHADER_API_VULKAN)
                    ourDepth01 = ourDepth01 * 0.5 + 0.5;
                #endif

                float range = max(_FXProjectionFar - _FXProjectionNear, 0.001);
                float metresDownstream = (ourDepth01 - sampled) * range;
                float falloff = max(_FXFalloff, 0.05);
                float wrap = saturate(1.0 - max(metresDownstream, 0.0) * falloff);
                wrap *= step(-0.5, metresDownstream);
                return wrap;
            }

            // Emit one extruded triangle-strip from edge (a,b) trailing along
            // the airflow direction. windFront is the windward-dot used to
            // gate emission (0..1, 1 = fully windward). Strip vertex layout:
            //
            //   b0 ──── b1     y=0  (just off the vessel surface)
            //   │ \  / │
            //   m0 ──── m1     y=0.5 (middle, spread, wobble)
            //   │ \  / │
            //   t0 ──── t1     y=1.0 (tip, alpha = 0)
            //
            // trailUV.x = 0 at the a-side of the edge, 1 at the b-side.
            //
            // nA/nB are the edge vertices' surface normals. The strip is
            // lifted along them: a small constant at the base so it clears
            // the hull skin instead of slicing through it (ZTest LEqual
            // against the hull's own depth), growing toward the tip so the
            // strip arcs AROUND the body like a separating streamline
            // rather than extruding straight through it.
            void emitStrip(float3 wpA, float3 wpB, float3 nA, float3 nB,
                           float3 windDir, float windFront,
                           float baseLen, float wobble, inout TriangleStream<g2f> stream)
            {
                // Per-vertex extrusion lengths from a position hash, so adjacent
                // edges get ragged rather than uniform-length trails.
                float randA = hash13(wpA * 3.7);
                float randB = hash13(wpB * 3.7);
                float lenA = baseLen * (0.55 + randA * 1.10);
                float lenB = baseLen * (0.55 + randB * 1.10);

                // Side spread: widen toward the tip. Cross product between
                // the airflow and the edge gives a sideways normal in the
                // plane perpendicular to airflow. When the edge is nearly
                // parallel to airflow (long thin parts: engine bells, ladders,
                // antenna booms) the cross product is degenerate — that
                // produced the off-to-the-side "stray flare" artifact on
                // high-mach reentry. Detect and skip.
                float3 edge = wpB - wpA;
                float3 edgeN = normalize(edge + 1e-4);
                float3 side = cross(windDir, edgeN);
                float sideLen2 = dot(side, side);
                if (sideLen2 < 0.04) return;   // edge < ~12° off airflow — skip
                side *= rsqrt(sideLen2);       // normalise so widths are absolute, not angle-dependent
                float radiusMul = max(_FxRadiusMul, 0.5);
                float sideMid = 0.15 * baseLen * radiusMul;
                float sideTip = 0.45 * baseLen * radiusMul;

                // Per-vertex perpendicular wobble — driven off _FXWobble so the
                // tuning lives in the stock published value when present.
                float w0 = (hash13(wpA * 5.1 + 7.7) - 0.5) * wobble;
                float w1 = (hash13(wpB * 5.1 + 7.7) - 0.5) * wobble;

                // Normal lift: constant hull clearance at the base, growing
                // with extrusion length toward the tip (streamline separation).
                float liftBase = 0.04;
                float liftMidA = liftBase + 0.18 * lenA;
                float liftMidB = liftBase + 0.18 * lenB;
                float liftTipA = liftBase + 0.30 * lenA;
                float liftTipB = liftBase + 0.30 * lenB;

                // Base / middle / tip positions, in world space.
                float3 b0 = wpA + nA * liftBase;
                float3 b1 = wpB + nB * liftBase;
                float3 m0 = wpA + nA * liftMidA + windDir * (lenA * 0.4) - side * sideMid + windDir * w0;
                float3 m1 = wpB + nB * liftMidB + windDir * (lenB * 0.4) + side * sideMid + windDir * w1;
                float3 t0 = wpA + nA * liftTipA + windDir * lenA          - side * sideTip;
                float3 t1 = wpB + nB * liftTipB + windDir * lenB          + side * sideTip;

                g2f o;
                o.windFront = windFront;

                #define EMIT(WP, UVX, UVY) \
                    o.worldPos = (WP); \
                    o.trailUV  = float2((UVX), (UVY)); \
                    o.pos      = mul(UNITY_MATRIX_VP, float4((WP), 1.0)); \
                    stream.Append(o);

                EMIT(b0, 0.0, 0.0);
                EMIT(b1, 1.0, 0.0);
                EMIT(m0, 0.0, 0.5);
                EMIT(m1, 1.0, 0.5);
                EMIT(t0, 0.0, 1.0);
                EMIT(t1, 1.0, 1.0);

                #undef EMIT
                stream.RestartStrip();
            }

            // Emit up to 9 vertices per input triangle — enough for one
            // 6-vert trailing strip plus headroom.
            [maxvertexcount(9)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
            {
                if (_Intensity <= 0.0001) return;

                float3 windDir = extrudeDir(); // airflow direction
                float3 viewAxis = -windDir;     // "windward" = facing into the airflow

                // Per-vertex windward score. Stock plasma only forms on
                // surfaces facing into the airflow; let the trail spawn from
                // edges that are at least nominally windward.
                float wf0 = saturate(dot(input[0].worldNormal, viewAxis));
                float wf1 = saturate(dot(input[1].worldNormal, viewAxis));
                float wf2 = saturate(dot(input[2].worldNormal, viewAxis));
                float triWind = max(wf0, max(wf1, wf2));
                if (triWind < 0.05) return;

                // Two-preset blend on trail length + wobble. Reentry stretches
                // and wobbles more than condensation.
                float fxState = saturate(_FxState);
                float lengthPreset = lerp(0.6, 2.6, fxState);
                float wobblePreset = lerp(0.25, 1.4, fxState);

                // Intensity ramps both length and wobble. _FxLength /
                // _FXWobble are KSP-tuned multipliers; the max() floor below
                // doubles as a fallback when KSP isn't publishing (unset
                // globals read as 0) so the trail is still visible during
                // pad iteration / early flight init.
                float fxLen     = max(_FxLength, 0.6);
                float fxWobble  = max(_FXWobble, 0.4);
                float baseLen   = lengthPreset * fxLen * _Intensity;
                float wobble    = wobblePreset * fxWobble * _Intensity;

                // Triangle centroid windward score → emission gate.
                float windFrontTri = (wf0 + wf1 + wf2) * (1.0 / 3.0);

                // Emit one strip from the edge whose two vertices are most
                // windward — that's the edge most likely to ride along the
                // visible plasma silhouette. Picking one of the three edges
                // (not all three) keeps the vertex count inside the
                // [maxvertexcount(9)] bound and avoids redundant overlap.
                int a = 0, b = 1;
                if (wf0 + wf1 >= wf1 + wf2 && wf0 + wf1 >= wf2 + wf0) { a = 0; b = 1; }
                else if (wf1 + wf2 >= wf2 + wf0) { a = 1; b = 2; }
                else { a = 2; b = 0; }

                emitStrip(input[a].worldPos, input[b].worldPos,
                          normalize(input[a].worldNormal), normalize(input[b].worldNormal),
                          windDir, windFrontTri, baseLen, wobble, stream);
            }

            // ----- fragment stage -----

            fixed4 frag(g2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float fxState = saturate(_FxState);

                // Sample KSP's tuned plasma noise along the trail. Scroll
                // along trailUV.y so the streaks visibly move toward the tail.
                // Scroll speed and noise tile rate both lerp between the two
                // presets — condensation moves slowly, reentry is fast.
                float scrollSpeed = lerp(0.6, 4.0, fxState);
                float tile = lerp(0.5, 1.4, fxState);
                float2 fxUv = float2(i.trailUV.x * tile,
                                     i.trailUV.y * tile - _Time.y * scrollSpeed);
                float fxNoise = tex2D(_FXMainTex, fxUv).r;
                // Sharpen into wisps; clamp away the lower half so the trail
                // is wispy rather than a solid slab.
                float noiseSharp = saturate(fxNoise * 1.7 - 0.35);

                // Length fade — bright at base, fades to nothing at tip.
                float lenFade = pow(saturate(1.0 - i.trailUV.y), 1.6);
                // Width fade — softer at the edges of the strip so the trail
                // doesn't read as a hard ribbon.
                float widthFade = 1.0 - pow(abs(i.trailUV.x * 2.0 - 1.0), 2.0);

                float streak = noiseSharp * lenFade * widthFade;

                // KSP depth-map wrap. Sampled at the fragment's worldPos so
                // it operates ALONG the trail (not just at base): the wrap
                // function returns ~1 close to the windward surface and
                // decays naturally with downstream distance, which already
                // gives "bright halo at the head, fades along the wings"
                // without us double-fading.
                float wrap = wrapFromDepthMap(i.worldPos);
                float wrapHead = wrap;

                // Two-layer alpha shape (cf. Firefly): the streak builds up
                // alpha (constructive); the wrap eats alpha through the noise
                // (destructive) to give the "wisps tearing off the body" feel.
                float alphaConstruct = streak * 0.85 + wrapHead * 0.75;
                float alphaDestruct  = wrapHead * (1.0 - noiseSharp * 0.6);
                float layerMix = 1.0 - i.windFront; // leeward fragments get more of the wispy layer
                float glow = lerp(alphaConstruct, alphaDestruct, layerMix);

                // Real-heating hint from KSP — a touch of FxColor.a brightens
                // genuinely-heating fragments and leaves cold ones subtle.
                float fxHeating = saturate(_FXColor.a);
                glow *= (0.7 + 0.5 * fxHeating);

                glow *= _Intensity;

                // Colour: lerp between the two presets purely by fxState
                // (separate from intensity). Real-heating colour from
                // _FXColor.rgb gets mixed in at full reentry as a tint.
                float3 baseCol = lerp(_CondensationColor.rgb, _ReentryColor.rgb, fxState);
                float3 col = lerp(baseCol, baseCol * (0.4 + _FXColor.rgb * 1.6),
                                  fxHeating * fxState);

                return fixed4(col * glow, 1.0);
            }
            ENDCG
        }
    }
}
