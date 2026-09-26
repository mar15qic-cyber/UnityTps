using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// 碰撞体在射击判定中的角色（2026-09-18 审计 §5：受击与移动阻挡职责分离）。
    ///
    /// 背景：玩家可受击体（TP_Model/BodyHitbox）与移动阻挡体（根 CharacterController）是两套
    /// 几何，实测射线表面距离差 0.04–0.29m。旧实现允许"命中根 CC → 到 CC 所在对象的子树里猜一个
    /// DamageableTarget"来归属伤害，于是**打模型后方的空处也会掉血并在空气中出现实体弹孔**。
    /// 现在移动阻挡体在"选择最近有效几何命中"阶段就被排除：它既不吃伤害，也不替身后的目标吞子弹。
    /// </summary>
    public enum HitVolumeRole
    {
        /// <summary>显式受击体：可归属伤害、可作为命中落点。</summary>
        DamageSurface = 0,
        /// <summary>只挡移动：射击判定整体跳过（玩家根 CharacterController）。</summary>
        MovementBlocker = 1,
    }

    /// <summary>
    /// 显式角色标记：挂在碰撞体自身 GameObject 上，运行时装配（零 prefab 改动）。
    ///
    /// 刻意**不按名字**识别（"Player"子串不算依据），也不做全局规则：没有本组件的碰撞体
    /// （墙体、普通可破坏目标、与玩家无关的 CharacterController）判定行为完全不变。
    /// </summary>
    public sealed class HitVolumeTag : MonoBehaviour
    {
        [SerializeField] private HitVolumeRole role;
        [SerializeField] private HitBodyRegion bodyRegion;

        public HitVolumeRole Role => role;
        public HitBodyRegion BodyRegion => bodyRegion;

        /// <summary>运行时装配入口（幂等：已存在则只校正角色，不重复添加）。</summary>
        public static HitVolumeTag Assign(GameObject go, HitVolumeRole assigned,
            HitBodyRegion region = HitBodyRegion.Torso)
        {
            if (go == null) return null;
            var tag = go.GetComponent<HitVolumeTag>();
            if (tag == null) tag = go.AddComponent<HitVolumeTag>();
            tag.role = assigned;
            tag.bodyRegion = region;
            return tag;
        }
    }
}
