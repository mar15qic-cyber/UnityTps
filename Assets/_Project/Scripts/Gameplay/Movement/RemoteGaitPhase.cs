using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>
    /// 远端步态相位本地积分器（2026-09-17 方案B，纯逻辑可离线测试）。
    ///
    /// 背景：服务器步态相位以固定速率 1/周期 推进（RootMotionProfile.EvaluateDelta：
    /// phaseAdvance = dt/cycleDuration，与移动输入无关），但同步到远端只有 10Hz
    /// （FishNet SyncVar 默认 SendRate=0.1s），而 TPAnimDriver 的混合器 Speed=0 全靠
    /// 逐帧覆写相位——腿部动画以 10Hz 跳变（实机"翻小人书"）。
    /// 修复哲学与位置缓冲插值一致：**渲染与同步解耦**——远端按同一周期常量本地连续推进相位，
    /// 10Hz 同步相位仅作低频锚定（超阈值对位、小误差指数拉拢，速率一致时误差≈0 锚定不可见）。
    /// </summary>
    public static class RemoteGaitPhase
    {
        /// <summary>走步态周期兜底（与 Locomotor 无 Profile 分支常量一致）。</summary>
        public const float FallbackWalkCycleSeconds = 0.9333334f;
        /// <summary>冲刺步态周期兜底。</summary>
        public const float FallbackSprintCycleSeconds = 0.6666667f;

        /// <summary>本地推进：相位 += dt/周期并包裹到 [0,1)。周期非法时保持原值。</summary>
        public static float Advance(float phase, float deltaTime, float cycleSeconds)
        {
            if (cycleSeconds <= 0.0001f) return phase;
            return Mathf.Repeat(phase + Mathf.Max(0f, deltaTime) / cycleSeconds, 1f);
        }

        /// <summary>相位差 anchor−current，包裹到 [-0.5, 0.5)（最短路径，跨 0/1 边界正确）。</summary>
        public static float PhaseError(float current, float anchor)
        {
            return Mathf.Repeat(anchor - current + 0.5f, 1f) - 0.5f;
        }

        /// <summary>
        /// 低频锚定纠偏：|误差| ≥ snapThreshold 直接对位（状态切换/重生/长时间漂移）；
        /// 否则按 pullPerSecond 指数拉拢（dt 秒步长）。返回值恒在 [0,1)。
        /// </summary>
        public static float CorrectToward(float current, float anchor, float snapThreshold, float pullPerSecond, float deltaTime)
        {
            float error = PhaseError(current, anchor);
            if (Mathf.Abs(error) >= snapThreshold) return Mathf.Repeat(anchor, 1f);
            float alpha = 1f - Mathf.Exp(-Mathf.Max(0f, pullPerSecond) * Mathf.Max(0f, deltaTime));
            return Mathf.Repeat(current + error * alpha, 1f);
        }
    }
}
