using System.Collections.Generic;

namespace Game.Core
{
    /// <summary>
    /// Modifier 作用的目标数值（Docs/13 §5.2 v3）。注意：原 CameraKick 已更名 AimRecoil——
    /// 在进入 WeaponRecoilState 前作用于冲量，同源影响 FireRay 与相机；不允许仅相机独立的
    /// Pitch/Yaw 后坐倍率（破坏恒等约束）。仅 ViewModelKick 与位置 Shake 允许独立表现倍率。
    /// </summary>
    public enum WeaponStatId
    {
        VerticalRecoil,      // 垂直后坐（度值型：base=Recoil.PitchDeg）
        HorizontalRecoil,    // 水平后坐（度值型：base=Recoil.YawDeg）
        AimRecoil,           // 瞄准后坐总倍率（倍率型：base=1，同源作用弹道+相机）
        ViewModelKick,       // Viewmodel 视觉 kick 倍率（倍率型：base=1）
        RecoilRecovery,      // Burst 恢复速度倍率（倍率型：base=1）
        RecoilPatternScale,  // Pattern 缩放倍率（倍率型：base=1，Pattern 资产预留）
        FirstShotRecoil,     // 首枪冲量倍率（倍率型：base=1）
        AdsRecoil,           // ADS 后坐倍率（倍率型：base=1，叠加在 AdsRecoilMultiplier 上）
        Spread               // 散布总倍率（倍率型：base=1，作用于合成公式末端）
    }

    public enum ModifierOperation
    {
        Add,       // 平铺加法（度值型：直接加基础值；倍率型：加在 1 上）
        Multiply   // 倍率连乘
    }

    /// <summary>
    /// 不可变修饰符描述（Docs/13 §6.2 v3）：来源可精确追踪与移除（卸配件 / Buff 到期 /
    /// 技能重置 → 按 SourceId 剔除后重算）。Day4 无任何生效来源，仅接口就绪。
    /// </summary>
    [System.Serializable]
    public readonly struct WeaponStatModifier
    {
        public readonly WeaponStatId Stat;
        public readonly ModifierOperation Op;
        public readonly float Value;
        public readonly string SourceId;   // "scope.x8" / "skill.recoil1" / "buff.rage"

        public WeaponStatModifier(WeaponStatId stat, ModifierOperation op, float value, string sourceId)
        {
            Stat = stat;
            Op = op;
            Value = value;
            SourceId = sourceId;
        }
    }

    /// <summary>
    /// Modifier 来源接口（配件 &lt; 技能 &lt; Buff 按 Priority 固定叠加序）。
    /// 实现方返回只读快照，不得改写共享状态；Resolver 统一排序/合成/防护/Clamp。
    /// </summary>
    public interface IWeaponStatModifierSource
    {
        int Priority { get; }               // 配件(0) < 技能(10) < Buff(20)
        string SourceId { get; }
        IReadOnlyList<WeaponStatModifier> GetModifiers();
    }
}
