using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心只读状态快照（Docs/13 §5.2）：Presenter 写、View 读。纯数据、无 Unity 引用——
    /// 保证 View 不触碰任何 Gameplay 类型（MVC 边界）。
    /// Day4 回归修复：Gap 统一以实际屏幕像素保存；View 在写入 Canvas RectTransform 时
    /// 再除以 Canvas.scaleFactor，避免 CanvasScaler 对物理像素进行二次缩放。
    /// </summary>
    public sealed class CrosshairModel
    {
        /// <summary>当前显示 Gap（实际屏幕 px，向 TargetGapPx 收拢）。</summary>
        public float CurrentGap;
        /// <summary>目标 Gap（实际屏幕 px，=真实散布角物理映射）。</summary>
        public float TargetGap;
        /// <summary>兼容旧调试字段；新实现不再叠加固定 ShotPulse。</summary>
        public float ShotPulse;
        /// <summary>最近一帧的真实散布锥角（度，调试快照：与 Pulse 区分）。</summary>
        public float LastSpreadDegrees;
        /// <summary>准心可见（ADS≥0.5 隐藏 / 冲刺可配置隐藏）。</summary>
        public bool Visible = true;
        /// <summary>命中标记剩余秒数（>0 显示）。</summary>
        public float HitMarkerRemaining;

        /// <summary>脉冲回落（px/s·dt）。独立方法+显式 dt——EditMode 可直测（审计 §2）。</summary>
        public void TickPulse(float decayPxPerSec, float dt)
        {
            if (ShotPulse > 0f)
                ShotPulse = Mathf.Max(0f, ShotPulse - Mathf.Max(0f, decayPxPerSec) * Mathf.Max(0f, dt));
        }
    }
}
