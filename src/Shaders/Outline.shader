Shader "VirtualDresser/Outline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.15, 0.65, 1.0, 1.0)
        _OutlineWidth ("Outline Width", Range(0.0, 0.05)) = 0.007
    }
    SubShader
    {
        // Overlay(4000): Opaque/Cutout/Transparent 모두 렌더된 뒤 실행
        // → lilToon Transparent(3000), Cutout(2450) 포함 모든 메쉬 이후에 아웃라인 렌더
        Tags { "RenderType"="Overlay" "Queue"="Overlay" }

        Pass
        {
            Name "OUTLINE"
            Cull Front      // 뒷면만 렌더 → 앞면이 클리핑해 아웃라인 실루엣 생성
            ZWrite Off      // 깊이 버퍼 안 씀
            ZTest LEqual    // 메인 메쉬 깊이 이하이면 출력 (아웃라인이 몸 뒤로 안 뚫림)

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _OutlineColor;
            float  _OutlineWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 뷰 공간 노말로 clip-space에서 확장 → 화면 크기와 무관한 균일한 두께
                float3 viewNorm = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal));
                float4 clipPos  = UnityObjectToClipPos(v.vertex);
                float2 offset   = TransformViewToProjection(viewNorm.xy);
                clipPos.xy     += offset * (_OutlineWidth * clipPos.w);
                o.pos = clipPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }
    }
    FallBack Off
}
