// 회화풍 모드 눈매 잉크 섞기. 붓질 텍스처(_MainTex)의 알파에 변환 도구가 구워 둔 '원본이 피부보다 어두운 정도' 비중을,
// PainterlyRig.eyeInk 슬라이더로 조절해 원본(_Orig)의 어두운 선을 Darken(min) 으로 얹는다. Graphics.Blit 전용 (화면에 그리는 셰이더가 아님).
Shader "Hidden/FBX2NPR/InkBlend"
{
    Properties
    {
        _MainTex ("붓질 텍스처 (알파 = 잉크 비중)", 2D) = "white" {}
        _Orig ("원본 텍스처", 2D) = "white" {}
        _EyeInk ("눈매 잉크 세기", Range(0, 1)) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex; sampler2D _Orig; float _EyeInk;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float4 o = tex2D(_Orig, i.uv);
                float w = saturate(b.a * _EyeInk);
                return float4(lerp(b.rgb, min(b.rgb, o.rgb), w), 1);
            }
            ENDCG
        }
    }
}
