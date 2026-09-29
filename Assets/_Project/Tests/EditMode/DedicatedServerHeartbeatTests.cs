using System.Threading.Tasks;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 心跳契约测试（Codex 审计第 2/4 项 + 第三轮 P1 修复锁定 2026-09-06）：
    /// 后端房间绑定与实例状态是权威真相——本地只上报已观察事实：
    /// ① 未注册未绑定：state=Starting（不自造 Ready）；
    /// ② 未绑定注册同步：回传后端告知状态；Reserved/InMatch 同步时无本地绑定 → 上报对应状态+空 roomCode（等票据补齐）；
    /// ③ 有效票据绑定 → Reserved + 该已认证 roomCode；
    /// ④ 【第三轮】OnRegistered("Ready"/"Offline") = 后端权威确认实例已释放 → 清除本地房间绑定与 InMatch，
    ///    后续心跳为 Ready/Offline + 空 roomCode（修复：旧 roomCode 无法清除导致 409 循环、实例无法复用）；
    /// ⑤ 【第三轮】有效 consume 的非空 roomCode 允许替换旧绑定（释放后重租 Room B），换房清除旧 InMatch 回 Reserved；
    ///    相同码幂等；空白码不创建也不清除绑定；
    /// ⑥ 409：重注册同步，本地绝不自恢复 Ready。
    /// </summary>
    public sealed class DedicatedServerHeartbeatTests
    {
        [Test]
        public void StoppedTransport_SuppressesReadyEvenWhenProcessIsAlive()
        {
            var go = new UnityEngine.GameObject("StoppedTransportTest");
            try
            {
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(DedicatedServerBootstrap).GetField("_transportUnavailable", flags).SetValue(bootstrap, true);
                var args = new object[] { null };
                var available = (bool)typeof(DedicatedServerBootstrap).GetMethod("TryBuildHeartbeat", flags).Invoke(bootstrap, args);
                Assert.IsFalse(available);
                Assert.IsNull(args[0]);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        // ---- 基础：Starting / 注册同步 / 绑定 ----

        [Test]
        public void UnboundAndUnregistered_ReportsStarting_NotReady()
        {
            var tracker = new ServerHeartbeatTracker();

            var request = tracker.BuildHeartbeat(currentPlayers: 0);

            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateStarting),
                "未获知任何后端事实前不得自造 Ready——Starting 是唯一安全值");
            Assert.That(request.roomCode, Is.Empty);
            Assert.That(request.currentPlayers, Is.EqualTo(0));
        }

        [Test]
        public void RegisteredUnbound_EchoesBackendSyncedState()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRegistered("Ready");

            Assert.That(tracker.BuildHeartbeat(1).state, Is.EqualTo("Ready"),
                "注册同步：后端注册时告知 Ready，未绑定前如实回传该同步值");

            var tracker2 = new ServerHeartbeatTracker();
            tracker2.OnRegistered("Reserved");
            Assert.That(tracker2.BuildHeartbeat(0).state, Is.EqualTo("Reserved"),
                "后端若在注册时即 Reserved，未绑定前回传 Reserved（绝不改成 Ready）");
            Assert.That(tracker2.BuildHeartbeat(0).roomCode, Is.Empty,
                "Reserved/InMatch 同步不凭空猜 roomCode——无本地绑定时上报状态+空码，等有效票据补齐");
        }

        [Test]
        public void FirstTicket_BindsRoom_ReportsReservedWithAuthenticatedRoomCode()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRegistered("Ready");           // 注册时后端说 Ready
            tracker.OnRoomBound("ROOM-7788");       // 首张有效票据

            var request = tracker.BuildHeartbeat(currentPlayers: 1);

            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateReserved),
                "已服务房间 → 至少 Reserved：绝不继续回传注册时的 Ready（防止后端把已在用实例再次租出）");
            Assert.That(request.roomCode, Is.EqualTo("ROOM-7788"),
                "只上报票据 consume 认证的 roomCode（后端房间绑定为权威真相）");
        }

        // ---- 【第三轮审计 1】Ready = 后端权威确认释放：清除房间与 InMatch ----

        [Test]
        public void BackendReady_AfterRoomBound_ClearsBindingAndInMatch()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMA");
            tracker.TryEnterMatch();
            Assert.That(tracker.IsRoomBound, Is.True);   // 前置确认

            tracker.OnRegistered("Ready");               // 后端释放实例

            Assert.That(tracker.BoundRoomCode, Is.Empty, "Ready=后端权威确认未绑定：旧 roomCode 必须清除");
            Assert.That(tracker.InMatch, Is.False, "释放时 InMatch 一并清除");
            var request = tracker.BuildHeartbeat(0);
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateReady));
            Assert.That(request.roomCode, Is.Empty, "释放后心跳为 Ready + 空 roomCode（不再上报旧房间）");
        }

        // ---- 【第三轮审计 2】Offline 同样清除；心跳按后端状态上报 ----

        [Test]
        public void BackendOffline_ClearsRoomAndInMatch_HeartbeatReportsOffline()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMA");
            Assert.That(tracker.TryEnterMatch(), Is.True);

            tracker.OnRegistered("Offline");

            Assert.That(tracker.BoundRoomCode, Is.Empty, "Offline=后端权威确认未绑定：房间清除");
            Assert.That(tracker.InMatch, Is.False, "Offline 时 InMatch 清除");
            var request = tracker.BuildHeartbeat(0);
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateOffline));
            Assert.That(request.roomCode, Is.Empty);
        }

        // ---- 【第三轮审计 3】释放→重租全链：RoomA InMatch → Ready 释放 → RoomB 重新绑定 ----

        [Test]
        public void ReleasedThenRebound_ReportsReservedRoomB_WithoutStaleRoomAOrInMatch()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMA");
            tracker.TryEnterMatch();

            tracker.OnRegistered("Ready");               // 后端释放：房间/InMatch 清除
            var releasedHeartbeat = tracker.BuildHeartbeat(0);
            Assert.That(releasedHeartbeat.state, Is.EqualTo(ServerHeartbeatTracker.StateReady));
            Assert.That(releasedHeartbeat.roomCode, Is.Empty);

            tracker.OnRoomBound("ROOMB");               // 实例重租给 Room B（有效 consume）

            var request = tracker.BuildHeartbeat(1);
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateReserved),
                "换房后默认回到 Reserved——旧房的 InMatch 不得残留");
            Assert.That(request.roomCode, Is.EqualTo("ROOMB"),
                "心跳必须上报新房间 ROOMB，不得残留 ROOMA");
            Assert.That(tracker.InMatch, Is.False, "切换房间时清除旧房 InMatch 状态");
        }

        // ---- 【第三轮审计 4】权威 consume 直接替换本地残留旧绑定 ----

        [Test]
        public void AuthoritativeConsume_ReplacesStaleRoomBinding()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMA");
            // 本地仍残留 ROOMA（如错过 Ready 同步）时，Room B 的有效 consume 是后端权威事实
            tracker.OnRoomBound("ROOMB");

            Assert.That(tracker.BoundRoomCode, Is.EqualTo("ROOMB"),
                "有效 consume 结果必须替换本地残留旧绑定（否则实例无法服务新房间）");
            var request = tracker.BuildHeartbeat(1);
            Assert.That(request.roomCode, Is.EqualTo("ROOMB"));
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateReserved));
        }

        // ---- 【第三轮审计 5】相同 roomCode 重复通知幂等 ----

        [Test]
        public void SameRoomCode_RepeatedNotification_IsIdempotent()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOM-7788");
            tracker.TryEnterMatch();
            bool firstEnter = tracker.InMatch;

            tracker.OnRoomBound("ROOM-7788");           // 同房间第二张票据
            tracker.OnRoomBound("ROOM-7788");           // 第三张

            Assert.That(tracker.BoundRoomCode, Is.EqualTo("ROOM-7788"));
            Assert.That(tracker.InMatch, Is.EqualTo(firstEnter),
                "相同 roomCode 重复通知不产生副作用（InMatch 不被同房重复通知清除）");
            Assert.That(tracker.BuildHeartbeat(2).roomCode, Is.EqualTo("ROOM-7788"));
        }

        // ---- 【第三轮审计 6】空白 roomCode 不创建也不清除绑定 ----

        [Test]
        public void BlankRoomCode_NeitherCreatesNorClearsBinding()
        {
            var tracker = new ServerHeartbeatTracker();

            tracker.OnRoomBound("");                     // 未绑定时空白码：不创建
            Assert.That(tracker.IsRoomBound, Is.False, "空白 roomCode 不得创建绑定");

            tracker.OnRoomBound("ROOMA");
            tracker.OnRoomBound("   ");                   // 绑定后空白码：不清除
            tracker.OnRoomBound(null);
            Assert.That(tracker.BoundRoomCode, Is.EqualTo("ROOMA"),
                "空白 roomCode 不得清除已有绑定（只有后端 Ready/Offline 同步能清除）");
        }

        // ---- Day2 InMatch 入口 ----

        [Test]
        public void TryEnterMatch_RequiresBoundRoom_ThenReportsInMatch()
        {
            var tracker = new ServerHeartbeatTracker();
            Assert.That(tracker.TryEnterMatch(), Is.False,
                "未绑定房间不得进入 InMatch（结构性防误用：无房间即无对局）");

            tracker.OnRoomBound("ROOM-1");
            Assert.That(tracker.TryEnterMatch(), Is.True);
            Assert.That(tracker.BuildHeartbeat(2).state, Is.EqualTo(ServerHeartbeatTracker.StateInMatch),
                "Day2 进入比赛后心跳上报 InMatch（预留入口，Day1 由 DedicatedServerBootstrap 之外的生命周期接线）");
        }

        // ---- 409 状态矛盾：重注册同步，绝不自恢复 Ready ----

        [Test]
        public void HeartbeatConflict_NeverLocallyRestoresReady()
        {
            // 服务器被后端 409 判定状态矛盾。正确处置 = 保留连接 + 重注册同步；
            // Reserved/InMatch 同步保留本地绑定（不猜房间），Ready/Offline 同步权威清除。
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOM-7788");

            tracker.OnRegistered("Reserved");   // 重注册返回 Reserved：绑定保留
            Assert.That(tracker.BuildHeartbeat(0).roomCode, Is.EqualTo("ROOM-7788"));
            Assert.That(tracker.BuildHeartbeat(0).state, Is.EqualTo(ServerHeartbeatTracker.StateReserved),
                "409 后重注册同步 Reserved：已绑定房间事实保留——唯一能恢复 Ready 的是后端权威决策，不是本地");

            tracker.OnRegistered("");           // 空状态同步：不清除绑定
            Assert.That(tracker.BoundRoomCode, Is.EqualTo("ROOM-7788"),
                "空状态同步不产生清除/创建副作用");
        }

        // ---- 控制面映射护栏 ----

        [Test]
        public void ControlPlaneHeartbeatOutcome_MapsFromHttpExceptionResponseCode()
        {
            // HeartbeatOutcome 的来源映射（HTTP 409 → StateConflict）由 UnityWebServerControlPlaneClient
            // 内部完成；本用例锁定异常携带的状态码语义（409 = 矛盾，其余 = 传输失败）。
            var conflict = new ControlPlaneRequestException("/api/server-instances/arena-01/heartbeat", 409, "conflict");
            var transport = new ControlPlaneRequestException("/api/server-instances/arena-01/heartbeat", 503, "unavailable");

            Assert.That(conflict.ResponseCode, Is.EqualTo(409));
            Assert.That(transport.ResponseCode, Is.EqualTo(503));
            Assert.That(conflict.Message, Does.Not.Contain("serverKey"), "异常文本不得泄露密钥");
        }

        [Test]
        public void FakeControlPlaneHeartbeat_SignatureReturnsOutcome()
        {
            // 假控制面按新契约返回 HeartbeatOutcome（供 Bootstrap 循环消费；接口签名回归护栏）
            IServerControlPlaneClient controlPlane = new FixedOutcomeControlPlane();
            var outcome = controlPlane.HeartbeatAsync(new ServerInstanceHeartbeatRequest()).GetAwaiter().GetResult();
            Assert.That(outcome, Is.EqualTo(HeartbeatOutcome.Accepted));
        }

        private sealed class FixedOutcomeControlPlane : IServerControlPlaneClient
        {
            public Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request)
                => Task.FromResult(new ServerInstanceRegisterResponse());

            public Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request)
                => Task.FromResult(HeartbeatOutcome.Accepted);

            public Task<TicketConsumeResult> ConsumeTicketAsync(string ticket)
                => Task.FromResult(TicketConsumeResult.Rejected("TICKET_INVALID"));

            public Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request)
                => Task.FromResult(PlayerDisconnectReport.AcceptedFromBackend(null));
        }
    }
}
