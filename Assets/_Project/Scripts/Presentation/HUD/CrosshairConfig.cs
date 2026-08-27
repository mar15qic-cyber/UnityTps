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
        public bool ShowCenterDot = false;      // Day4 回归：纯四线十字，不显示中心点
        public float DotSize = 3f;              // px

        [Header("间距映射")]
        [Tooltip("Gap 角度→像素的额外缩放（1=物理映射：pxPerDeg）")]
        public float GapScale = 1f;
        public float MinGap = 6f;               // px（静止收紧下限）
        public float MaxGap = 160f;             // px（防极端分辨率/数值越界）

        [Header("动态")]
        [Tooltip("散布收拢速度（实际屏幕 px/s）；扩张不平滑，保证跳跃/开火反馈即时")]
        public float SmoothSpeed = 180f;
        public bool HideOnSprint;               // 冲刺时隐藏准心

        [Header("开火脉冲")]
        [Tooltip("已废弃：准心只显示真实散布/Bloom，避免固定脉冲掩盖武器差异")]
        public float ShotPulsePerShotPx = 0f;
        [Tooltip("已废弃：保留字段以兼容旧资产")]
        public float ShotPulseMaxPx = 0f;
        [Tooltip("已废弃：保留字段以兼容旧资产")]
        public float ShotPulseDecayPxPerSec = 0f;

        [Header("命中标记")]
        public Color HitMarkerColor = new(1f, 0.3f, 0.3f, 1f);
        public float HitMarkerSeconds = 0.25f;
    }
}
