// kerbcam atmospheric-FX core sheath shader. Drawn additively over the vessel's
// part renderers (CommandBuffer at AfterForwardAlpha) so it composites against
// the near render's depth — correct occlusion, no second camera.
//
// Look: a windward + rim glow, broken up by animated procedural turbulence so it
// flows/shakes rather than sitting as a smooth shell, inflated off the hull by
// _PuffDistance, and colour-ramped from pale wind (low intensity) to orange
// plasma (high). Tunables are material properties driven from C# (fast loop).
Shader "Kerbcam/Plasma"
{
    Properties
    {
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.70, 0.85, 1.0, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.45, 0.15, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _WindDirWorld("Wind Dir (world)", Vector) = (0,1,0,0)
        _RimPower    ("Rim Power", Range(0.5,8)) = 3
        _NoiseScale  ("Turbulence Scale", Float) = 0.15
        _NoiseSpeed  ("Turbulence Speed", Float) = 3
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

            float4 _WindColor;
            float4 _PlasmaColor;
            float  _Intensity;
            float4 _WindDirWorld;
            float  _RimPower;
            float  _NoiseScale;
            float  _NoiseSpeed;
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
                // skin (a flow/halo, not a decal). Free: no new geometry.
                worldPos += worldNormal * _PuffDistance;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.worldNormal = worldNormal;
                o.worldPos = worldPos;
                return o;
            }

            // Cheap flowing turbulence — layered sines along the wind axis +
            // lateral, animated. Returns 0..1, contrast-biased into wisps.
            float turbulence(float3 p, float3 wind, float t)
            {
                float along = dot(p, wind) * _NoiseScale;
                float lat = (p.x + p.z) * _NoiseScale;
                float n = sin(along * 1.0 + t)
                        + sin(along * 2.3 - t * 1.7 + lat * 1.5) * 0.6
                        + sin(lat * 3.1 + t * 2.3) * 0.4;
                n = n * 0.5 + 0.5;             // ~0..1
                return saturate(n * 1.5 - 0.25); // sharpen into wisps
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                float3 n = normalize(i.worldNormal);
                float3 wind = normalize(_WindDirWorld.xyz);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                float windward = saturate(dot(n, wind));
                windward *= windward;
                float rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                float turb = turbulence(i.worldPos, wind, _Time.y * _NoiseSpeed);

                float base = windward * 0.8 + rim * 0.5;
                float glow = base * lerp(0.35, 1.2, turb) * _Intensity;

                // Pale wind → orange plasma as intensity (heating) rises.
                float3 col = lerp(_WindColor.rgb, _PlasmaColor.rgb, saturate(_Intensity));
                return fixed4(col * glow, 1.0);
            }
            ENDCG
        }
    }
}
