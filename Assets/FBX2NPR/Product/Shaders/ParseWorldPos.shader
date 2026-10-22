// 라벨 투영용 데이터 패스. 화소마다 그 자리에 보이는 표면의 월드 위치(xyz)와 '지오메트리 있음'(a=1)을 float 로 씁니다.
// 카메라·URP 를 거치지 않고 Graphics.DrawMeshNow 로 직접 그립니다(URP 중간 버퍼의 8비트 양자화를 피하려고).
// 알파 컷아웃 카드(머리카락·속눈썹)는 _AlphaClip 이 켜지면 원본 알베도의 알파로 잘라, 색 렌더와 같은 곳만 가립니다.
Shader "FBX2NPR/ParseWorldPos"
{
    Properties
    {
        _BaseMap ("알베도(알파용)", 2D) = "white" {}
        _Cutoff ("컷아웃 문턱", Range(0, 1)) = 0.5
        _AlphaClip ("컷아웃 켬", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _BaseMap; float4 _BaseMap_ST; float _Cutoff; float _AlphaClip;
            struct a2v { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; float2 uv : TEXCOORD1; };
            v2f vert(a2v v)
            {
                v2f o;
                o.wp = mul(unity_ObjectToWorld, float4(v.pos.xyz, 1)).xyz;
                o.pos = UnityObjectToClipPos(v.pos);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                if (_AlphaClip > 0.5) clip(tex2D(_BaseMap, i.uv).a - _Cutoff);
                return float4(i.wp, 1);
            }
            ENDCG
        }
    }
}
