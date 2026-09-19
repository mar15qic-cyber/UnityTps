using System.Reflection;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 生命周期闭环锁定（任务 A，2026-09-07）：
    /// ① MatchRearmGate 五条件矩阵（Phase==Ended/旧局客户端全走/认证档案空/延迟移除收口/后端权威已同步）；
    /// ② TryRearmForNextMatch 正负路径（pendingDeparture 未收口、后端未同步 → 拒绝；全备 → 复位）；
    /// ③ 重臂复位完备性（用户规则 ④逐项：Phase/InputFrozen/ClientMatchId/_endedBroadcast/
    ///    _leaveGuard/_hitRegistry/_pendingDeparture/中继宿主/比赛计时）；
    /// ④ "同一 Dedicated Server 进程连续两局"模拟：倒计时#1 → 播种再 Ended → 重臂 →
    ///    倒计时#2 生成新 matchId、离开闸全新、_endedBroadcast 复位（无重复终局标记）。
    /// 静态态播种/断言经反射（项目既有惯例：LobbyPresenter 私有渲染方法直调同款）；
    /// "eligible>0 / AcceptedUsers>0 拒绝重臂"分支在 EditMode 无法伪造有资格玩家（需运行中
    /// 服务器），其判定语义由 ①矩阵锁定、真实行为由 IT-12 实进程取证（B/C 在局中不得复位）。
    /// </summary>
    public sealed class MatchLifecycleRearmTests
    {
        private GameObject _lifecycleHost;
        private MatchLifecycle _lifecycle;

        [SetUp]
        public void SetUp()
        {
            DedicatedServerLifecycle.ResetForTests();
            ResetAllStatics();
            _lifecycleHost = new GameObject("MatchLifecycleRearmHost");
            _lifecycle = _lifecycleHost.AddComponent<MatchLifecycle>();
        }

        [TearDown]
        public void TearDown()
        {
            ResetAllStatics();
            DedicatedServerLifecycle.ResetForTests();
            if (_lifecycleHost != null) Object.DestroyImmediate(_lifecycleHost);
        }

        // ---- 反射播种/读取工具 ----

        private static void SetPhase(MatchPhase phase)
            => typeof(MatchLifecycle).GetProperty("Phase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, phase);

        private static void SetInputFrozen(bool value)
            => typeof(MatchLifecycle).GetProperty("InputFrozen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, value);

        private static void SetClientMatchId(string value)
            => typeof(MatchLifecycle).GetProperty("ClientMatchId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, value);

        private static T GetStaticField<T>(string name)
            => (T)typeof(MatchLifecycle).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static void SetStaticField(string name, object value)
            => typeof(MatchLifecycle).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

        /// <summary>全量复位（重臂核心 PerformRearmReset 直调——Phase 未在 Ended 时 TryRearm 不执行复位）。</summary>
        private static void ResetAllStatics()
            => typeof(MatchLifecycle).GetMethod("PerformRearmReset", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

        /// <summary>播种完整 Ended 态（含用户规则 ④清单里的全部可播种残余）。</summary>
        private static void SeedEndedState(GameObject killerGo, GameObject targetGo)
        {
            SetPhase(MatchPhase.Ended);
            SetInputFrozen(true);
            SetClientMatchId("match-old-0001");
            SetStaticField("_endedBroadcast", true);
            SetStaticField("_matchStartRealtime", 77.5f);
            SetStaticField("_pendingDeparture", killerGo.AddComponent<NetworkCombatAuthority>());
            SetStaticField("_relayHostStatic", targetGo.AddComponent<NetworkCombatAuthority>());
            // 命中归因登记 + 离开闸占用（真实链路 API，无需反射）
            var shooter = targetGo.AddComponent<NetworkCombatAuthority>();
            var hitTarget = killerGo.AddComponent<Game.Gameplay.Health.DamageableTarget>();
            MatchLifecycle.RegisterHit(shooter, hitTarget);
            GetStaticField<LeaveOnceGuard>("_leaveGuard").TryBegin(7);
        }

        /// <summary>倒计时（真实 ServerStartCountdown 直调：生成新 matchId + Phase=Countdown）。</summary>
        private string InvokeServerStartCountdown()
        {
            typeof(MatchLifecycle).GetMethod("ServerStartCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_lifecycle, null);
            return MatchLifecycle.ClientMatchId;
        }

        /// <summary>登记表 key 的取出工具（DamageableTarget 为 sealed，直接用本体）。</summary>
        private static Game.Gameplay.Health.DamageableTarget GetStaticFieldStubHitTarget(GameObject go)
            => go.GetComponent<Game.Gameplay.Health.DamageableTarget>()!;

        // ---- ① 五条件矩阵 ----

        [Test]
        public void RearmGate_AllFiveConditionsRequired()
        {
            Assert.That(MatchRearmGate.Evaluate(true, true, true, true, true), Is.True);

            Assert.That(MatchRearmGate.Evaluate(false, true, true, true, true), Is.False, "未在 Ended 不得复位");
            Assert.That(MatchRearmGate.Evaluate(true, false, true, true, true), Is.False, "旧局客户端未全走不得复位");
            Assert.That(MatchRearmGate.Evaluate(true, true, false, true, true), Is.False, "认证档案非空不得复位");
            Assert.That(MatchRearmGate.Evaluate(true, true, true, false, true), Is.False, "延迟移除未收口不得复位");
            Assert.That(MatchRearmGate.Evaluate(true, true, true, true, false), Is.False, "后端权威未重新注册同步不得复位");
        }

        // ---- ② 正负路径 ----

        [Test]
        public void TryRearm_PendingDepartureOpen_IsRejected()
        {
            var killerGo = new GameObject("RearmStub_KillerA");
            var targetGo = new GameObject("RearmStub_TargetA");
            try
            {
                SeedEndedState(killerGo, targetGo);
                // 2 人终局延迟移除窗口未收口（_pendingDeparture 非 null）→ 全部其余条件满足也拒绝
                Assert.That(MatchLifecycle.TryRearmForNextMatch(backendReadySynced: true), Is.False);
                Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Ended), "被拒绝时状态机原样不动");
                Assert.That(GetStaticField<bool>("_endedBroadcast"), Is.True, "终局广播标记不得被清除");
            }
            finally
            {
                Object.DestroyImmediate(killerGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        [Test]
        public void TryRearm_BackendNotResynced_IsRejected()
        {
            var killerGo = new GameObject("RearmStub_KillerB");
            var targetGo = new GameObject("RearmStub_TargetB");
            try
            {
                SeedEndedState(killerGo, targetGo);
                SetStaticField("_pendingDeparture", null);
                // 后端权威状态未重新注册同步（无注册 ack since 上局开始）→ 拒绝（用户规则 ③）
                Assert.That(MatchLifecycle.TryRearmForNextMatch(backendReadySynced: false), Is.False);
                Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Ended));
            }
            finally
            {
                Object.DestroyImmediate(killerGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        // ---- ③④ 复位完备性 + 同一进程连续两局 ----

        [Test]
        public void Rearm_ClearsEveryResidual_AndAllowsTwoConsecutiveMatches()
        {
            var killerGo = new GameObject("RearmStub_KillerC");
            var targetGo = new GameObject("RearmStub_TargetC");
            try
            {
                SeedEndedState(killerGo, targetGo);
                SetStaticField("_pendingDeparture", null);

                // ---- 第一局：Ended → 重臂 → 倒计时#1 → 新 matchId ----
                Assert.That(MatchLifecycle.TryRearmForNextMatch(backendReadySynced: true), Is.True,
                    "五条件齐备必须复位（MATCH_REARMED）");
                Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Idle));
                Assert.That(MatchLifecycle.InputFrozen, Is.False);
                Assert.That(MatchLifecycle.ClientMatchId, Is.Null);
                Assert.That(GetStaticField<bool>("_endedBroadcast"), Is.False, "终局广播标记复位——下一局可正常再广播 Ended");
                Assert.That(GetStaticField<LeaveOnceGuard>("_leaveGuard").ProcessedCount, Is.Zero, "离开闸清空——无旧局闸位");
                Assert.That(GetStaticField<LeaveOnceGuard>("_leaveGuard").TryBegin(7), Is.True, "旧 clientId 重新可用（重臂后无残留占用）");
                Assert.That(MatchLifecycle.ConsumeKillerOf(GetStaticFieldStubHitTarget(killerGo)), Is.Null, "命中归因登记表清空");
                Assert.That(GetStaticField<object>("_pendingDeparture"), Is.Null);
                Assert.That(GetStaticField<object>("_relayHostStatic"), Is.Null, "中继宿主缓存清空——下一局重新解析");
                Assert.That(GetStaticField<float>("_matchStartRealtime"), Is.EqualTo(0f).Within(0.0001f), "比赛计时复位");

                string matchId1 = InvokeServerStartCountdown();
                Assert.That(matchId1, Does.StartWith("match-"), "倒计时#1 生成新 matchId");
                Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Countdown));

                // ---- 播种第一局终局（正常终局语义的静态态）----
                SetPhase(MatchPhase.Ended);
                SetStaticField("_endedBroadcast", true);

                // ---- 第二局：重臂 → 倒计时#2 → 新 matchId ≠ 旧、无重复终局标记 ----
                Assert.That(MatchLifecycle.TryRearmForNextMatch(backendReadySynced: true), Is.True,
                    "同一进程第二局：Ended → Idle 重臂必须可重复发生");
                Assert.That(GetStaticField<bool>("_endedBroadcast"), Is.False, "第二局不存在旧局的 Ended 标记（不会出现重复终局广播）");

                string matchId2 = InvokeServerStartCountdown();
                // DateTime 秒粒度 + 4 位随机：同秒内重摇直至不等（碰撞概率 1/10000，重试上限 4 次）
                int attempts = 0;
                while (matchId2 == matchId1 && attempts < 4)
                {
                    matchId2 = InvokeServerStartCountdown();
                    attempts++;
                }
                Assert.That(matchId2, Does.StartWith("match-"));
                Assert.That(matchId2, Is.Not.EqualTo(matchId1), "第二局必须生成全新 matchId");
                Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Countdown), "第二局能重新进入倒计时");
            }
            finally
            {
                Object.DestroyImmediate(killerGo);
                Object.DestroyImmediate(targetGo);
            }
        }

        [Test]
        public void TryRearm_NotInEndedPhase_IsNoOp()
        {
            // 重臂只属于 Ended 态：Idle/Countdown/InProgress 一律不动（防误复位进行中的比赛）
            SetPhase(MatchPhase.InProgress);
            SetClientMatchId("match-live-0001");
            Assert.That(MatchLifecycle.TryRearmForNextMatch(backendReadySynced: true), Is.False);
            Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.InProgress), "进行中的比赛绝不被重臂干扰");
            Assert.That(MatchLifecycle.ClientMatchId, Is.EqualTo("match-live-0001"));
        }

        [Test]
        public void NewLeaseTicketEvidence_BeforeAcceptedProfile_AllowsImmediateRearm()
        {
            // 释放→重注册窗口内新房先抢租：Reserved ack 本身不是证据；首个新房票据才提供
            // roomCode 代际证据。TicketAccepted 必须在当前连接写入 AcceptedUsers/生成玩家前触发，
            // 让旧局连接均已清空的此刻能够立即重臂，而不是浪费整间新房。
            SetPhase(MatchPhase.Ended);
            SetStaticField("_endedBroadcast", true);
            SetStaticField("_pendingDeparture", null);
            DedicatedServerLifecycle.OnServerMatchEnded("OLDROOM");
            DedicatedServerLifecycle.OnTicketAcceptedRoom("NEWROOM");

            Assert.That(MatchLifecycle.TryRearmForNextMatch(
                DedicatedServerLifecycle.BackendReadyForNextMatch), Is.True);
            Assert.That(MatchLifecycle.Phase, Is.EqualTo(MatchPhase.Idle));
            Assert.That(MatchLifecycle.ClientMatchId, Is.Null);
        }
    }
}
