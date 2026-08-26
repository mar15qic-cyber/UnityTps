using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心表现参数（Docs/13 §5.2 v3）：颜色/尺寸/动画速度——不含任何 Gameplay 数值
    /// （散布角来自 WeaponController.CurrentSpreadDegrees，映射公式在 Presenter）。
    /// </summary>
    [CreateAssetMenu(menuName = "UnityFps/HUD/Crosshair Config", fileName = "CrosshairConfig")]
    public sealed class CrosshairConfig : ScriptableObject
    {
        [Header("线条")]
        public Color LineColor = new(1f, 1f, 1f, 0.9f);
        public float LineLength = 14f;          // px
        public float LineThickness = 2f;        // px

        [Header("中心点")]
        public bool ShowCenterDot = true;       // §9-8 拍板：默认开
        public float DotSize = 3f;              // px

        [Header("间距映射")]
        [Tooltip("Gap 角度→像素的额外缩放（1=物理映射：pxPerDeg）")]
        public float GapScale = 1f;
        public float MinGap = 6f;               // px（静止收紧下限）
        public float MaxGap = 160f;             // px（防极端分辨率/数值越界）

        [Header("动态")]
        [Tooltip("显示值向目标 Gap 的收敛速度（px/s）；Bloom/恢复由 Gameplay 数值驱动")]
        public float SmoothSpeed = 240f;
        public bool HideOnSprint;               // 冲刺时隐藏准心

        [Header("命中标记")]
        public Color HitMarkerColor = new(1f, 0.3f, 0.3f, 1f);
        public float HitMarkerSeconds = 0.25f;
    }
}
