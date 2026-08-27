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
        /// <summary>被跳过的 shooter 自身碰撞体数（诊断/测试：拖尾异常时确认是否踩到自身）。</summary>
        public readonly int SelfHitsSkipped;

        public HitscanResult(bool hit, bool damaged, Vector3 point, Vector3 normal, DamageableTarget target,
            int selfHitsSkipped = 0)
        {
            Hit = hit;
            Damaged = damaged;
            Point = point;
            Normal = normal;
            Target = target;
            SelfHitsSkipped = selfHitsSkipped;
        }
    }

    /// <summary>
    /// 命中判定单点（架构表A）：射线检测 → 伤害应用。服务器权威版在 Day8；
    /// 本地版与服务器版共用同一判定代码，联网时只换调用方。
    /// </summary>
    public sealed class CombatResolver : MonoBehaviour
    {
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        public HitscanResult ResolveHitscan(
            Vector3 origin, Vector3 direction, float maxRange, int damage, int layerMask, Transform ignoreRoot)
        {
            // Day4 实机审计 §1：旧版首命中为自身碰撞体时直接返回自身命中点（Hit=false 但 Point 贴脸），
            // 表现层把它当拖尾终点 → 偶发竖直/向下短线，且丢失其后真实命中。
            // 现改为遍历全部命中（RaycastNonAlloc），跳过 ignoreRoot 下所有碰撞体，取最近非自身命中；
            // 无非自身命中时 Point 必须是 origin + dir*maxRange（远点），绝不返回自身命中点。
            Vector3 dir = direction.normalized;
            int count = Physics.RaycastNonAlloc(
                new Ray(origin, dir), _hits, maxRange, layerMask, QueryTriggerInteraction.Ignore);

            int selfSkipped = 0;
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                var collider = _hits[i].collider;
                if (collider == null) continue;
                if (ignoreRoot != null && collider.transform.root == ignoreRoot)
                {
                    selfSkipped++;
                    continue;
                }
                if (best < 0 || _hits[i].distance < _hits[best].distance) best = i;
            }

            if (best < 0)
                return new HitscanResult(false, false, origin + dir * maxRange, Vector3.up, null, selfSkipped);

            var hitInfo = _hits[best];
            var target = hitInfo.collider.GetComponentInParent<DamageableTarget>();
            if (target != null && target.IsAlive)
            {
                target.ApplyDamage(damage, hitInfo.point, dir);
                return new HitscanResult(true, true, hitInfo.point, hitInfo.normal, target, selfSkipped);
            }

            return new HitscanResult(true, false, hitInfo.point, hitInfo.normal, null, selfSkipped);
        }
    }
}
