using System.Reflection;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 第二轮审计约束锁定（接管契约 §1.1/§1.2/§2，2026-09-07）：
    /// ① 租约代际证据只承认两种来源——(a) 注册 ack 明确 Ready/Offline 且 tracker 旧 roomCode 已清；
    ///    (b) 新票据 consume 的非空 roomCode 与旧局 EndedRoomCode 不同。Reserved/InMatch 注册 ack
    ///    不携带 roomCode、无法证明旧房释放，【不得】单独置位（约束 1）。
    /// ② 服务器终局记录旧局房间码快照并作废既有证据（每次 Ended 要求全新代际证明）。
    /// ③ OnServerMatchInProgress 清位 + TryEnterMatch 仅在本地持有非空权威 roomCode 时执行（§1.1）。
    /// ④ Bootstrap 静态事件接线对称解绑 + 单次接线闸（约束 2）——EditMode 直证订阅计数。
    /// </summary>
    public sealed class DedicatedServerLifecycleTests
    {
        private ServerHeartbeatTracker _tracker;

        [SetUp]
        public void SetUp()
        {
            DedicatedServerLifecycle.ResetForTests();
            _tracker = new ServerHeartbeatTracker();
        }

        private static int CountSubscribers(string eventName)
        {
            var field = typeof(MatchLifecycle).GetField(eventName, BindingFlags.NonPublic | BindingFlags.Static);
            var dlg = field?.GetValue(null) as System.MulticastDelegate;
            return dlg == null ? 0 : dlg.GetInvocationList().Length;
        }

        // ---- 证据 (a)：注册 ack Ready/Offline + tracker 旧绑定已清 ----

        [Test]
        public void RegisterAck_ReadyWithTrackerUnbound_MarksLeaseGeneration()
        {
            DedicatedServerLifecycle.OnRegisterAck("Ready", trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True,
                "注册 ack Ready + 旧 roomCode 已清 = 权威释放证据（接管契约 §1.2 (a)）");
        }

        [Test]
        public void RegisterAck_OfflineWithTrackerUnbound_MarksLeaseGeneration()
        {
            DedicatedServerLifecycle.OnRegisterAck("Offline", trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True);
        }

        [Test]
        public void RegisterAck_ReservedOrInMatch_NeverMarks_Alone()
        {
            // 约束 1 主案：Reserved/InMatch 注册 ack 不携带 roomCode，无法证明旧房已释放——不得置位
            DedicatedServerLifecycle.OnRegisterAck("Reserved", trackerUnbound: true);
            DedicatedServerLifecycle.OnRegisterAck("InMatch", trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False,
                "单独 Reserved/InMatch ack 不得等价于 BackendReadyForNextMatch=true");
        }

        [Test]
        public void RegisterAck_ReadyButTrackerStillBound_DoesNotMark()
        {
            // tracker 旧 roomCode 未清除 → 不满足 (a) 的"tracker 已清除旧 roomCode"要件
            DedicatedServerLifecycle.OnRegisterAck("Ready", trackerUnbound: false);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False);
        }

        // ---- 证据 (b)：新票据不同 roomCode ----

        [Test]
        public void TicketRoom_DifferentFromEndedRoom_MarksNewLeaseGeneration()
        {
            DedicatedServerLifecycle.OnServerMatchEnded("OLDROOM");
            DedicatedServerLifecycle.OnTicketAcceptedRoom("NEWROO");
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True,
                "新票据 consume 的非空 roomCode ≠ 旧局 endedRoomCode = 新租约代际证据（接管契约 §1.2 (b)）");
        }

        [Test]
        public void TicketRoom_SameAsEndedRoom_DoesNotMark()
        {
            DedicatedServerLifecycle.OnServerMatchEnded("OLDROOM");
            DedicatedServerLifecycle.OnTicketAcceptedRoom("OLDROOM");
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False,
                "同房间码不构成新租约代际");
        }

        [Test]
        public void TicketRoom_WithoutEndedMatch_IsNoOp()
        {
            // 无已终局比赛（首局流转）：代际证据不适用（重臂门另有 Phase==Ended 前置）
            DedicatedServerLifecycle.OnTicketAcceptedRoom("NEWROO");
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False);
            DedicatedServerLifecycle.OnTicketAcceptedRoom(string.Empty);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False);
        }

        // ---- ② 服务器终局：记录快照 + 作废旧证据 ----

        [Test]
        public void ServerMatchEnded_RecordsRoomSnapshot_AndInvalidatesPriorReady()
        {
            // 先取得 (a) 证据
            DedicatedServerLifecycle.OnRegisterAck("Ready", trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True);

            DedicatedServerLifecycle.OnServerMatchEnded("OLDROOM");

            Assert.That(DedicatedServerLifecycle.EndedRoomCode, Is.EqualTo("OLDROOM"), "终局记录旧局房间码快照");
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False,
                "每次 Ended 作废既有证据——重臂必须持有全新租约代际证明");
        }

        // ---- ③ OnServerMatchInProgress：清位 + TryEnterMatch 门槛（§1.1） ----

        [Test]
        public void MatchInProgress_WithBoundRoom_EnterInMatch_AndClearsReadyFlag()
        {
            // 先取得证据，再开局——InProgress 必须清位（新局开始，旧同步事实失效）
            DedicatedServerLifecycle.OnRegisterAck("Ready", trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True);
            _tracker.OnRoomBound("ROOMAB");

            DedicatedServerLifecycle.OnServerMatchInProgress(_tracker);

            Assert.That(_tracker.InMatch, Is.True, "已绑定非空权威 roomCode → TryEnterMatch 生效");
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False,
                "新局开始必须清位——旧 Ready 事实失效");
        }

        [Test]
        public void MatchInProgress_WithoutBoundRoom_SkipsTryEnterMatch()
        {
            // §1.1：本地未持有非空权威 roomCode → 不执行 TryEnterMatch（离线/F1 调试/未租房）
            DedicatedServerLifecycle.OnServerMatchInProgress(_tracker);
            Assert.That(_tracker.InMatch, Is.False, "未绑定房间不得进入 InMatch");
        }

        // ---- ④ Bootstrap 静态事件接线：单次接线闸 + 对称解绑（约束 2） ----

        [Test]
        public void Bootstrap_HooksWiredOnce_AndSymmetricallyUnbound()
        {
            var go = new GameObject("BootstrapUnbindProbe");
            try
            {
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                var wire = typeof(DedicatedServerBootstrap).GetMethod("WireMatchLifecycleHooks",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var unwire = typeof(DedicatedServerBootstrap).GetMethod("UnwireMatchLifecycleHooks",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                wire.Invoke(bootstrap, null);
                wire.Invoke(bootstrap, null); // 重复接线必须被单次闸拦截

                Assert.That(CountSubscribers("OnServerMatchInProgress"), Is.EqualTo(1),
                    "OnServerMatchInProgress 至多一个订阅者（约束 2：避免重复初始化多次触发 TryEnterMatch）");
                Assert.That(CountSubscribers("OnServerMatchEnded"), Is.EqualTo(1),
                    "OnServerMatchEnded 同样单次接线");

                // ---- 对称解绑直证（约束 2）：Unwire 与 Wire 严格配对，解绑后零残留、重复解绑幂等。
                // （EditMode 下 DestroyImmediate 不回调 OnDestroy——探针日志实证；运行时由 OnDestroy 兜底）----
                unwire.Invoke(bootstrap, null);
                Assert.That(CountSubscribers("OnServerMatchInProgress"), Is.EqualTo(0),
                    "对称解绑后 OnServerMatchInProgress 零残留订阅");
                Assert.That(CountSubscribers("OnServerMatchEnded"), Is.EqualTo(0),
                    "OnServerMatchEnded 同样零残留");

                unwire.Invoke(bootstrap, null); // 幂等：重复解绑无副作用
                Assert.That(CountSubscribers("OnServerMatchInProgress"), Is.EqualTo(0));

                Object.DestroyImmediate(go); // 销毁探针：静态事件零复活（已解绑组件不得重新占用）
                Assert.That(CountSubscribers("OnServerMatchInProgress"), Is.EqualTo(0),
                    "销毁后零复活");
                Assert.That(CountSubscribers("OnServerMatchEnded"), Is.EqualTo(0));
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }
    }
}
