using System;
using System.Threading.Tasks;
using Game.Account;

namespace Game.UI
{
    /// <summary>
    /// R5（审计修复）CF 房间对局返房事务的纯编排核心：「权威结果查询 → 结果发布 → 返房 ack →
    /// 断战斗连接 → 清启动上下文 → 导航回大厅」整条异步链共用【捕获的 roomCode + matchId +
    /// ConnectionGeneration】——每个 await 返回后重新校验代际/房间，被顶替（玩家切房/新战斗连接）
    /// 即立刻终止：不写结果、不 ack、不断新连接、不清新上下文、不导航。旧任务从此不可能断开新会话。
    /// 依赖全部注入（查询/等待/ack/副作用），EditMode 用 TaskCompletionSource 延迟响应锁定守卫语义；
    /// 生产装配见 MatchSettlementFlow。
    /// </summary>
    public sealed class MatchReturnSequence
    {
        /// <summary>会话快照（房间码 + 连接代际）。</summary>
        public readonly struct SessionSnapshot
        {
            public readonly string RoomCode;
            public readonly long Generation;
            public SessionSnapshot(string roomCode, long generation) { RoomCode = roomCode ?? string.Empty; Generation = generation; }
        }

        /// <summary>注入依赖（全部必填；生产由 MatchSettlementFlow 装配，测试用假实现）。</summary>
        public sealed class Deps
        {
            /// <summary>每个 await 前后采样的会话快照（房间码/代际；Func 而非值——保证读的是当下状态）。</summary>
            public Func<SessionSnapshot> SnapshotSession;
            /// <summary>单次权威结果查询（Success=false = 错误，计入重试）。</summary>
            public Func<Task<(bool Success, RoomMatchResultViewDto Data)>> QueryResultOnce;
            /// <summary>轮询间隔等待（测试注入即时完成/可控时钟）。</summary>
            public Func<Task> Delay;
            /// <summary>返房 ack（true = 成功）。</summary>
            public Func<Task<bool>> AckReturn;
            /// <summary>
            /// 可选的生产 ack seam：只返回 HTTP 结果与快照，不在 await 委托内写 AccountSession。
            /// RunAsync 在捕获会话身份复核通过后才调用 ApplyAckSnapshot。
            /// </summary>
            public Func<Task<(bool Success, RoomSnapshotDto Data)>> AckReturnWithData;
            /// <summary>捕获身份仍有效时应用 ack 快照；调用方必须保持同步、无 await。</summary>
            public Action<RoomSnapshotDto> ApplyAckSnapshot;
            /// <summary>发布结果（仅未被顶替时调用；生产写 LastRoomResult）。</summary>
            public Action<RoomMatchResultViewDto> PublishResult;
            /// <summary>断开战斗连接（仅未被顶替时调用）。</summary>
            public Action StopBattleConnection;
            /// <summary>清启动上下文（仅未被顶替时调用）。</summary>
            public Action ClearLaunchContext;
            /// <summary>导航回大厅（仅未被顶替时调用）。</summary>
            public Action NavigateToLobby;
        }

        public const int MaxAttempts = 12;
        public const double PollIntervalSeconds = 1.0;

        /// <summary>整条链是否因代际/房间被顶替而终止（true = 未发布/未 ack/未断连接/未导航）。</summary>
        public bool Superseded { get; private set; }

        /// <summary>轮询取得的最后快照（未成功时为 null；被顶替时不保证已发布）。</summary>
        public RoomMatchResultViewDto LastSnapshot { get; private set; }

        /// <summary>轮询是否以 Final 结束（false = 超限仍 Pending，等待房间页补刷新入口）。</summary>
        public bool ReachedFinal { get; private set; }

        /// <summary>代际守卫（纯函数）：会话房间码与捕获不一致，或代际推进 = 已被顶替。</summary>
        public static bool IsSuperseded(SessionSnapshot live, string capturedRoomCode, long capturedGeneration)
            => !string.Equals(live.RoomCode, capturedRoomCode, StringComparison.Ordinal)
               || live.Generation != capturedGeneration;

        public async Task RunAsync(string roomCode, string matchId, long generationAtStart, Deps deps)
        {
            if (deps == null) throw new ArgumentNullException(nameof(deps));

            // ---- 阶段 1：权威结果轮询（每次查询前后校验代际；Pending 上限 = MaxAttempts）----
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (CheckSuperseded(deps, roomCode, generationAtStart)) return;
                var outcome = await deps.QueryResultOnce();
                if (CheckSuperseded(deps, roomCode, generationAtStart)) return;
                if (outcome.Success && outcome.Data != null)
                {
                    LastSnapshot = outcome.Data;
                    if (outcome.Data.status == "Final")
                    {
                        ReachedFinal = true;
                        break;
                    }
                }
                if (attempt < MaxAttempts - 1)
                    await deps.Delay();
            }

            if (!CheckSuperseded(deps, roomCode, generationAtStart))
                deps.PublishResult?.Invoke(LastSnapshot);

            // ---- 阶段 2：返房 ack → 断战斗 → 清上下文 → 导航（每步 await 后校验）----
            if (CheckSuperseded(deps, roomCode, generationAtStart)) return;
            bool acked;
            RoomSnapshotDto ackSnapshot = null;
            if (deps.AckReturnWithData != null)
            {
                var ackResult = await deps.AckReturnWithData();
                acked = ackResult.Success;
                ackSnapshot = ackResult.Data;
            }
            else
            {
                acked = deps.AckReturn != null && await deps.AckReturn();
            }
            if (CheckSuperseded(deps, roomCode, generationAtStart)) return;

            // F3：旧响应只在同一 room + connection generation 仍存活时写入会话。
            // ApplyAckSnapshot 本身还需做一次身份检查，以覆盖委托返回与此处之间的同步接管。
            if (acked && ackSnapshot != null)
            {
                deps.ApplyAckSnapshot?.Invoke(ackSnapshot);
                // RefreshRoomSnapshot 会同步触发 AccountSession.Changed；订阅方可能在
                // 回调中接管会话，因此应用快照后、断开战斗连接前再复核一次。
                if (CheckSuperseded(deps, roomCode, generationAtStart)) return;
            }

            if (acked)
                deps.StopBattleConnection?.Invoke();
            else
                UnityEngine.Debug.LogWarning("[MatchReturnSequence] 返房 ack 未成功（后端懒维护兜底）——仍按既定链路返回等待房间");
            deps.ClearLaunchContext?.Invoke();
            deps.NavigateToLobby?.Invoke();
        }

        private bool CheckSuperseded(Deps deps, string roomCode, long generationAtStart)
        {
            var live = deps.SnapshotSession != null
                ? deps.SnapshotSession()
                : new SessionSnapshot(string.Empty, generationAtStart);
            if (IsSuperseded(live, roomCode, generationAtStart))
            {
                Superseded = true;
                UnityEngine.Debug.Log($"[MatchReturnSequence] 返房链终止（会话已变化：room={live.RoomCode} gen={live.Generation} ≠ 捕获 room={roomCode} gen={generationAtStart}）");
            }
            return Superseded;
        }
    }
}
