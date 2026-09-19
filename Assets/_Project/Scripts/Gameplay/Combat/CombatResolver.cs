using Game.Gameplay.Health;
using Game.Gameplay.Network;
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

    /// <summary>几何命中（不含伤害应用）：两段命中与单段结算共用的射线产物。</summary>
    public readonly struct GeometryHit
    {
        public readonly bool Hit;
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        public readonly DamageableTarget Target;
        public readonly float Distance;
        public readonly int SelfHitsSkipped;
        // 2026-09-16 审计 §6.1：落点必须能追到具体碰撞体（旧诊断只有坐标，X=9.82 无法命名），
        // 并区分"命中但不可归属/已死目标"与"根本没命中"。
        public readonly string ColliderName;
        public readonly int Layer;
        public readonly int OwnerObjectId;

        public GeometryHit(bool hit, Vector3 point, Vector3 normal, DamageableTarget target, float distance,
            int selfHitsSkipped, string colliderName = null, int layer = -1, int ownerObjectId = -1)
        {
            Hit = hit;
            Point = point;
            Normal = normal;
            Target = target;
            Distance = distance;
            SelfHitsSkipped = selfHitsSkipped;
            ColliderName = colliderName;
            Layer = layer;
            OwnerObjectId = ownerObjectId;
        }

        public string LayerName => Layer >= 0 ? LayerMask.LayerToName(Layer) : "-";
    }

    /// <summary>
    /// 单段射线的完整证据（审计 2026-09-16 §6.1）：起点/方向/最大距离/命中点/碰撞体名/层/所属
    /// NetworkObject/可归属目标及其存活状态。用于把"某发为什么没造成伤害"追到具体一段几何。
    /// </summary>
    public readonly struct FireRaySegment
    {
        public readonly string Label;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly float MaxDistance;
        public readonly bool Hit;
        public readonly Vector3 Point;
        public readonly float Distance;
        public readonly string ColliderName;
        public readonly int Layer;
        public readonly int OwnerObjectId;
        public readonly string TargetName;
        public readonly bool TargetAlive;

        public FireRaySegment(string label, Vector3 origin, Vector3 direction, float maxDistance,
            in GeometryHit hit)
        {
            Label = label;
            Origin = origin;
            Direction = direction;
            MaxDistance = maxDistance;
            Hit = hit.Hit;
            Point = hit.Point;
            Distance = hit.Distance;
            ColliderName = hit.ColliderName;
            Layer = hit.Layer;
            OwnerObjectId = hit.OwnerObjectId;
            TargetName = hit.Target != null ? hit.Target.name : null;
            TargetAlive = hit.Target != null && hit.Target.IsAlive;
        }

        public string Format()
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append(Label).Append(" o=").Append(Origin.ToString("F3"))
              .Append(" d=").Append(Direction.ToString("F3"))
              .Append(" max=").Append(MaxDistance.ToString("F2"))
              .Append(" hit=").Append(Hit ? 1 : 0);
            if (Hit)
                sb.Append(" dist=").Append(Distance.ToString("F3"))
                  .Append(" collider=").Append(string.IsNullOrEmpty(ColliderName) ? "-" : ColliderName)
                  .Append(" layer=").Append(Layer >= 0 ? LayerMask.LayerToName(Layer) : "-")
                  .Append(" ownerObj=").Append(OwnerObjectId.ToString())
                  .Append(" target=").Append(TargetName ?? "null")
                  .Append(" alive=").Append(TargetAlive ? 1 : 0);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一次两段权威命中的完整证据（审计 2026-09-16 §6.1/P0-H）。命中结果本身只有最终点，
    /// 本结构保留三段射线与判定分支，让"为什么这一发没打中玩家"可追、可对账。
    /// </summary>
    public readonly struct FireEvidence
    {
        public readonly FireRaySegment Camera;
        public readonly FireRaySegment Muzzle;
        public readonly FireRaySegment Body;
        public readonly TwoStageHitResolver.TwoStageDecision Decision;
        public readonly Vector3 CandidatePoint;
        public readonly string FinalCollider;
        public readonly int FinalLayer;
        public readonly int FinalOwnerObjectId;
        public readonly string FinalTargetName;
        public readonly bool FinalTargetAlive;
        /// <summary>未命中可归属存活目标的机械原因：""=造成伤害；否则见 MissReason 常量。</summary>
        public readonly string MissReason;
        /// <summary>Phase 6 回溯证据：本次结算实际使用的回溯 tick（0=未回溯）。</summary>
        public readonly uint RewindTick;
        /// <summary>Phase 6 回溯证据：请求 tick 被越窗裁剪（used != requested）。</summary>
        public readonly bool RewindClamped;

        public FireEvidence(in FireRaySegment camera, in FireRaySegment muzzle, in FireRaySegment body,
            TwoStageHitResolver.TwoStageDecision decision, Vector3 candidatePoint, in GeometryHit final,
            string missReason, uint rewindTick = 0, bool rewindClamped = false)
        {
            Camera = camera;
            Muzzle = muzzle;
            Body = body;
            Decision = decision;
            CandidatePoint = candidatePoint;
            FinalCollider = final.ColliderName;
            FinalLayer = final.Layer;
            FinalOwnerObjectId = final.OwnerObjectId;
            FinalTargetName = final.Target != null ? final.Target.name : null;
            FinalTargetAlive = final.Target != null && final.Target.IsAlive;
            MissReason = missReason;
            RewindTick = rewindTick;
            RewindClamped = rewindClamped;
        }

        public string Format()
        {
            var sb = new System.Text.StringBuilder(384);
            sb.Append("[FireGeom] reason=").Append(string.IsNullOrEmpty(MissReason) ? "OK" : MissReason)
              .Append(" decision=").Append(Decision)
              .Append(" rewind=").Append(RewindTick).Append(RewindClamped ? "(clamped)" : string.Empty)
              .Append(" candidate=").Append(CandidatePoint.ToString("F3"))
              .Append(" finalCollider=").Append(string.IsNullOrEmpty(FinalCollider) ? "-" : FinalCollider)
              .Append(" finalLayer=").Append(FinalLayer >= 0 ? LayerMask.LayerToName(FinalLayer) : "-")
              .Append(" finalOwnerObj=").Append(FinalOwnerObjectId.ToString())
              .Append(" finalTarget=").Append(FinalTargetName ?? "null")
              .Append(" finalAlive=").Append(FinalTargetAlive ? 1 : 0)
              .Append(" | ").Append(Camera.Format())
              .Append(" | ").Append(Muzzle.Format())
              .Append(" | ").Append(Body.Format());
            return sb.ToString();
        }
    }

    /// <summary>
    /// 命中判定单点（架构表A）：射线检测 → 伤害应用。服务器权威版在 Day8；
    /// 本地版与服务器版共用同一判定代码，联网时只换调用方。
    /// C3/Q04：TDM 友伤过滤（Docs/26 §2.4）——同队命中按阻挡处理（Hit=true/Damaged=false），
    /// 友军身体仍然吸收子弹（射线在此截断），只是不掉血；仅加过滤，不改射线解析。
    /// I4a/P4 两段权威命中（Docs/26 §5）：服务器路径用 ResolveHitscanTwoStage——
    /// ①相机候选（可信视点+实际射击方向）→ ②服务器逻辑枪口向候选点遮挡二次验证
    /// （候选点前被遮挡改用枪口路径最近命中）→ ③身体锚点防伸墙（枪口进墙时信任相机候选）；
    /// 两段查询在同一回溯窗口内完成，最终结果只应用一次伤害。
    /// </summary>
    public sealed class CombatResolver : MonoBehaviour
    {
        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private int _hitCount; // 最近一次 RaycastNonAlloc 的命中数（同根归属回退遍历用）

        /// <summary>未命中可归属存活目标的机械原因（[FireGeom] reason=）。</summary>
        public const string MissNone = "";
        public const string MissGeometry = "GEOMETRY";       // 三段都没打到任何碰撞体
        public const string MissNoTarget = "NO_TARGET";      // 打到碰撞体但父链无 DamageableTarget
        public const string MissTargetDead = "TARGET_DEAD";  // 打到玩家 hitbox 但该目标已死
        public const string MissFriendly = "FRIENDLY";       // 同队（TDM 友伤过滤）
        public const string MissTargetStale = "TARGET_STALE";         // Phase 2：回溯快照代际≠目标当前代际（旧生命位姿 vs 新生命）
        public const string MissTargetInvincible = "TARGET_INVINCIBLE"; // Phase 2：射击对应历史 tick 的快照显示目标处于出生保护

        /// <summary>最近一次两段命中的完整证据（诊断；服务器侧有意义）。</summary>
        public FireEvidence LastTwoStageEvidence { get; private set; }

        // 诊断节流：一发完整证据优先（审计 §6.1"补日志必须节流"）。静态=单进程一个节流门。
        private static float _nextEvidenceLogTime;
        /// <summary>两段命中证据的最小输出间隔（秒）。0=每发都输出。</summary>
        public static float EvidenceLogIntervalSeconds { get; set; } = 0.5f;
        /// <summary>证据日志总开关（正式构建可关；诊断轮保持开）。</summary>
        public static bool EvidenceLoggingEnabled { get; set; } = true;

        public HitscanResult ResolveHitscan(
            Vector3 origin, Vector3 direction, float maxRange, int damage, int layerMask, Transform ignoreRoot)
        {
            var geometry = ResolveGeometry(origin, direction, maxRange, layerMask, ignoreRoot);
            if (geometry.Hit && geometry.Target != null && geometry.Target.IsAlive)
            {
                // C3/Q04 TDM 友伤过滤（Docs/26 §2.4）：同队命中按阻挡处理——友军身体吸收子弹（射线截断）但不掉血
                if (IsFriendlyBlocked(ignoreRoot, geometry.Target))
                    return new HitscanResult(true, false, geometry.Point, geometry.Normal, geometry.Target, geometry.SelfHitsSkipped);
                geometry.Target.ApplyDamage(damage, geometry.Point, direction.normalized);
                return new HitscanResult(true, true, geometry.Point, geometry.Normal, geometry.Target, geometry.SelfHitsSkipped);
            }

            return geometry.Hit
                ? new HitscanResult(true, false, geometry.Point, geometry.Normal, null, geometry.SelfHitsSkipped)
                : new HitscanResult(false, false, origin + direction.normalized * maxRange, Vector3.up, null, geometry.SelfHitsSkipped);
        }

        /// <summary>
        /// I4a/P4 两段权威命中（服务器专用；离线单段路径走 ResolveHitscan）。
        /// 语义见 TwoStageHitResolver（R10 修正版）：相机候选 → 枪口遮挡验证（同源比较）→
        /// 枪口伸墙时采纳身体段可信侧遮挡命中（不豁免）；最终结果只应用一次伤害（含友伤过滤），
        /// 表现事件继续携带本发结果（拖尾连到最终点）。
        /// 2026-09-16 审计 §6.1：三段射线与判定分支全部留证（LastTwoStageEvidence），
        /// 并把"命中但无归属/已死/友军"与"没命中"在日志里区分开。
        /// </summary>
        public HitscanResult ResolveHitscanTwoStage(
            Vector3 cameraOrigin, Vector3 direction, float maxRange, int damage, int layerMask, Transform ignoreRoot,
            Vector3 muzzleOrigin, Vector3 bodyAnchor, LagCompRewindContext rewindContext = default)
        {
            var dir = direction.normalized;
            var cameraCandidate = ResolveGeometry(cameraOrigin, dir, maxRange, layerMask, ignoreRoot);
            Vector3 candidatePoint = cameraCandidate.Hit
                ? cameraCandidate.Point
                : cameraOrigin + dir * maxRange;

            // ③ 身体锚点 → 逻辑枪口穿墙检查：被阻断 = 枪口伸进墙体（R10：据此改判，不再豁免）
            Vector3 bodyToMuzzle = muzzleOrigin - bodyAnchor;
            float bodyToMuzzleDistance = bodyToMuzzle.magnitude;
            var bodySegment = bodyToMuzzleDistance > 0.001f
                ? ResolveGeometry(bodyAnchor, bodyToMuzzle / bodyToMuzzleDistance, bodyToMuzzleDistance, layerMask, ignoreRoot)
                : new GeometryHit(false, bodyAnchor, Vector3.up, null, 0f, 0);
            bool muzzleInsideWall = bodySegment.Hit;

            // ② 逻辑枪口 → 候选点遮挡检测（R10：候选距离与枪口命中距离同源于枪口，0.05m 容差有效）
            Vector3 toCandidate = candidatePoint - muzzleOrigin;
            float toCandidateDistance = toCandidate.magnitude;
            var muzzlePath = toCandidateDistance > 0.01f
                ? ResolveGeometry(muzzleOrigin, toCandidate / toCandidateDistance, toCandidateDistance, layerMask, ignoreRoot)
                : new GeometryHit(false, muzzleOrigin, Vector3.up, null, 0f, 0);

            var decision = TwoStageHitResolver.Decide(
                cameraCandidate.Hit, toCandidateDistance, muzzleInsideWall, muzzlePath.Hit, muzzlePath.Distance);

            GeometryHit final;
            switch (decision)
            {
                case TwoStageHitResolver.TwoStageDecision.UseMuzzleHit:
                    final = muzzlePath; // 枪口路径最近命中（含命中几何/墙体）
                    break;
                case TwoStageHitResolver.TwoStageDecision.UseBodySegmentHit:
                    // 枪口进墙：采纳身体→枪口段的遮挡命中（眼前墙/段内目标）——防御性无命中按 miss 处理
                    final = bodySegment.Hit
                        ? bodySegment
                        : new GeometryHit(false, cameraOrigin + dir * maxRange, Vector3.up, null, maxRange, bodySegment.SelfHitsSkipped);
                    break;
                default:
                    final = cameraCandidate; // 相机候选（含枪口路径同点到达情形）
                    break;
            }

            string missReason = MissNone;
            HitscanResult result;
            if (final.Hit && final.Target != null && final.Target.IsAlive)
            {
                if (IsFriendlyBlocked(ignoreRoot, final.Target))
                {
                    missReason = MissFriendly;
                    result = new HitscanResult(true, false, final.Point, final.Normal, final.Target, final.SelfHitsSkipped);
                }
                else if (!PassesRewindLifeGate(final.Target, rewindContext, out string gateReason))
                {
                    // Phase 2 生命代际/无敌闸：回溯命中按"射击时刻快照"比对——旧生命位姿不得伤害
                    // 已复活的新生命（代际不符），射击时刻处于出生保护的目标不掉血。零伤害、目标保留
                    //（与友军过滤同形状：Hit=true/Damaged=false，证据 missReason 区分）。
                    missReason = gateReason;
                    result = new HitscanResult(true, false, final.Point, final.Normal, final.Target, final.SelfHitsSkipped);
                }
                else
                {
                    final.Target.ApplyDamage(damage, final.Point, dir);
                    result = new HitscanResult(true, true, final.Point, final.Normal, final.Target, final.SelfHitsSkipped);
                }
            }
            else if (final.Hit)
            {
                missReason = final.Target != null ? MissTargetDead : MissNoTarget;
                result = new HitscanResult(true, false, final.Point, final.Normal, null, final.SelfHitsSkipped);
            }
            else
            {
                missReason = MissGeometry;
                result = new HitscanResult(false, false, cameraOrigin + dir * maxRange, Vector3.up, null, cameraCandidate.SelfHitsSkipped);
            }

            LastTwoStageEvidence = new FireEvidence(
                new FireRaySegment("cam", cameraOrigin, dir, maxRange, cameraCandidate),
                new FireRaySegment("mzl", muzzleOrigin, toCandidateDistance > 0.01f ? toCandidate / toCandidateDistance : dir,
                    toCandidateDistance, muzzlePath),
                new FireRaySegment("body", bodyAnchor, bodyToMuzzleDistance > 0.001f ? bodyToMuzzle / bodyToMuzzleDistance : dir,
                    bodyToMuzzleDistance, bodySegment),
                decision, candidatePoint, final, missReason,
                rewindContext.UsedTick, rewindContext.WasClamped);
            LogEvidence(LastTwoStageEvidence);
            return result;
        }

        /// <summary>节流输出一发完整证据（默认只在"未造成伤害"时输出，命中伤害走既有 FireTrace）。</summary>
        private void LogEvidence(in FireEvidence evidence)
        {
            if (!EvidenceLoggingEnabled) return;
            if (string.IsNullOrEmpty(evidence.MissReason)) return; // 造成伤害：不需要三段证据
            if (EvidenceLogIntervalSeconds > 0f && Time.unscaledTime < _nextEvidenceLogTime) return;
            _nextEvidenceLogTime = Time.unscaledTime + EvidenceLogIntervalSeconds;
            Debug.Log(evidence.Format());
        }

        /// <summary>
        /// 几何射线（不含伤害）：遍历全部命中（RaycastNonAlloc），跳过 ignoreRoot 下所有碰撞体与
        /// 显式移动阻挡体，取最近有效命中；无命中时 Point = origin + dir*maxRange（远点），绝不返回自身命中点。
        /// </summary>
        private GeometryHit ResolveGeometry(
            Vector3 origin, Vector3 direction, float maxRange, int layerMask, Transform ignoreRoot)
        {
            Vector3 dir = direction.normalized;
            // 2026-09-10 审计 §3：玩家 BodyHitbox 已改 trigger（受击/阻挡分离），命中查询显式 Collide——
            // Ignore 会让射线直接穿透玩家。已核实全场景（游戏场景/预制体）无其他 trigger 碰撞体，
            // Collide 不会误吸环境触发体；激光指示器走全局 queriesHitTriggers=1 语义不变。
            int count = Physics.RaycastNonAlloc(
                new Ray(origin, dir), _hits, maxRange, layerMask, QueryTriggerInteraction.Collide);
            _hitCount = count;

            int selfSkipped = 0;
            int movementSkipped = 0;
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
                // P2（2026-09-18 审计 §5）：移动阻挡体在【候选选择阶段】整体跳过，而不是"先选中它
                // 再把 Target 设空"。后者仍会让不可见的根 CC 外壳挡枪，并在模型后方的空处生成实体
                // 弹孔。跳过后射线继续向后走：真实命中 BodyHitbox 正常结算，命中墙则落点归墙。
                if (IsMovementBlocker(collider))
                {
                    movementSkipped++;
                    continue;
                }
                if (best < 0 || _hits[i].distance < _hits[best].distance) best = i;
            }
            LastSegmentMovementSkipped = movementSkipped;

            if (best < 0)
            {
#if UNITY_SERVER && !UNITY_EDITOR
                // Day4 Gate B 诊断：服务器权威射线落空时留痕（origin/方向/命中数/自跳/移动体跳过），用于离线复盘
                Debug.Log($"[CombatRay] MISS origin={origin.ToString("F2")} dir={dir.ToString("F3")} hits={count} selfSkipped={selfSkipped} movementSkipped={movementSkipped} mask={layerMask}");
#endif
                return new GeometryHit(false, origin + dir * maxRange, Vector3.up, null, maxRange, selfSkipped);
            }

            var hitInfo = _hits[best];
            var target = hitInfo.collider.GetComponentInParent<DamageableTarget>();
            if (target == null)
            {
                // 同根归属回退（审计 2026-09-15 §5.1）：只在【同一受害者根】的其余命中里找最近的可归属
                // 碰撞体（BodyHitbox），用它的点/法线/距离归因（几何近似共面时给出精确落点）。不同根
                // 绝不归属——需要 root 相同 + 5cm 共面两个条件同时成立，独立墙体永远挡住这条路径。
                target = ResolveAttributionFallback(hitInfo, ignoreRoot, out var fallbackHit);
                if (target != null)
                    return Describe(true, fallbackHit, target, selfSkipped);
#if UNITY_SERVER && !UNITY_EDITOR
                // 诊断绊线：命中了带 NetworkCombatAuthority 的根碰撞体却归属失败
                //（= 该玩家对象子树内没有任何可归属受击体，资产缺口；撞线时按类型/层/命中数排查）
                if (hitInfo.collider.GetComponentInParent<NetworkCombatAuthority>() != null)
                    Debug.Log($"[CombatRay] HIT_NO_TARGET victimRootCollider={hitInfo.collider.name}"
                        + $" colliderType={hitInfo.collider.GetType().Name} layer={LayerMask.LayerToName(hitInfo.collider.gameObject.layer)}"
                        + $" dist={hitInfo.distance:F2} hits={count} movementSkipped={movementSkipped}");
#endif
            }
            return Describe(true, hitInfo, target, selfSkipped);
        }

        /// <summary>最近一次 ResolveGeometry 跳过的移动阻挡体数（测试/诊断接缝：证明 CC 是"被跳过"
        /// 而不是"被选中后置空"——后者仍会挡枪）。单发取最后一段（body 段）的值。</summary>
        internal int LastSegmentMovementSkipped { get; private set; }

        /// <summary>
        /// 该碰撞体是否被显式声明为"只挡移动、不参与受击判定"（玩家根 CharacterController，
        /// 由 <see cref="HitVolumeTag"/> 运行时登记）。没有标记的碰撞体（墙、普通可破坏目标、
        /// 与玩家无关的控制器）判定行为完全不变。
        /// </summary>
        internal static bool IsMovementBlocker(Collider collider)
        {
            if (collider == null) return false;
            var tag = collider.GetComponent<HitVolumeTag>();
            return tag != null && tag.Role == HitVolumeRole.MovementBlocker;
        }

        /// <summary>把一条 RaycastHit 转成带碰撞体归属信息的几何命中（审计 §6.1）。</summary>
        private static GeometryHit Describe(bool hit, in RaycastHit hitInfo, DamageableTarget target, int selfSkipped)
        {
            var collider = hitInfo.collider;
            int ownerObjectId = -1;
            if (collider != null)
            {
                var authority = collider.GetComponentInParent<NetworkCombatAuthority>();
                if (authority != null && authority.NetworkObject != null)
                    ownerObjectId = authority.NetworkObject.ObjectId;
            }
            return new GeometryHit(hit, hitInfo.point, hitInfo.normal, target, hitInfo.distance, selfSkipped,
                collider != null ? collider.name : null,
                collider != null ? collider.gameObject.layer : -1,
                ownerObjectId);
        }

        /// <summary>同根归属回退（审计 §5.1）：最近命中不可归属时，在同一根的其余命中里取最近的
        /// DamageableTarget 命中。找到返回该目标并把命中信息写入 fallbackHit。
        /// 容差保守（5cm）是有意的：本层只负责"两段命中几何共面"时给出**精确落点**，不得靠放大容差
        /// 去救不共面的几何。2026-09-18 §5：玩家根移动阻挡体已在候选选择阶段被排除，因此本层不再
        /// 承担"按对象猜归属"的职责（旧的祖先→子树兜底已删除）。</summary>
        private DamageableTarget ResolveAttributionFallback(
            RaycastHit nearest, Transform ignoreRoot, out RaycastHit fallbackHit)
        {
            fallbackHit = nearest;
            Transform victimRoot = nearest.collider.transform.root;
            DamageableTarget best = null;
            const float CoincidentEpsilon = 0.05f; // 同体积共心胶囊的表面距离差量级（毫米级，取 5cm 上限）
            for (int i = 0; i < _hits.Length && i < _hitCount; i++)
            {
                var collider = _hits[i].collider;
                if (collider == null || collider == nearest.collider) continue;
                if (_hits[i].distance > nearest.distance + CoincidentEpsilon) continue;
                if (collider.transform.root != victimRoot) continue;
                if (ignoreRoot != null && collider.transform.root == ignoreRoot) continue;
                var candidate = collider.GetComponentInParent<DamageableTarget>();
                if (candidate == null) continue;
                if (best == null || _hits[i].distance < fallbackHit.distance)
                {
                    best = candidate;
                    fallbackHit = _hits[i];
                }
            }
            return best;
        }

        /// <summary>
        /// 友伤过滤（TDM）：射手与目标同队非 None → 阻挡但零伤害。
        /// 队伍来自 NetworkCombatAuthority 服务器权威 SyncVar；离线/未联网（无组件或 None 队）恒放行。
        /// </summary>
        private static bool IsFriendlyBlocked(Transform shooterRoot, DamageableTarget target)
        {
            if (shooterRoot == null || target == null) return false;
            var shooter = shooterRoot.GetComponentInParent<NetworkCombatAuthority>();
            if (shooter == null) return false;
            var victim = target.GetComponentInParent<NetworkCombatAuthority>();
            if (victim == null) return false;
            return !MatchRules.IsDamageAllowed(shooter.TeamId, victim.TeamId);
        }

        /// <summary>
        /// Phase 2 生命代际/无敌闸（仅回溯语境生效；本地预测/离线 default 语境直接放行）：
        /// 以"射击对应的历史快照"（ServerLagCompensation.TryGetRewindContext，与回溯取 Entry 同 tick
        /// 同口径）比对目标当前生命代际——不符 = 打的是旧生命位姿（重生瞬移后 0.3s 窗口的迟到射击），
        /// 伤害不得作用于新生命；快照显示无敌 = 射击时刻目标处于出生保护。
        /// 无快照语境（目标未注册/无历史）不拦截——维持既有即时判定语义（快照能力缺失时诚实降级）。
        /// 目标"服务器当前时刻"的无敌兜底另在 DamageableTarget.ApplyDamage 终闸执行。
        /// </summary>
        /// <summary>internal 供 EditMode 直驱（InternalsVisibleTo 惯例；产品路径仅 ResolveHitscanTwoStage 调用）。</summary>
        internal static bool PassesRewindLifeGate(
            DamageableTarget target, LagCompRewindContext rewindContext, out string gateReason)
        {
            gateReason = null;
            if (!rewindContext.Rewound) return true;
            var lagComp = ServerLagCompensation.Instance;
            if (lagComp == null) return true;
            var targetRoot = target.transform.root;
            if (!lagComp.TryGetRewindContext(targetRoot, rewindContext.UsedTick, out ulong snapshotGeneration, out bool snapshotInvincible))
                return true;
            ulong currentGeneration = 0;
            var targetAuthority = targetRoot.GetComponent<NetworkCombatAuthority>();
            if (targetAuthority != null) currentGeneration = targetAuthority.LifeGeneration;
            if (snapshotGeneration != currentGeneration)
            {
                gateReason = MissTargetStale;
                return false;
            }
            if (snapshotInvincible)
            {
                gateReason = MissTargetInvincible;
                return false;
            }
            return true;
        }
    }
}
