Shader "MateEngine/CompanionProp"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        [Enum(Off,0,On,1)] _ZWrite ("Write depth", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Geometry" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct Interpolated { float4 vertex : SV_POSITION; float light : TEXCOORD0; };
            fixed4 _Color;
            Interpolated vert(Input value)
            {
                Interpolated result;
                result.vertex = UnityObjectToClipPos(value.vertex);
                result.light = .65 + .35 * saturate(dot(UnityObjectToWorldNormal(value.normal), normalize(float3(-.4,.7,-.6))));
                return result;
            }
            fixed4 frag(Interpolated value) : SV_Target
            {
                return fixed4(_Color.rgb * value.light, _Color.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
