// kerbcam atmospheric-FX core sheath shader. Drawn additively over the vessel's
// part renderers (via a CommandBuffer at AfterForwardAlpha), so it composites
// against the near render's depth — ZTest LEqual gives correct occlusion.
//
// P1 placeholder: the *shape* of the final shader (additive, depth-tested,
// windward + rim + scrolling streaks, intensity-driven) without the visual
// richness. Tunables are material properties so they can be driven from C#
// (fast loop) rather than baked (which would need a CI rebuild per tweak).
Shader "Kerbcam/Plasma"
{
    Properties
    {
        _Color       ("Plasma Color", Color) = (1.0, 0.45, 0.15, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _WindDirWorld("Wind Dir (world)", Vector) = (0,1,0,0)
        _RimPower    ("Rim Power", Range(0.5,8)) = 3
        _StreakScale ("Streak Scale", Float) = 6
        _StreakSpeed ("Streak Speed", Float) = 4
        // Outward inflation (metres) along vertex normals — makes the sheath
        // "puff" off the skin so it reads as flowing over the hull. Driven from
        // C# (scaled by intensity); free perf-wise (a per-vertex offset).
        _PuffDistance("Puff Distance (m)", Range(0,1)) = 0.2
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

            float4 _Color;
            float  _Intensity;
            float4 _WindDirWorld;
            float  _RimPower;
            float  _StreakScale;
            float  _StreakSpeed;
            float  _PuffDistance;

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                // Inflate outward along the normal so the sheath sits off the
                // skin (a halo/flow rather than a decal). Free: no new geometry.
                worldPos += worldNormal * _PuffDistance;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.worldNormal = worldNormal;
                o.worldPos = worldPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float3 n = normalize(i.worldNormal);
                float3 wind = normalize(_WindDirWorld.xyz);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Windward surfaces (facing into the airflow) glow most.
                float windward = saturate(dot(n, wind));
                windward *= windward;

                // Rim / fresnel edge glow.
                float rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                // Streaks: a stripe scrolling along the wind axis.
                float along = dot(i.worldPos, wind);
                float streak = 0.5 + 0.5 * sin(along * _StreakScale - _Time.y * _StreakSpeed);
                streak = lerp(0.6, 1.0, streak);

                float glow = (windward * 0.8 + rim * 0.5) * streak * _Intensity;
                return fixed4(_Color.rgb * glow, 1.0);
            }
            ENDCG
        }
    }
}
