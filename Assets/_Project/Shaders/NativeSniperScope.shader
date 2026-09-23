Shader "Game/UI/NativeSniperScope"
{
    Properties
    {
        [PerRendererData] _MainTex ("LPFP Scope Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ZTest ("Depth Test", Float) = 8
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Transparent+1" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off Lighting Off ZWrite Off ZTest [_ZTest]
        // Preserve the original LPFP alpha; transparent RGB contains import edge dilation.
        // Multiplication keeps white neutral and the original black crosshair dark.
        Blend DstColor Zero
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; float4 local:TEXCOORD1; };
            sampler2D _MainTex; fixed4 _Color; float4 _ClipRect;
            v2f vert(appdata v) { v2f o; o.local=v.vertex; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color*_Color; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                fixed4 texel=tex2D(_MainTex,i.uv);
                float opacity=i.color.a*texel.a;
                #ifdef UNITY_UI_CLIP_RECT
                opacity*=UnityGet2DClipping(i.local.xy,_ClipRect);
                #endif
                return fixed4(lerp(fixed3(1,1,1),texel.rgb,opacity),1);
            }
            ENDCG
        }
    }
}
