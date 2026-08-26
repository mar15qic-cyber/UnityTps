namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心只读状态快照（Docs/13 §5.2）：Presenter 写、View 读。纯数据、无 Unity 引用——
    /// 保证 View 不触碰任何 Gameplay 类型（MVC 边界）。
    /// </summary>
    public sealed class CrosshairModel
    {
        /// <summary>当前显示 Gap（px，向 TargetGap 平滑）。</summary>
        public float CurrentGap;
        /// <summary>目标 Gap（px，=真实散布角物理映射）。</summary>
        public float TargetGap;
        /// <summary>准心可见（ADS≥0.5 隐藏 / 冲刺可配置隐藏）。</summary>
        public bool Visible = true;
        /// <summary>命中标记剩余秒数（>0 显示）。</summary>
        public float HitMarkerRemaining;
    }
}
