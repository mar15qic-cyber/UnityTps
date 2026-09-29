using System.IO;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-13 DS 终局上报持久补偿（R2/R3 已知限制收口）：MatchResultPendingStore 纯逻辑回归——
    /// JSON 落盘往返保真（重启恢复的前提）、同 matchId 幂等、winnerTeam 空白归一（A01 双保险，
    /// 保证重放与首次上报同内容）、终态移除后文件同步。
    /// </summary>
    public sealed class MatchResultPendingStoreTests
    {
        private sealed class RecoveringControlPlane : IServerControlPlaneClient, IServerMatchResultReporter
        {
            public int Reports;
            public System.Threading.Tasks.Task<MatchResultReportOutcome> ReportMatchResultAsync(ServerMatchResultReportRequest request)
                => System.Threading.Tasks.Task.FromResult(++Reports == 1 ? MatchResultReportOutcome.RewardsPending : MatchResultReportOutcome.Accepted);
            public System.Threading.Tasks.Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request)
                => System.Threading.Tasks.Task.FromResult(Reports < 2 ? HeartbeatOutcome.Accepted : HeartbeatOutcome.StateConflict);
            public System.Threading.Tasks.Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request)
                => throw new System.Exception("Must recover without re-registering");
            public System.Threading.Tasks.Task<TicketConsumeResult> ConsumeTicketAsync(string ticket) => throw new System.NotSupportedException();
            public System.Threading.Tasks.Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request) => throw new System.NotSupportedException();
        }

        [Test]
        public async System.Threading.Tasks.Task Heartbeats_RetryRewardsPendingUntilAccepted_WithoutRegistration()
        {
            var go = new GameObject("ResultHeartbeatTest");
            try
            {
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var store = new MatchResultPendingStore(Path.Combine(_directory, "retry.json"));
                store.Append(Request("retry-rewards"));
                var control = new RecoveringControlPlane();
                typeof(DedicatedServerBootstrap).GetField("_pendingMatchResults", flags).SetValue(bootstrap, store);
                typeof(DedicatedServerBootstrap).GetField("_controlPlane", flags).SetValue(bootstrap, control);
                typeof(DedicatedServerBootstrap).GetField("_heartbeatIntervalSeconds", flags).SetValue(bootstrap, .01f);
                // A fake binding is only needed for its connected-player fact.
                typeof(DedicatedServerBootstrap).GetField("_manager", flags).SetValue(bootstrap, new EmptyBinding());
                typeof(DedicatedServerBootstrap).GetField("_options", flags).SetValue(bootstrap, new DedicatedServerOptions());
                await (System.Threading.Tasks.Task)typeof(DedicatedServerBootstrap).GetMethod("HeartbeatUntilConflictAsync", flags).Invoke(bootstrap, null);
                Assert.AreEqual(2, control.Reports);
                Assert.AreEqual(0, store.Count);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private sealed class EmptyBinding : INetworkManagerBinding
        {
            public event System.Action<FishNet.Transporting.ServerConnectionStateArgs> ServerConnectionState { add { } remove { } }
            public bool IsClientConnectionActive => false;
            public int ConnectedClientCount => 0;
            public bool TrySetTransportPort(ushort port, out string name) { name = "fake"; return true; }
            public bool TrySetRemoteClientTimeout(double seconds, out string name) { name = "fake"; return true; }
            public JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient control, bool debug) => null;
            public bool StartServerConnection() => true;
            public bool StopServerConnection() => true;
        }
        private sealed class PausedReporter : IServerMatchResultReporter
        {
            public readonly System.Threading.Tasks.TaskCompletionSource<MatchResultReportOutcome> Completion = new();
            public System.Threading.Tasks.Task<MatchResultReportOutcome> ReportMatchResultAsync(ServerMatchResultReportRequest request)
                => Completion.Task;
        }

        [Test]
        public async System.Threading.Tasks.Task Delivery_PersistsBeforeNetworkCompletes_AndRemovesOnlyAfterAck()
        {
            var go = new GameObject("PendingDeliveryTest");
            try
            {
                var path = Path.Combine(_directory, "before-network.json");
                var store = new MatchResultPendingStore(path);
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(DedicatedServerBootstrap).GetField("_pendingMatchResults", flags).SetValue(bootstrap, store);
                var reporter = new PausedReporter();
                var delivery = (System.Threading.Tasks.Task)typeof(DedicatedServerBootstrap).GetMethod("DeliverMatchResultAsync", flags)
                    .Invoke(bootstrap, new object[] { reporter, Request("awaiting-ack") });
                Assert.IsFalse(delivery.IsCompleted);
                Assert.AreEqual(1, new MatchResultPendingStore(path).Count, "A crash now must preserve the result");
                reporter.Completion.SetResult(MatchResultReportOutcome.Accepted);
                await delivery;
                Assert.AreEqual(0, new MatchResultPendingStore(path).Count);
            }
            finally { Object.DestroyImmediate(go); }
        }
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Application.temporaryCachePath, "pending-store-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private static ServerMatchResultReportRequest Request(string matchId, string winnerTeam = null)
        {
            return new ServerMatchResultReportRequest
            {
                matchId = matchId,
                durationSeconds = 300,
                winnerTeam = winnerTeam,
                players = new[]
                {
                    new ServerMatchResultPlayerRow
                    {
                        userId = 42L, teamId = "Red", kills = 10, deaths = 2, assists = 3,
                        participationSeconds = 300, rewardEligible = true, leftAtSeconds = 0, isWin = true,
                    },
                },
            };
        }

        [Test]
        public void Append_ReloadAcrossRestart_RestoresRequestFields()
        {
            var store = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json"));
            store.Append(Request("match-abc", winnerTeam: "Red"));

            var reloaded = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json")); // 模拟 DS 进程重启
            Assert.That(reloaded.Count, Is.EqualTo(1), "落盘条目必须跨重启恢复");
            var snapshot = reloaded.CollectSnapshot();
            Assert.That(snapshot[0].matchId, Is.EqualTo("match-abc"));
            Assert.That(snapshot[0].winnerTeam, Is.EqualTo("Red"));
            Assert.That(snapshot[0].durationSeconds, Is.EqualTo(300));
            Assert.That(snapshot[0].players, Has.Length.EqualTo(1));
            Assert.That(snapshot[0].players[0].userId, Is.EqualTo(42L));
            Assert.That(snapshot[0].players[0].isWin, Is.True);
            Assert.That(snapshot[0].players[0].rewardEligible, Is.True);
        }

        [Test]
        public void Append_SameMatchId_Idempotent()
        {
            var store = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json"));
            store.Append(Request("match-abc"));
            store.Append(Request("match-abc")); // 同局重复落盘被拒（与后端幂等键一致）
            Assert.That(store.Count, Is.EqualTo(1));
        }

        [Test]
        public void Append_NullOrEmptyMatchId_Rejected()
        {
            var store = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json"));
            store.Append(null);
            store.Append(Request(""));
            Assert.That(store.Count, Is.EqualTo(0));
        }

        [Test]
        public void CollectSnapshot_BlankWinnerTeamNormalizedToNull()
        {
            var store = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json"));
            store.Append(Request("match-abc", winnerTeam: "")); // JsonUtility 往返产生的空白形态

            var snapshot = store.CollectSnapshot();
            Assert.That(snapshot[0].winnerTeam, Is.Null,
                "重放载荷必须与首次上报同内容（A01：空白归一 null）");
        }

        [Test]
        public void Remove_TerminalOutcome_PersistsRemoval()
        {
            var store = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json"));
            store.Append(Request("match-abc"));
            store.Append(Request("match-def"));

            Assert.That(store.Remove("match-abc"), Is.True, "终态移除必须成功");
            Assert.That(store.Remove("match-missing"), Is.False);

            var reloaded = new MatchResultPendingStore(Path.Combine(_directory, "match-result-pending.json")); // 移除后文件同步（重启不复活）
            Assert.That(reloaded.Count, Is.EqualTo(1));
            Assert.That(reloaded.CollectSnapshot()[0].matchId, Is.EqualTo("match-def"));
        }
    }
}
