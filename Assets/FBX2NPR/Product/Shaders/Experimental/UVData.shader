// 렌더 공간 → 텍셀 대응용 데이터 패스. 화소마다 그 자리에 보이는 표면의 UV(xy)와 '지오메트리 있음'(a=1)을 float 로 씁니다.
// 카메라·URP 를 거치지 않고 Graphics.DrawMeshNow 로 직접 그립니다 (8비트·sRGB 양자화를 피하려고). FBX2NPRLipSymmetry 가 씁니다.
Shader "FBX2NPR/UVData"
{
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
            struct a2v { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(a2v v) { v2f o; o.pos = UnityObjectToClipPos(v.pos); o.uv = v.uv; return o; }
            float4 frag(v2f i) : SV_Target { return float4(i.uv, 0, 1); }
            ENDCG
        }
    }
}
