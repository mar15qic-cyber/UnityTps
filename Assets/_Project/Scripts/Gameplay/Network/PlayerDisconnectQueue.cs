using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 已认证玩家掉线上报队列（Day2 三缺口 2026-09-07，纯逻辑，Bootstrap 驱动）。
    /// 上报对象：后端 server-key 端点 players/disconnect（成员行删除 + leader 转移 + 最后成员释放）。
    /// 用户定案规则：
    /// ① 只有已认证玩家断线才上报（ClientConnectionCleaned 携带的 TicketConsumeResult 快照为
    ///    null = 从未认证通过 → 跳过；debug 旁路无后端身份，同样跳过）；
    /// ② 同一 userId 已有新连接（仍在认证档案中）时，旧连接迟到的 Stopped 不得上报——
    ///    防止删除新会话的成员行（重连用户经大厅重取票据连回本实例，成员行必须保留）；
    /// ③ 后端暂时不可达 → 有界重试（每条上限 MaxAttemptsPerEntry 次，队列容量上限 MaxQueued 条），
    ///    恢复后补交，避免房间成员永久残留；超限丢弃必须留明显错误日志标记（Bootstrap 打印）。
    /// 时序依据：快照在 JoinTicketAuthenticator 内先取后删——本队列收到通知时，掉线连接已不在
    /// liveAcceptedUserIds 中，"同 userId 新连接"判定只看剩余连接，语义精确。
    /// </summary>
    public sealed class PlayerDisconnectQueue
    {
        /// <summary>队列容量上限（有界：溢出即丢弃最新并要求调用方告警——单人掉线事件体积极小，
        /// 16 已远超实际需要（后端恢复前至多缓存一次全员掉线）。</summary>
        public const int MaxQueued = 16;
        /// <summary>单条上报重试上限（含首发）：默认重试间隔 10s × 19 次 ≈ 3 分钟后端不可达仍不恢复才放弃。</summary>
        public const int MaxAttemptsPerEntry = 20;

        public sealed class Entry
        {
            public long UserId;
            public string RoomCode;
            public long SessionId;
            /// <summary>已尝试次数（含首发）。</summary>
            public int Attempts;
        }

        public enum NotifyResult
        {
            /// <summary>已入队（待发送/重试）。</summary>
            Enqueued,
            /// <summary>从未认证通过（快照为 null/无身份）——不上报（用户规则 ①）。</summary>
            SkippedNotAuthenticated,
            /// <summary>同 userId 已有新连接在认证档案中——旧连接迟到的 Stopped 不上报（用户规则 ②）。</summary>
            SkippedUserReconnected,
            /// <summary>队列已满，本条被丢弃（有界规则 ③；调用方必须告警）。</summary>
            DroppedQueueFull,
        }

        private readonly Queue<Entry> _pending = new();
        private readonly HashSet<string> _keys = new();

        /// <summary>当前排队条数（测试/日志）。</summary>
        public int Count => _pending.Count;

        /// <summary>
        /// 掉线通知（Bootstrap 在 ClientConnectionCleaned 时调用）。
        /// identitySnapshot = null 或无有效身份 → 不上报；liveAcceptedUserIds 含同 userId → 不上报；
        /// 同一 (userId, roomCode) 已在队列 → 幂等忽略（重试中的重复 Stopped 无需双份）。
        /// </summary>
        public NotifyResult Notify(
            TicketConsumeResult identitySnapshot,
            IReadOnlyCollection<TicketConsumeResult> liveAcceptedProfiles,
            long parsedUserId,
            string roomCode)
        {
            if (identitySnapshot == null || !identitySnapshot.Accepted
                || string.IsNullOrEmpty(identitySnapshot.UserId) || parsedUserId <= 0
                || string.IsNullOrEmpty(roomCode))
                return NotifyResult.SkippedNotAuthenticated;

            if (liveAcceptedProfiles != null)
            {
                foreach (var profile in liveAcceptedProfiles)
                {
                    if (profile != null && profile.UserId == identitySnapshot.UserId)
                        return NotifyResult.SkippedUserReconnected;
                }
            }

            string key = parsedUserId + "@" + roomCode;
            if (_keys.Contains(key))
                return NotifyResult.Enqueued; // 幂等：重试中重复通知不产生第二份

            if (_pending.Count >= MaxQueued)
                return NotifyResult.DroppedQueueFull;

            _keys.Add(key);
            _pending.Enqueue(new Entry
            {
                UserId = parsedUserId,
                RoomCode = roomCode,
                SessionId = identitySnapshot.SessionId,
                Attempts = 0,
            });
            return NotifyResult.Enqueued;
        }

        /// <summary>取出待发送条目（不删除——结果分类后再决定完成/重排；无到期条目返回空表）。</summary>
        public List<Entry> CollectPending()
        {
            var due = new List<Entry>(_pending.Count);
            while (_pending.Count > 0)
                due.Add(_pending.Dequeue());
            return due;
        }

        /// <summary>发送结果分类：终态（Accepted/RoomGone/StateConflict）→ 完成（真正出队）。</summary>
        public void Complete(Entry entry)
        {
            _keys.Remove(KeyOf(entry));
        }

        /// <summary>
        /// 传输失败：重排等待重试（队尾）；超过单条重试上限 → 丢弃（返回 true 表示已耗尽，调用方必须告警）。
        /// </summary>
        public bool RequeueOrDrop(Entry entry)
        {
            entry.Attempts++;
            if (entry.Attempts >= MaxAttemptsPerEntry)
            {
                _keys.Remove(KeyOf(entry));
                return true; // 耗尽丢弃：房间成员可能残留，需要人工核对——调用方打印错误级日志
            }
            _pending.Enqueue(entry);
            return false;
        }

        private static string KeyOf(Entry entry) => entry.UserId + "@" + entry.RoomCode;
    }
}
