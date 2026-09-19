using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 第一人称武器视图"表现可见性/生命周期"闸门（审计 2026-09-16 §4）。
    /// Gameplay 定义接口、Presentation 实现（FPWeaponRig）——保持 Gameplay→Presentation 单向依赖。
    /// 为什么需要：LaserSightBeam 这类**持续写者**（每帧 LateUpdate 直接 enabled=true）必须读取统一
    /// 的死亡/镜内/复活准备闸门，否则视图被隐藏后下一帧又自行打开（"枪隐形但激光仍在"）。
    /// </summary>
    public interface IWeaponPresentationGate
    {
        /// <summary>true=允许该视图上的持续表现写者运行（视图有效且未被任何隐藏原因命中）。</summary>
        bool IsWeaponViewVisible { get; }

        /// <summary>闸门当前认定的有效视图（池化/切枪时用于失效写者缓存；null=无有效视图）。</summary>
        GameObject ActiveView { get; }
    }
}
