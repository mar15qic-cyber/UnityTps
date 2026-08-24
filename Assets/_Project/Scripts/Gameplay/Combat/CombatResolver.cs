using Game.Gameplay.Health;
using UnityEngine;

namespace Game.Gameplay.Combat
{
    public readonly struct HitscanResult
    {
        public readonly bool Hit;
        public readonly bool Damaged;
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        public readonly DamageableTarget Target;

        public HitscanResult(bool hit, bool damaged, Vector3 point, Vector3 normal, DamageableTarget target)
        {
            Hit = hit;
            Damaged = damaged;
            Point = point;
            Normal = normal;
            Target = target;
        }
    }

    /// <summary>
    /// 命中判定单点（架构表A）：射线检测 → 伤害应用。服务器权威版在 Day8；
    /// 本地版与服务器版共用同一判定代码，联网时只换调用方。
    /// </summary>
    public sealed class CombatResolver : MonoBehaviour
    {
        public HitscanResult ResolveHitscan(
            Vector3 origin, Vector3 direction, float maxRange, int damage, int layerMask, Transform ignoreRoot)
        {
            var ray = new Ray(origin, direction);
            if (!Physics.Raycast(ray, out var hitInfo, maxRange, layerMask, QueryTriggerInteraction.Ignore))
                return new HitscanResult(false, false, origin + direction * maxRange, Vector3.up, null);

            // 忽略 shooter 自身（root 比较）
            if (ignoreRoot != null && hitInfo.collider.transform.root == ignoreRoot)
                return new HitscanResult(false, false, hitInfo.point, hitInfo.normal, null);

            var target = hitInfo.collider.GetComponentInParent<DamageableTarget>();
            if (target != null && target.IsAlive)
            {
                target.ApplyDamage(damage, hitInfo.point, direction);
                return new HitscanResult(true, true, hitInfo.point, hitInfo.normal, target);
            }

            return new HitscanResult(true, false, hitInfo.point, hitInfo.normal, null);
        }
    }
}
