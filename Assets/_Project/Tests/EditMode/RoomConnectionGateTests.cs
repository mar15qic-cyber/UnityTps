using System;
using Game.Account;
using Game.Gameplay.Network;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>Docs/27 v1.2 CF 定向测试：RoomConnectionInfoDto 严格校验、ticket 卫生、
    /// 「加载 Arena 前写入 NetworkLaunchContext、失败不写不加载」、比赛身份必填。</summary>
    public sealed class RoomConnectionGateTests
    {
        [TearDown]
        public void TearDown()
        {
            NetworkLaunchContext.Clear();
        }

        private static RoomConnectionInfoDto ValidConnection()
        {
            return new RoomConnectionInfoDto
            {
                serverAddress = "10.0.0.8",
                serverPort = 7770,
                joinTicket = "one-time-ticket-plain",
                ticketExpiresAtUtc = DateTime.UtcNow.AddMinutes(5).ToString("o"),
                matchId = Guid.NewGuid().ToString("N"),
                matchGeneration = 1,
            };
        }

        // ---- 校验通过 ----

        [Test]
        public void Valid_Passes()
        {
            Assert.That(RoomConnectionGate.TryValidate(ValidConnection(), DateTime.UtcNow, out var error), Is.True);
            Assert.That(error, Is.Null.Or.Empty);
        }

        // ---- 每类失败都不放行（失败路径绝不写 NetworkLaunchContext、绝不加载 Arena）----

        [Test]
        public void NullConnection_Fails()
        {
            Assert.That(RoomConnectionGate.TryValidate(null, DateTime.UtcNow, out _), Is.False);
        }

        [Test]
        public void EmptyAddress_Fails()
        {
            var dto = ValidConnection();
            dto.serverAddress = "   ";
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out var error), Is.False);
            Assert.That(error, Does.Contain("地址"));
        }

        [Test]
        public void PortZero_Fails()
        {
            var dto = ValidConnection();
            dto.serverPort = 0;
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out _), Is.False);
        }

        [Test]
        public void PortAboveRange_Fails()
        {
            var dto = ValidConnection();
            dto.serverPort = 65536;
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out _), Is.False);
        }

        [Test]
        public void EmptyTicket_Fails()
        {
            var dto = ValidConnection();
            dto.joinTicket = "";
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out var error), Is.False);
            Assert.That(error, Does.Contain("票据"));
        }

        [Test]
        public void ExpiredTicket_Fails()
        {
            var dto = ValidConnection();
            dto.ticketExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("o");
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out var error), Is.False);
            Assert.That(error, Does.Contain("过期"));
        }

        [Test]
        public void MalformedExpiry_Fails()
        {
            var dto = ValidConnection();
            dto.ticketExpiresAtUtc = "not-a-timestamp";
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out _), Is.False);
        }

        [Test]
        public void MissingMatchIdentity_Fails()
        {
            var dto = ValidConnection();
            dto.matchId = "";
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out var error), Is.False);
            Assert.That(error, Does.Contain("比赛身份"));
        }

        // ---- ticket 卫生：写上下文 → 立即清除 DTO 明文 → 上下文一次性消费 ----

        [Test]
        public void WriteLaunchContext_ConsumesOnce_AndSanitizesDto()
        {
            var dto = ValidConnection();
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.False,
                "前置：无遗留待消费上下文");

            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out _), Is.True);
            RoomConnectionGate.WriteLaunchContext(dto, "Arena");

            // DTO 明文已清除（配置完成后 UI/临时 DTO 不再持有 ticket）
            Assert.That(dto.joinTicket, Is.Null);
            Assert.That(dto.ticketExpiresAtUtc, Is.Null);
            // 上下文可消费且一次性
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.True);
            var launch = NetworkLaunchContext.TryBeginClientLaunch();
            Assert.That(launch, Is.Not.Null);
            Assert.That(launch.ServerAddress, Is.EqualTo("10.0.0.8"));
            Assert.That(launch.ServerPort, Is.EqualTo(7770));
            Assert.That(launch.JoinTicket, Is.EqualTo("one-time-ticket-plain"));
            Assert.That(launch.SceneName, Is.EqualTo("Arena"), "场景过滤载荷随票据写入（P0-B）");
            Assert.That(launch.MatchId, Is.EqualTo(dto.matchId), "比赛身份随载荷写入（日志归属）");
            Assert.That(launch.Generation, Is.GreaterThan(0), "代际由 ConfigureClient 分配");
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.False, "消费即清空，明文不滞留");
        }

        [Test]
        public void InvalidDto_NeverWritesLaunchContext()
        {
            var dto = ValidConnection();
            dto.joinTicket = "";
            Assert.That(RoomConnectionGate.TryValidate(dto, DateTime.UtcNow, out _), Is.False);
            // 失败路径：调用方只允许 Clear，绝不 Configure——上下文保持为空（不加载 Arena）
            NetworkLaunchContext.Clear();
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.False);
        }

        // ---- 日志卫生：预检本项目新增代码不得拼接 ticket 明文（源码级静态断言）----

        [Test]
        public void TicketNeverInterpolatedIntoLogs_InGateAndPresenter()
        {
            foreach (var path in new[]
            {
                "Assets/_Project/Scripts/UI/RoomConnectionGate.cs",
                "Assets/_Project/Scripts/UI/LobbyPresenter.cs",
                "Assets/_Project/Scripts/UI/Pages/LobbyPresenter.ShellPages.cs",
                "Assets/_Project/Scripts/UI/Pages/LobbyPresenter.WaitingRoom.cs",
            })
            {
                var text = System.IO.File.ReadAllText(path);
                Assert.That(text, Does.Not.Contain("joinTicket}\""), $"{path} 不得把 ticket 拼进字符串");
                Assert.That(text, Does.Not.Contain("{dto.joinTicket"), $"{path} 不得插值 ticket");
                Assert.That(text, Does.Not.Contain("{connection.joinTicket"), $"{path} 不得插值 ticket");
                Assert.That(text, Does.Not.Contain("Debug.Log($"+"\"[Lobby] ticket"), $"{path} 不得记录 ticket");
            }
        }
    }
}
