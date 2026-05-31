// kerbcam atmospheric-FX embers shader, geometry-shader rewrite.
//
// Architecture: same windward-filter + airflow-extrusion pattern as
// KerbcamPlasma. Where plasma emits a long trailing STRIP per windward
// triangle, embers emit a small CAMERA-ALIGNED QUAD (billboard) per
// triangle, positioned at a random point along the airflow extrusion.
// Each quad is a single spark — hot-cored, soft-edged, alpha-faded.
//
// The geom shader gates emission with the same windward dot-product test
// as plasma so sparks shed FROM the heated surfaces (heat shield, nose
// cone, windward sides of the body) and trail backward along airflow.
// Replaces the previous ParticleSystem-based embers; same scene-graph
// pattern as KerbcamPlasma — CommandBuffer.DrawRenderer on vessel parts.
//
// Run-state mirrors plasma: Blend One One (additive), ZTest LEqual,
// ZWrite Off, Cull Off. AfterForwardAlpha pass on the near camera.
Shader "Kerbcam/Ember"
{
    Properties
    {
        _Intensity ("Intensity", Range(0, 4)) = 0
        // Same Condensation→Plasma blend gradient as plasma so the embers
        // visually agree at any flight regime: white-hot core at high
        // _FxState, cooler red-orange at low _FxState.
        _CondensationColor ("Condensation Colour", Color) = (1.00, 0.95, 0.85, 1)
        _ReentryColor      ("Reentry Colour",      Color) = (1.00, 0.45, 0.10, 1)
        _FxState           ("FxState (cond→reentry)", Range(0, 1)) = 0
        _WindDirWorld      ("Wind Dir Fallback (vessel velocity, world)", Vector) = (0, 0, 1, 0)
        _SparkSize         ("Spark Size (m)",      Range(0.01, 0.5)) = 0.10
        _SoftEdge          ("Soft Edge Start",     Range(0, 0.5))    = 0.30
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+11" "IgnoreProjector"="True" }

        Pass
        {
            Blend One One   // additive
            ZWrite Off
            ZTest LEqual    // occlude against near render's depth buffer
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            float  _Intensity;
            float4 _CondensationColor;
            float4 _ReentryColor;
            float  _FxState;
            float4 _WindDirWorld;
            float  _SparkSize;
            float  _SoftEdge;
            float  _FxLength;
            float4 _LightDirection0;
            float4 _FXColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2g
            {
                float3 worldPos    : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2g vert(appdata v)
            {
                v2g o;
                o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            struct g2f
            {
                float4 pos      : SV_POSITION;
                float2 quadUV   : TEXCOORD0;
                float  life01   : TEXCOORD1; // 0 = hot/fresh, 1 = cool/old
            };

            // Cheap pseudo-random scalar from a 3D position.
            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            // Airflow direction = -velocity. Same logic as plasma's extrudeDir.
            float3 extrudeDir()
            {
                float3 ld = _LightDirection0.xyz;
                float ldLen = length(ld);
                if (ldLen > 0.01) return ld / ldLen;
                float3 fb = _WindDirWorld.xyz;
                float fbLen = length(fb);
                if (fbLen > 0.01) return -fb / fbLen;
                return float3(0, 0, 1);
            }

            // Emit one camera-aligned billboard quad at world position centre
            // with the given half-size in world units. life01 is passed
            // through to the fragment for the lifecycle colour blend.
            //
            // Billboard math via view-space offsets: transform centre to
            // view space, then add the half-size in the view-X / view-Y
            // axes (which are screen X / Y by construction). Avoids the
            // matrix-layout ambiguity of picking world camera basis vectors
            // out of UNITY_MATRIX_V directly.
            void emitQuad(float3 centre, float halfSize, float life01,
                          inout TriangleStream<g2f> stream)
            {
                float4 viewCenter = mul(UNITY_MATRIX_V, float4(centre, 1.0));
                g2f o;
                o.life01 = life01;

                o.pos = mul(UNITY_MATRIX_P, viewCenter + float4(-halfSize, -halfSize, 0, 0));
                o.quadUV = float2(0, 0); stream.Append(o);
                o.pos = mul(UNITY_MATRIX_P, viewCenter + float4( halfSize, -halfSize, 0, 0));
                o.quadUV = float2(1, 0); stream.Append(o);
                o.pos = mul(UNITY_MATRIX_P, viewCenter + float4(-halfSize,  halfSize, 0, 0));
                o.quadUV = float2(0, 1); stream.Append(o);
                o.pos = mul(UNITY_MATRIX_P, viewCenter + float4( halfSize,  halfSize, 0, 0));
                o.quadUV = float2(1, 1); stream.Append(o);
                stream.RestartStrip();
            }

            // Emit up to two billboarded sparks per input triangle (8 verts).
            // Two-per-triangle gives reasonable density on a typical part
            // without overpacking.
            [maxvertexcount(8)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
            {
                if (_Intensity <= 0.0001) return;

                float3 windDir = extrudeDir(); // airflow direction (downstream)
                float3 viewAxis = -windDir;     // windward = facing into airflow

                // Same windward filter as plasma.
                float wf0 = saturate(dot(input[0].worldNormal, viewAxis));
                float wf1 = saturate(dot(input[1].worldNormal, viewAxis));
                float wf2 = saturate(dot(input[2].worldNormal, viewAxis));
                float triWind = max(wf0, max(wf1, wf2));
                if (triWind < 0.05) return;

                // Triangle triCentroid in world space — the spark's emission
                // anchor (ablation point on the heated surface).
                float3 triCentroid = (input[0].worldPos + input[1].worldPos + input[2].worldPos) / 3.0;

                // Per-triangle pseudo-random seeds for variation.
                float seedA = hash13(triCentroid * 7.3);
                float seedB = hash13(triCentroid * 4.1 + 11.7);

                // Maximum extrusion distance along airflow — scales with the
                // KSP-published _FxLength and our _Intensity. Embers can be
                // shorter than plasma trails since each spark is discrete.
                float fxLen = max(_FxLength, 0.6);
                float maxDist = 1.2 * fxLen * _Intensity;

                // Emit two sparks at different distances along the airflow.
                // life01 = normalised position along the wake (0 hot, 1 cool).
                float life01_a = seedA;
                float life01_b = seedB * 0.5 + 0.5; // bias the second one further back
                float distA = life01_a * maxDist;
                float distB = life01_b * maxDist;

                // Random lateral jitter so sparks don't all sit dead-centre.
                float3 jitterAxis = normalize(input[1].worldPos - input[0].worldPos + float3(1e-4, 0, 0));
                float3 jitterAxis2 = cross(jitterAxis, windDir);
                float jitterA = (hash13(triCentroid * 3.7) - 0.5) * 0.08 * fxLen;
                float jitterB = (hash13(triCentroid * 5.9) - 0.5) * 0.08 * fxLen;

                float3 posA = triCentroid + windDir * distA + jitterAxis * jitterA;
                float3 posB = triCentroid + windDir * distB + jitterAxis2 * jitterB;

                // Size shrinks with lifecycle (hot sparks bigger, cool dimmer).
                float sizeA = _SparkSize * lerp(1.0, 0.4, life01_a);
                float sizeB = _SparkSize * lerp(1.0, 0.4, life01_b);

                emitQuad(posA, sizeA, life01_a, stream);
                emitQuad(posB, sizeB, life01_b, stream);
            }

            fixed4 frag(g2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                // Two-tier radial falloff (matches the previous ember frag):
                // tight hot core + soft outer halo for a pinpoint-with-bloom look.
                float r = length(i.quadUV - 0.5);
                float halo = saturate(1.0 - smoothstep(_SoftEdge, 0.5, r));
                float core = saturate(1.0 - smoothstep(0.0, 0.16, r));
                float brightness = halo + core * 1.6;

                // Lifecycle gradient: hot Condensation colour at life01=0
                // (just shed, white-yellow hot), cooling toward ReentryColor
                // at life01=1 (red-orange embers further back in the wake).
                // Inverted from plasma's Condensation→Reentry mach blend
                // because each individual spark cools as it ages — so
                // condensation = "young/hot", reentry = "old/cooling".
                float3 hot = _CondensationColor.rgb;
                float3 cool = _ReentryColor.rgb;
                float3 sparkCol = lerp(hot, cool, i.life01);

                // Light tint toward KSP's stock _FXColor at high real heat.
                float fxHeat = saturate(_FXColor.a);
                sparkCol = lerp(sparkCol, sparkCol * (0.4 + _FXColor.rgb * 1.6), fxHeat * 0.5);

                // Lifecycle alpha falloff — sparks fade out as they cool.
                float lifeAlpha = pow(saturate(1.0 - i.life01), 1.2);

                fixed3 rgb = sparkCol * brightness * lifeAlpha * _Intensity;
                return fixed4(rgb, halo * lifeAlpha);
            }
            ENDCG
        }
    }
}
