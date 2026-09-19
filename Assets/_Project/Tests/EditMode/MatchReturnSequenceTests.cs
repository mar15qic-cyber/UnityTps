using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// R5 审计修复：返房事务代际守卫（TaskCompletionSource 延迟响应直接用例）——
    /// 旧局查询/ack 在途时玩家切房/推进连接代际 → 被顶替的旧任务【不写结果、不 ack、
    /// 不断连接、不清上下文、不导航】；未被顶替时整条链依序完成。
    /// </summary>
    public sealed class MatchReturnSequenceTests
    {
        private static RoomMatchResultViewDto FinalDto(string matchId)
        {
            var players = new[] { new RoomMatchResultPlayerViewDto { userId = 1, username = "A", teamId = "Red", kills = 5, isWin = true } };
            var dto = new RoomMatchResultViewDto();
            dto.matchId = matchId;
            dto.status = "Final";
            dto.winnerTeam = "Red";
            dto.durationSeconds = 60;
            dto.players = players;
            return dto;
        }

        private static MatchReturnSequence.Deps MakeDeps(
            Func<MatchReturnSequence.SessionSnapshot> snapshot,
            TaskCompletionSource<RoomMatchResultViewDto> queryGate,
            TaskCompletionSource<bool> ackGate,
            List<string> log)
        {
            return new MatchReturnSequence.Deps
            {
                SnapshotSession = snapshot,
                QueryResultOnce = async () =>
                {
                    var dto = await queryGate.Task;
                    return (dto != null, dto);
                },
                Delay = () => Task.CompletedTask,
                AckReturn = async () =>
                {
                    log.Add("ack");
                    return await ackGate.Task;
                },
                PublishResult = dto => { log.Add("publish:" + dto.status); },
                StopBattleConnection = () => log.Add("stop-connection"),
                ClearLaunchContext = () => log.Add("clear-context"),
                NavigateToLobby = () => log.Add("navigate"),
            };
        }

        [Test]
        public async Task SupersededDuringQuery_WritesNothing_AndStops()
        {
            string roomCode = "ROOM01";
            string liveRoom = roomCode; // 可变会话状态（模拟查询在途时切房）
            long generation = 3;
            var queryGate = new TaskCompletionSource<RoomMatchResultViewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ackGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();

            var sequence = new MatchReturnSequence();
            var run = sequence.RunAsync(roomCode, "match-1", generation,
                MakeDeps(() => new MatchReturnSequence.SessionSnapshot(liveRoom, generation), queryGate, ackGate, log));

            // 查询响应在途时玩家切房（房间码变化）→ await 返回后的代际校验必须终止旧任务
            liveRoom = "ROOM02";
            queryGate.TrySetResult(FinalDto("match-1"));
            await run;
            await Task.Delay(50); // 延迟续体排空

            Assert.That(sequence.Superseded, Is.True);
            Assert.That(log, Does.Not.Contain("publish:Final"), "被顶替的旧任务不得写新结果");
            Assert.That(log, Does.Not.Contain("ack"), "不得 ack 当前（新）房间");
            Assert.That(log, Does.Not.Contain("stop-connection"), "绝不断开新会话连接");
            Assert.That(log, Does.Not.Contain("navigate"), "不得导航旧流程");
        }

        [Test]
        public async Task SupersededDuringAck_StopsBeforeSideEffects()
        {
            string roomCode = "ROOM01";
            long generation = 3;
            var queryGate = new TaskCompletionSource<RoomMatchResultViewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ackGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();
            long liveGeneration = generation;
            var sequence = new MatchReturnSequence();

            var run = sequence.RunAsync(roomCode, "match-1", generation, MakeDeps(
                () => new MatchReturnSequence.SessionSnapshot(roomCode, liveGeneration), queryGate, ackGate, log));

            queryGate.TrySetResult(FinalDto("match-1"));
            await Task.Yield();
            await Task.Delay(50);
            Assert.That(log, Does.Contain("publish:Final"), "未被顶替阶段正常发布");
            Assert.That(log, Does.Contain("ack"));

            // ack 在途时代际推进（新战斗连接）→ 后续副作用全部跳过
            liveGeneration = 4;
            ackGate.TrySetResult(true);
            await run;
            await Task.Delay(50);

            Assert.That(sequence.Superseded, Is.True);
            Assert.That(log, Does.Not.Contain("stop-connection"), "旧任务不得断开新连接");
            Assert.That(log, Does.Not.Contain("navigate"), "不得导航旧流程");
        }

        [Test]
        public async Task CleanRun_ExecutesWholeChain_InOrder()
        {
            string roomCode = "ROOM01";
            long generation = 3;
            var queryGate = new TaskCompletionSource<RoomMatchResultViewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ackGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();

            var sequence = new MatchReturnSequence();
            var run = sequence.RunAsync(roomCode, "match-1", generation,
                MakeDeps(() => new MatchReturnSequence.SessionSnapshot(roomCode, generation), queryGate, ackGate, log));

            queryGate.TrySetResult(FinalDto("match-1"));
            ackGate.TrySetResult(true);
            await run;
            await Task.Delay(50);

            Assert.That(sequence.Superseded, Is.False);
            Assert.That(sequence.ReachedFinal, Is.True);
            Assert.That(log, Is.EqualTo(new[] { "publish:Final", "ack", "stop-connection", "clear-context", "navigate" }).AsCollection);
        }

        [Test]
        public void IsSuperseded_RoomChangeOrGenerationBump()
        {
            Assert.That(MatchReturnSequence.IsSuperseded(new MatchReturnSequence.SessionSnapshot("ROOM01", 3), "ROOM01", 3), Is.False);
            Assert.That(MatchReturnSequence.IsSuperseded(new MatchReturnSequence.SessionSnapshot("ROOM02", 3), "ROOM01", 3), Is.True, "切房");
            Assert.That(MatchReturnSequence.IsSuperseded(new MatchReturnSequence.SessionSnapshot("ROOM01", 4), "ROOM01", 3), Is.True, "代际推进");
            Assert.That(MatchReturnSequence.IsSuperseded(new MatchReturnSequence.SessionSnapshot(null, 3), "ROOM01", 3), Is.True, "房间清空");
        }

        [Test]
        public async Task ProductionAckData_DelayedResponseAfterRoomSwitch_DoesNotPolluteAccountSession()
        {
            var session = new AccountSession();
            session.ApplyRoomSnapshot(new RoomSnapshotDto
            {
                room = new GameRoomDto { roomCode = "ROOM01", status = RoomStatus.Returning },
            });
            long capturedGeneration = session.ConnectionGeneration;
            var ackGate = new TaskCompletionSource<(bool Success, RoomSnapshotDto Data)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var newRoom = new RoomSnapshotDto
            {
                room = new GameRoomDto { roomCode = "ROOM02", status = RoomStatus.Waiting },
            };
            var oldAck = new RoomSnapshotDto
            {
                room = new GameRoomDto { roomCode = "ROOM01", status = RoomStatus.Waiting },
            };
            int applyCount = 0;
            var sequence = new MatchReturnSequence();
            var run = sequence.RunAsync("ROOM01", "match-1", capturedGeneration,
                new MatchReturnSequence.Deps
                {
                    SnapshotSession = () => new MatchReturnSequence.SessionSnapshot(
                        session.Room?.RoomCode, session.ConnectionGeneration),
                    QueryResultOnce = () => Task.FromResult((true, FinalDto("match-1"))),
                    Delay = () => Task.CompletedTask,
                    AckReturnWithData = () => ackGate.Task,
                    ApplyAckSnapshot = snapshot =>
                    {
                        applyCount++;
                        var live = session.Room;
                        if (live != null && live.RoomCode == "ROOM01"
                            && session.ConnectionGeneration == capturedGeneration
                            && snapshot?.room?.roomCode == "ROOM01")
                            session.RefreshRoomSnapshot(snapshot);
                    },
                    StopBattleConnection = () => Assert.Fail("旧 ack 不得断开新连接"),
                    ClearLaunchContext = () => Assert.Fail("旧 ack 不得清新上下文"),
                    NavigateToLobby = () => Assert.Fail("旧 ack 不得导航"),
                });

            // 连接代际保持不变，但玩家已在大厅切换到另一房间；旧 HTTP 响应随后才完成。
            session.ApplyRoomSnapshot(newRoom);
            ackGate.TrySetResult((true, oldAck));
            await run;

            Assert.That(sequence.Superseded, Is.True);
            Assert.That(applyCount, Is.EqualTo(0), "旧 ack 快照不得写入 AccountSession");
            Assert.That(session.Room.RoomCode, Is.EqualTo("ROOM02"));
            Assert.That(session.Room.Status, Is.EqualTo(RoomStatus.Waiting));
        }
    }
}
