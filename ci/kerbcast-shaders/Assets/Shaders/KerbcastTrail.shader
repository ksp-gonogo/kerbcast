// kerbcast atmospheric-FX trail shader. Drawn additively over a procedural
// tapered tube (built in TrailEffect.cs) that streams behind the vessel along
// -velocity. Composites against the near render's depth so it occludes
// correctly against terrain/ships in front of it.
//
// Look: a plasma wake. Bright at the vessel end, fading to nothing by the
// tail. Scrolling streaks along the tube length read as "flowing backward".
// Colour stays wind-white through moderate intensities; plasma-orange shift
// only blends in at hard reentry, same convention as KerbcastPlasma.
//
// UVs (set by mesh): uv.y runs 0 at the vessel-end → 1 at the tail; uv.x runs
// 0→1 around the tube circumference (seam-duplicated). Cull Off so both the
// outer and inner sides of the tube render — looking from inside the wake
// should still show.
Shader "Kerbcast/Trail"
{
    Properties
    {
        _WindColor   ("Wind Colour (low intensity)", Color) = (0.85, 0.92, 1.0, 1)
        _PlasmaColor ("Plasma Colour (high intensity)", Color) = (1.0, 0.45, 0.15, 1)
        _Intensity   ("Intensity", Range(0,4)) = 0
        _WindDirWorld("Wind Dir (world)", Vector) = (0,1,0,0)
        _ScrollSpeed ("Scroll Speed", Float) = 3.0
        _StreakFreq  ("Streak Frequency (along)", Float) = 18.0
        _CrossFreq   ("Streak Frequency (around)", Float) = 6.0
        _FadePower   ("Tail Fade Power", Range(0.5,8)) = 3.2
        // Brightness ceiling — caps the trail's peak output so a maxed-out
        // intensity doesn't paint a solid wall in front of the camera. The
        // wake should read as a moving texture, not a filled colour.
        _Brightness  ("Brightness Ceiling", Range(0.1, 1.5)) = 0.55
        // Streak contrast: higher = more wisp-like, more dark gaps between
        // streaks; lower = more uniform glow.
        _StreakContrast ("Streak Contrast", Range(1.0, 4.0)) = 2.4
        // Plasma colour only blends in above this intensity (reserved for
        // heavy reentry); below it stays wind-white.
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
            Cull Off        // show both inside and outside of the tube

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _WindColor;
            float4 _PlasmaColor;
            float  _Intensity;
            float4 _WindDirWorld;
            float  _ScrollSpeed;
            float  _StreakFreq;
            float  _CrossFreq;
            float  _FadePower;
            float  _Brightness;
            float  _StreakContrast;
            float  _PlasmaOnset;

            // KSP FXCamera globals — published process-wide by the stock
            // aero-FX system. Provide the same tuned plasma noise texture
            // and real-heating colour stock plasma uses, so the trail
            // visually agrees with KSP's own FX in colour and animation.
            sampler2D _FXMainTex;
            float4    _FXColor;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.texcoord.xy;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Intensity <= 0.0001) return fixed4(0, 0, 0, 0);

                // Length fade — bright at vessel end (uv.y=0), gone by the
                // tail (uv.y=1). pow gives a soft front and a long thin tail.
                float lenFade = pow(saturate(1.0 - i.uv.y), _FadePower);

                // Head fade — ramps the trail in over its first ~5% length
                // so the cylinder's vessel-end RING isn't a hard bright
                // disc against the dark sky. Combined with the head of the
                // tube positioned slightly inside the vessel body (occluded
                // by the cylinder), the wake reads as emerging continuously
                // from the craft rather than starting at a hard edge.
                float headFade = smoothstep(0.0, 0.05, i.uv.y);

                // Scroll speed scales with intensity — faster wake at higher
                // mach. Sample KSP's tuned plasma noise scrolled along the
                // tube length (uv.y - t) so streaks visibly pull toward the
                // tail. _CrossFreq tiles the texture around the tube.
                float scrollT = _Time.y * _ScrollSpeed * (0.5 + _Intensity);
                float2 fxUv = float2(i.uv.x * _CrossFreq,
                                     i.uv.y * _StreakFreq * 0.05 - scrollT * 0.12);
                float fxNoise = tex2D(_FXMainTex, fxUv).r;
                // Sharpen into wispy streaks — _StreakContrast controls how
                // hard the dark/bright gap is. Combined with a second
                // octave scrolled at half speed for some sub-streak detail.
                float streaks = pow(saturate(fxNoise), _StreakContrast);
                float fxNoise2 = tex2D(_FXMainTex, float2(i.uv.x * _CrossFreq * 0.5,
                                                          i.uv.y * _StreakFreq * 0.07 - scrollT * 0.06)).r;
                streaks = max(streaks, pow(saturate(fxNoise2), _StreakContrast) * 0.6);

                // Soft radial pulse toward the front so the head reads as a
                // hot core, not a uniform tube.
                float head = pow(saturate(1.0 - i.uv.y * 1.4), 2.0);

                // Face-weighted volume term: bright in the centre of the
                // visible tube cross-section (camera looking through the
                // hot core, where the surface normal is parallel to the
                // view), fades to zero at the silhouette edges (where the
                // normal is perpendicular to the view). Cull Off means
                // both near and far surfaces contribute additively — they
                // stack in the centre and fall off together at the rim,
                // giving a flame-like glow with no hard mesh outline.
                float3 nrm = normalize(i.worldNormal);
                float3 vDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float faceAbs = abs(dot(nrm, vDir));
                float volume = pow(faceAbs, 1.5);

                // Brightness held in by _Brightness so even at _Intensity=1
                // the trail's peak fragment doesn't paint a wall. headFade
                // suppresses the hard top ring at uv.y=0.
                float glow = (streaks * 0.85 + head * 0.4) * lenFade * volume * headFade
                             * saturate(_Intensity) * _Brightness;

                // Wind→plasma colour ramp, then tinted toward KSP's stock
                // heating colour at high real heating. Stays wind-white when
                // FXColor.a is low (no real heating yet).
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
