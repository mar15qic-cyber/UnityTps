using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 精度状态（Docs/13 §6.3 v3）：连射 Bloom 累计/延迟门控衰减 + 情境合成（腰射↔ADS、
    /// 移动、冲刺、跳跃）。纯 C#、dt 注入；CurrentSpread 是弹道锥角与准心 HUD 的同一数据源。
    /// 停火不 HardReset（BloomRecoveryDelay 门控后自然衰减）；切枪/死亡才全清。
    /// </summary>
    public sealed class WeaponAccuracyState
    {
        /// <summary>当前连射 Bloom（度，≤MaxBloom）。</summary>
        public float CurrentBloom { get; private set; }
        /// <summary>距上一发秒数（Bloom 恢复门控）。</summary>
        public float TimeSinceLastShot { get; private set; }

        /// <summary>
        /// 合成当前散布锥角（度）：Lerp(hip,ads,Ads01) + 移动惩罚 + 冲刺 + 空中惩罚 + Bloom，
        /// 末端乘 Spread 修饰并 Clamp [0,45°]（Docs/13 §6.3）。
        /// </summary>
        public float CurrentSpread(in WeaponFireContext ctx, in ResolvedWeaponStats s)
        {
            var a = s.Stat.Accuracy;
            float spread = Mathf.Lerp(a.BaseHipSpread, a.BaseAdsSpread, ctx.Ads01)
                + Mathf.Lerp(0f, a.MovementSpreadMax, ctx.HorizontalSpeed01)
                + (ctx.IsSprinting ? a.SprintSpreadExtra : 0f)
                + (!ctx.IsGrounded ? a.AirborneSpreadExtra : 0f)
                + CurrentBloom;
            return Mathf.Clamp(spread * s.SpreadScale, 0f, 45f);
        }

        /// <summary>五步顺序第③步：本发 Bloom 累计（影响下一发，§5.3-5）。</summary>
        public void OnShot(in ResolvedWeaponStats s)
        {
            var a = s.Stat.Accuracy;
            CurrentBloom = Mathf.Min(CurrentBloom + a.ShotBloomPerShot, a.MaxBloom);
            TimeSinceLastShot = 0f;
        }

        /// <summary>每帧：BloomRecoveryDelay 门控后按度/秒衰减。</summary>
        public void Tick(float deltaTime, in ResolvedWeaponStats s)
        {
            if (deltaTime <= 0f) return;
            TimeSinceLastShot += deltaTime;
            if (TimeSinceLastShot > s.Stat.Accuracy.BloomRecoveryDelay && CurrentBloom > 0f)
                CurrentBloom = Mathf.Max(0f, CurrentBloom - s.Stat.Accuracy.BloomRecoverySpeed * deltaTime);
        }

        /// <summary>硬重置：仅切枪/死亡/组件禁用。</summary>
        public void HardReset()
        {
            CurrentBloom = 0f;
            TimeSinceLastShot = 0f;
        }
    }
}
