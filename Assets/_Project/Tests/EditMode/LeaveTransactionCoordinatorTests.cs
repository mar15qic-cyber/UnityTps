using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Menu;
using Game.UI.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day4 残余审计 P1-1（Gate A-3 延续）定向测试：退出对局事务协调器——
    /// 成功 / HTTP 失败 / 有界超时 / 异常四条路径全部最终清本地 Room 并 Complete
    ///（生产注入 = 停客户端→清 NetworkLaunchContext→回大厅）；重复点击幂等；
    /// 「新会话已替代旧会话」改用显式单调 ConnectionGeneration 判定：
    /// 真正新会话接管后旧事务零副作用（不清房、不停新连接、不清新上下文、不导航），
    /// 同房间快照刷新（generation 不推进）不影响退出正常完成。
    /// 全部用同步完成的 Task 模拟（EditMode 无异步泵依赖）。
    /// </summary>
    public sealed class LeaveTransactionCoordinatorTests
    {
        private sealed class Recorder
        {
            public int BeginCalls;
            public int ApiCalls;
            public int ClearRoomCalls;
            public int CompleteCalls;
            public readonly List<string> Warnings = new List<string>();
            public GameplayMenuController.LeaveTransactionOutcome NextBegin =
                GameplayMenuController.LeaveTransactionOutcome.ProceedNetwork;
            public long CurrentGeneration;
            public CancellationToken LastApiToken;
        }

        private static LeaveTransactionCoordinator Create(Recorder rec,
            Func<CancellationToken, Task<ApiResult<object>>> api = null,
            TimeSpan? timeout = null)
        {
            return new LeaveTransactionCoordinator(
                begin: () =>
                {
                    rec.BeginCalls++;
                    return rec.NextBegin;
                },
                leaveApi: api == null ? null : token =>
                {
                    rec.ApiCalls++;
                    rec.LastApiToken = token;
                    return api(token);
                },
                generationProvider: () => rec.CurrentGeneration,
                clearRoom: () => rec.ClearRoomCalls++,
                complete: () => rec.CompleteCalls++,
                timeout: timeout ?? TimeSpan.FromSeconds(LeaveTransactionCoordinator.DefaultTimeoutSeconds),
                warn: message => rec.Warnings.Add(message));
        }

        private static Task<ApiResult<object>> OkTask() => Task.FromResult(ApiResult<object>.Ok(null));

        private static Task<ApiResult<object>> FailTask(string code) =>
            Task.FromResult(ApiResult<object>.Fail(409, code, "test failure"));

        // ---- 成功路径 ----

        [Test]
        public void SuccessPath_ClearsRoom_InvokesComplete_AndReportsStarted()
        {
            var rec = new Recorder();
            var coordinator = Create(rec, api: _ => OkTask());

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.BeginCalls, Is.EqualTo(1));
            Assert.That(rec.ApiCalls, Is.EqualTo(1));
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1), "成功路径必须清本地 Room");
            Assert.That(rec.CompleteCalls, Is.EqualTo(1), "成功路径必须 Complete（停客户端→清上下文→回大厅由注入方执行）");
            Assert.That(rec.Warnings, Is.Empty);
            Assert.That(coordinator.HasCompleted, Is.True);
            Assert.That(coordinator.IsRunning, Is.False);
        }

        [Test]
        public void SuccessPath_PassesCancellableTimeoutToken_ToApi()
        {
            var rec = new Recorder();
            var coordinator = Create(rec, api: _ => OkTask());

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.LastApiToken, Is.Not.EqualTo(default(CancellationToken)), "超时令牌必须传给 API 调用");
            Assert.That(rec.LastApiToken.CanBeCanceled, Is.True, "有界超时依赖可取消令牌");
        }

        // ---- HTTP 失败路径 ----

        [Test]
        public void ApiFailure_StillClearsRoom_Completes_AndWarnsWithCode()
        {
            var rec = new Recorder();
            var coordinator = Create(rec, api: _ => FailTask("409"));

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1), "失败也继续本地流程（DS 掉线端点兜底）");
            Assert.That(rec.CompleteCalls, Is.EqualTo(1));
            Assert.That(rec.Warnings.Count, Is.EqualTo(1));
            Assert.That(rec.Warnings[0], Does.Contain("409").And.Contain("DS 掉线端点兜底"));
        }

        // ---- 有界超时路径 ----

        [Test]
        public void Timeout_StillClearsRoom_Completes_AndWarns()
        {
            var rec = new Recorder();
            // 已取消任务 = 同步模拟「等待超过有界超时」→ OperationCanceledException 路径
            var canceled = new CancellationToken(canceled: true);
            var coordinator = Create(rec, api: _ => Task.FromCanceled<ApiResult<object>>(canceled),
                timeout: TimeSpan.FromMilliseconds(50));

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1), "超时后本地照常返回");
            Assert.That(rec.CompleteCalls, Is.EqualTo(1));
            Assert.That(rec.Warnings.Count, Is.EqualTo(1));
            Assert.That(rec.Warnings[0], Does.Contain("有界超时").And.Contain("DS 掉线端点兜底"));
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator UncooperativeTransport_ExitStillCompletes()
        {
            var rec = new Recorder();
            var never = new TaskCompletionSource<ApiResult<object>>();
            var coordinator = Create(rec, api: _ => never.Task, timeout: TimeSpan.FromMilliseconds(30));
            var task = coordinator.RunAsync();
            var deadline = UnityEngine.Time.realtimeSinceStartup + 2f;
            while (!task.IsCompleted && UnityEngine.Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Exit must complete even when cancellation is ignored");
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1));
            Assert.That(rec.CompleteCalls, Is.EqualTo(1));
            never.SetResult(ApiResult<object>.Ok(null));
            yield return null;
            Assert.That(rec.CompleteCalls, Is.EqualTo(1), "Late HTTP completion must not navigate twice");
        }

        // ---- 异常路径 ----

        [Test]
        public void ApiException_StillClearsRoom_Completes_AndWarns()
        {
            var rec = new Recorder();
            var coordinator = Create(rec, api: _ => Task.FromException<ApiResult<object>>(
                new InvalidOperationException("connection refused")));

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1));
            Assert.That(rec.CompleteCalls, Is.EqualTo(1));
            Assert.That(rec.Warnings.Count, Is.EqualTo(1));
            Assert.That(rec.Warnings[0], Does.Contain("异常").And.Contain("connection refused"));
        }

        // ---- 重复点击幂等 ----

        [Test]
        public void RepeatedClick_WhileRunning_IsRejected_WithoutSideEffects()
        {
            var rec = new Recorder();
            var gate = new TaskCompletionSource<ApiResult<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = Create(rec, api: _ => gate.Task);

            coordinator.RunAsync(); // 挂起于 leave API（模拟真实网络延迟）
            Assert.That(coordinator.IsRunning, Is.True);
            Assert.That(coordinator.RunAsync().Result, Is.False, "进行中重复点击必须拒绝");
            Assert.That(rec.BeginCalls, Is.EqualTo(1), "重入不得再次 Begin");
            Assert.That(rec.ApiCalls, Is.EqualTo(1));
            Assert.That(rec.CompleteCalls, Is.EqualTo(0));
            // 本用例刻意不让事务完成（gate 不释放）——隔离实例，不影响其它用例
        }

        [Test]
        public void RepeatedClick_AfterCompletion_IsRejected_WithoutSideEffects()
        {
            var rec = new Recorder();
            var coordinator = Create(rec, api: _ => FailTask("409"));

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(coordinator.RunAsync().Result, Is.False, "已完成事务不得重入");
            Assert.That(rec.BeginCalls, Is.EqualTo(1));
            Assert.That(rec.ApiCalls, Is.EqualTo(1));
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1));
            Assert.That(rec.CompleteCalls, Is.EqualTo(1), "重复点击不得重复 Complete（防二次停连接/清上下文）");
        }

        [Test]
        public void BeginRejected_ReturnsFalse_WithoutAnySideEffect()
        {
            var rec = new Recorder { NextBegin = GameplayMenuController.LeaveTransactionOutcome.Rejected };
            var coordinator = Create(rec, api: _ => OkTask());

            Assert.That(coordinator.RunAsync().Result, Is.False);
            Assert.That(rec.ApiCalls, Is.EqualTo(0));
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(0));
            Assert.That(rec.CompleteCalls, Is.EqualTo(0));
        }

        // ---- 不得触碰已被新会话替代的状态（P1-1 generation 守卫） ----

        [Test]
        public void GenerationAdvancedDuringTransaction_Supersedes_AllSideEffectsZero()
        {
            var rec = new Recorder();
            rec.CurrentGeneration = 5;
            var coordinator = Create(rec, api: _ =>
            {
                rec.CurrentGeneration = 6; // 事务期间真正的新 create/join/reconnect 成功（代际推进）
                return FailTask("timeout");
            });

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(coordinator.Superseded, Is.True, "代际推进必须标记 superseded");
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(0), "新会话的 Room 绝不得被旧事务清除");
            // complete 承载生产侧 StopConnection / NetworkLaunchContext.Clear / 回大厅导航——
            // 断言它 0 次调用即断言三者全部不被旧事务触碰
            Assert.That(rec.CompleteCalls, Is.EqualTo(0),
                "superseded 事务不得停连接/清启动上下文/导航");
            Assert.That(rec.Warnings, Has.Some.Contains("superseded"));
            Assert.That(rec.Warnings, Has.Some.Contains("code="), "HTTP 失败告警照常留痕");
        }

        [Test]
        public void GenerationUnchangedDuringTransaction_ClearsRoom_AndCompletes()
        {
            var rec = new Recorder();
            rec.CurrentGeneration = 5;
            var coordinator = Create(rec, api: _ => OkTask());

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(coordinator.Superseded, Is.False);
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(1));
            Assert.That(rec.CompleteCalls, Is.EqualTo(1));
        }

        [Test]
        public void GenerationAdvancedWhileNoneAtBegin_SupersedesTransaction()
        {
            // 事务开始时无会话代际（0），期间出现真正的新会话 → 同样视为被替代
            var rec = new Recorder(); // CurrentGeneration = 0
            var coordinator = Create(rec, api: _ =>
            {
                rec.CurrentGeneration = 1;
                return FailTask("timeout");
            });

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(coordinator.Superseded, Is.True);
            Assert.That(rec.ClearRoomCalls, Is.EqualTo(0));
            Assert.That(rec.CompleteCalls, Is.EqualTo(0));
        }

        // ---- 离线/本地服（ReturnNow）路径 ----

        [Test]
        public void ReturnNowPath_DoesNotCallApi_AndReportsStarted()
        {
            var rec = new Recorder { NextBegin = GameplayMenuController.LeaveTransactionOutcome.ReturnNow };
            var coordinator = Create(rec, api: _ => OkTask());

            coordinator.RunAsync().WaitForCompletion();
            Assert.That(rec.ApiCalls, Is.EqualTo(0), "离线/本地服：begin 已就地回大厅，无需后端 leave");
            Assert.That(rec.CompleteCalls, Is.EqualTo(0), "导航已由 begin 内部完成，Complete 不得重复触发");
            Assert.That(coordinator.HasCompleted, Is.True);
        }
    }

    /// <summary>AccountSession.ConnectionGeneration 代际语义（P1-1 的会话侧锁定）。</summary>
    public sealed class AccountSessionGenerationTests
    {
        private static AccountSession NewSession()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "t",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = "me" },
            });
            return session;
        }

        private static RoomSnapshotDto Snapshot(string roomCode, string leader, int players, string status = "Waiting")
        {
            return new RoomSnapshotDto
            {
                room = new GameRoomDto { roomCode = roomCode, leaderUsername = leader, joinedPlayers = players, maxPlayers = 8, status = status },
                members = System.Array.Empty<RoomMemberDto>(),
                you = new RoomSelfDto { userId = 1, teamId = TeamId.None, isReady = false },
                connection = null,
            };
        }

        [Test]
        public void ApplyRoomSnapshot_DoesNotAdvanceGeneration()
        {
            var session = NewSession();
            long gen0 = session.ConnectionGeneration;
            // Docs/27 §11 CF：等待房间的创建/加入不是战斗连接，不推进代际
            session.ApplyRoomSnapshot(Snapshot("AAAAAA", "me", 1));
            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen0), "进等待房间不递增代际");
            session.ApplyRoomSnapshot(Snapshot("BBBBBB", "me", 1));
            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen0), "换房同样只替换状态");
        }

        [Test]
        public void AdvanceConnectionGeneration_IsTheOnlyBump()
        {
            var session = NewSession();
            long gen0 = session.ConnectionGeneration;
            session.ApplyRoomSnapshot(Snapshot("AAAAAA", "me", 1));
            session.AdvanceConnectionGeneration(); // 战斗连接启动（写上下文进 Arena）唯一递增点
            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen0 + 1), "战斗连接启动=新会话");
        }

        [Test]
        public void RefreshRoomSnapshot_DoesNotAdvanceGeneration_ButUpdatesFields()
        {
            var session = NewSession();
            session.ApplyRoomSnapshot(Snapshot("AAAAAA", "me", 1));
            long gen = session.ConnectionGeneration;

            // 同房间的成员数/队长/状态快照刷新（CF 等待房间轮询形态）——原地更新，代际不推进
            var refresh = Snapshot("AAAAAA", "someone_else", 2);
            refresh.room.roomVersion = 5;
            refresh.you.teamId = TeamId.Blue;
            session.RefreshRoomSnapshot(refresh);

            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen),
                "同房间快照刷新不得推进代际（否则正常退出被误判 superseded）");
            Assert.That(session.Room, Is.Not.Null, "快照刷新不清房间");
            Assert.That(session.Room.RoomCode, Is.EqualTo("AAAAAA"));
            Assert.That(session.Room.MemberCount, Is.EqualTo(2), "快照字段原地更新");
            Assert.That(session.Room.IsHost, Is.False, "队长变更随快照更新");
            Assert.That(session.Room.RoomVersion, Is.EqualTo(5), "房间版本随快照更新");
            Assert.That(session.Room.TeamId, Is.EqualTo(TeamId.Blue), "选边随快照更新");
        }

        [Test]
        public void RefreshRoomSnapshot_WithNoRoom_DelegatesToApply()
        {
            var session = NewSession();
            long gen0 = session.ConnectionGeneration;
            session.RefreshRoomSnapshot(Snapshot("BBBBBB", "me", 1));
            Assert.That(session.Room, Is.Not.Null);
            Assert.That(session.Room.RoomCode, Is.EqualTo("BBBBBB"));
            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen0), "建立房间会话同样不推进战斗连接代际");
        }
    }

    /// <summary>Task&lt;bool&gt;.Result 的显式包装（仅用于已同步完成的任务，避免裸 .Result 语义混淆）。</summary>
    internal static class LeaveTransactionTestExtensions
    {
        public static void WaitForCompletion(this Task<bool> task) => task.GetAwaiter().GetResult();
    }
}
