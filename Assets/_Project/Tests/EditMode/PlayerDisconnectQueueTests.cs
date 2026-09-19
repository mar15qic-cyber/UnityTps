using System.Collections.Generic;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 三缺口掉线上报队列锁定（2026-09-07，纯逻辑）：
    /// ① 只有已认证玩家断线才上报（快照 null/无身份/缺房间码 → 跳过）；
    /// ② 同 userId 已有新连接（仍在认证档案中）→ 旧连接迟到的 Stopped 不上报（防删新会话成员）；
    /// ③ 重复通知幂等（同 user+room 只保一份）；
    /// ④ 有界：容量上限丢弃最新 + 单条重试上限丢弃（耗尽信号必须可被调用方感知以告警）；
    /// ⑤ 排水协议：CollectPending 取走→终态 Complete / 失败 RequeueOrDrop（队尾重排）。
    /// Bootstrap 排水循环（HTTP 发送/时延）为运行时壳，实进程行为由 IT-11 定向联测覆盖。
    /// </summary>
    public sealed class PlayerDisconnectQueueTests
    {
        private static TicketConsumeResult Snapshot(long userId, string roomCode = "ROOMAB")
            => TicketConsumeResult.AcceptedFromBackend(roomCode, userId.ToString(), "name");

        // ---- ① 只有已认证玩家断线才上报 ----

        [Test]
        public void NeverAuthenticatedSnapshot_IsSkipped()
        {
            var queue = new PlayerDisconnectQueue();
            Assert.That(queue.Notify(null, liveAcceptedProfiles: null, parsedUserId: 42, roomCode: "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.SkippedNotAuthenticated),
                "从未认证通过的连接（快照 null）不得调用 players/disconnect（用户规则 ②）");
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void RejectedSnapshot_IsSkipped()
        {
            var queue = new PlayerDisconnectQueue();
            var rejected = TicketConsumeResult.Rejected("TICKET_INVALID");
            Assert.That(queue.Notify(rejected, null, 0, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.SkippedNotAuthenticated),
                "被拒/无身份快照不上报");
        }

        [Test]
        public void MissingRoomCode_IsSkipped()
        {
            var queue = new PlayerDisconnectQueue();
            Assert.That(queue.Notify(Snapshot(42), null, 42, string.Empty),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.SkippedNotAuthenticated),
                "无房间码（后端异常数据）不上报——无从定位成员行");
        }

        // ---- ② 同 userId 新连接在档 → 迟到 Stopped 不上报 ----

        [Test]
        public void LateStoppedForReconnectedUser_IsSkipped()
        {
            var queue = new PlayerDisconnectQueue();
            // 旧连接断开清理后，同 userId 已用新连接重新认证（新 ClientId 在档）
            var liveProfiles = new[] { TicketConsumeResult.AcceptedFromBackend("ROOMAB", "42", "rejoined") };

            Assert.That(queue.Notify(Snapshot(42), liveProfiles, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.SkippedUserReconnected),
                "同 userId 已有新连接：旧连接迟到的 Stopped 不得上报（不得删除新会话成员）");
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void EnqueuedEntry_PreservesConsumedTicketSessionId()
        {
            var queue = new PlayerDisconnectQueue();
            var snapshot = TicketConsumeResult.AcceptedFromBackend("ROOMAB", "42", "name", sessionId: 1234);

            Assert.That(queue.Notify(snapshot, null, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued));

            var entries = queue.CollectPending();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].SessionId, Is.EqualTo(1234));
        }

        [Test]
        public void OtherUsersReconnected_DoesNotBlockThisReport()
        {
            var queue = new PlayerDisconnectQueue();
            // 其他 userId 的新连接不影响本条上报
            var liveProfiles = new[] { TicketConsumeResult.AcceptedFromBackend("ROOMAB", "99", "other") };

            Assert.That(queue.Notify(Snapshot(42), liveProfiles, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued));
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        // ---- ③ 幂等 / ④ 有界 ----

        [Test]
        public void DuplicateNotify_IsIdempotent()
        {
            var queue = new PlayerDisconnectQueue();
            Assert.That(queue.Notify(Snapshot(42), null, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued));
            Assert.That(queue.Notify(Snapshot(42), null, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued),
                "重复通知幂等（内部忽略，不产生第二份）");
            Assert.That(queue.Count, Is.EqualTo(1), "同 user+room 只保一份在途");
        }

        [Test]
        public void QueueCapacity_IsBounded()
        {
            var queue = new PlayerDisconnectQueue();
            for (long userId = 1; userId <= PlayerDisconnectQueue.MaxQueued; userId++)
            {
                Assert.That(queue.Notify(Snapshot(userId), null, userId, "ROOMAB"),
                    Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued));
            }
            Assert.That(queue.Count, Is.EqualTo(PlayerDisconnectQueue.MaxQueued));
            Assert.That(queue.Notify(Snapshot(999), null, 999, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.DroppedQueueFull),
                "有界规则：溢出即丢弃并要求调用方告警（防内存无界）");
            Assert.That(queue.Count, Is.EqualTo(PlayerDisconnectQueue.MaxQueued));
        }

        [Test]
        public void RetryAttempts_Bounded_DropSignalsExhaustion()
        {
            var queue = new PlayerDisconnectQueue();
            queue.Notify(Snapshot(42), null, 42, "ROOMAB");

            // 忠实模拟排水循环协议：CollectPending（取走）→ 发送失败 → RequeueOrDrop（重排）
            bool exhausted = false;
            int passes = 0;
            while (!exhausted && passes < 100)
            {
                passes++;
                var entries = queue.CollectPending();
                if (entries.Count == 0) break;
                exhausted = queue.RequeueOrDrop(entries[0]);
            }

            Assert.That(exhausted, Is.True,
                "单条重试达上限必须丢弃并给出耗尽信号（调用方打错误日志——房间成员可能残留）");
            Assert.That(queue.Count, Is.EqualTo(0), "耗尽后不得继续占用队列");
            Assert.That(passes, Is.EqualTo(PlayerDisconnectQueue.MaxAttemptsPerEntry),
                "总尝试次数（含首发）恰为单条上限");
        }

        // ---- ⑤ 排水协议 ----

        [Test]
        public void CollectPending_EmptiesQueue_CompleteRemovesKey()
        {
            var queue = new PlayerDisconnectQueue();
            queue.Notify(Snapshot(42), null, 42, "ROOMAB");

            var entries = queue.CollectPending();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].UserId, Is.EqualTo(42));
            Assert.That(entries[0].RoomCode, Is.EqualTo("ROOMAB"));

            queue.Complete(entries[0]);
            Assert.That(queue.Count, Is.EqualTo(0));

            // 终态后同 user 的新掉线可再次入队（key 已释放）
            Assert.That(queue.Notify(Snapshot(42), null, 42, "ROOMAB"),
                Is.EqualTo(PlayerDisconnectQueue.NotifyResult.Enqueued),
                "完成后 key 必须释放——后续掉线可重新上报");
        }

        [Test]
        public void Requeue_PreservesEntryForRetry_AttemptsIncrement()
        {
            var queue = new PlayerDisconnectQueue();
            queue.Notify(Snapshot(42), null, 42, "ROOMAB");
            var entry = queue.CollectPending()[0];

            Assert.That(queue.RequeueOrDrop(entry), Is.False, "未达上限：重排等待重试");
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(entry.Attempts, Is.EqualTo(1), "每次失败尝试计数递增");

            var retried = queue.CollectPending();
            Assert.That(retried.Count, Is.EqualTo(1), "重排条目可被下一轮排水取出");
            Assert.That(retried[0].UserId, Is.EqualTo(42));
        }

        [Test]
        public void CollectPending_EmptyQueue_ReturnsEmptyList()
        {
            var queue = new PlayerDisconnectQueue();
            Assert.That(queue.CollectPending(), Is.Empty);
        }
    }
}
