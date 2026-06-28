Shader "Kerbcast/NightVision"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Gain    ("Gain",   Range(1, 10)) = 4.0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Gain;
            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                // Multiplicative gain lifts dark scenes — the feature
                // HullcamVDS's additive-shift NightVision filter omits.
                float luma = dot(c.rgb, fixed3(0.299, 0.587, 0.114));
                float v = saturate(luma * _Gain);
                // NVG phosphor: full green, 15% red, 5% blue.
                return fixed4(v * 0.15, v, v * 0.05, c.a);
            }
            ENDCG
        }
    }
}
