using System;
using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// DS 聊天中继核心（C4/I2，复审 R06）：局内 Owner RPC 聊天的服务器权威状态——
    /// 每局（transportEpoch = matchId）独立 seq 空间（与后端 HTTP 聊天 seq 不同空间，客户端按
    /// (transport, epoch, seq) 与 clientMessageId 双重去重）；令牌桶/去重/校验与后端 ChatPolicy 同语义；
    /// 身份由服务器注入（认证档案），频道/队伍/收件人全部服务端决定，System 无客户端发送路径。
    /// 纯静态无 Unity 依赖，EditMode 可测（先例 MatchRules）。
    /// </summary>
    public static class ChatRelayCore
    {
        /// <summary>单条聊天消息（服务器构造；JSON/参数双形态共用字段名）。</summary>
        public sealed class ChatMessage
        {
            public string Epoch = string.Empty;
            public ulong Seq;
            public string Channel = ChatRules.ChannelAll;
            public long SenderUserId;
            public string SenderUsername = string.Empty;
            public string TeamId = MatchRules.TeamNone;
            public string Body = string.Empty;
            public string ClientMessageId = string.Empty;
        }

        public enum AcceptOutcome { Accepted, Duplicate, RateLimited, Rejected }

        public sealed class AcceptResult
        {
            public AcceptOutcome Outcome;
            public ChatMessage Message;
            public int RetryAfterSeconds;
            public string RejectReason = string.Empty;
        }

        private const int DedupPerUser = 64;
        private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);

        private sealed class EpochState
        {
            public ulong Seq;
            /// <summary>key=userId → (tokens, refilledAt)。</summary>
            public readonly Dictionary<long, (double Tokens, DateTime RefilledAt)> Buckets = new();
            /// <summary>key=userId → FIFO clientMessageId 列表；value=记录（近窗去重）。</summary>
            public readonly Dictionary<long, List<string>> DedupKeys = new();
            public readonly Dictionary<string, (DateTime At, ChatMessage Message)> Dedup = new();
        }

        private static readonly Dictionary<string, EpochState> Epochs = new(StringComparer.Ordinal);

        /// <summary>局内发言准入（服务器唯一入口）：校验 → 去重 → 限频 → 分配 seq 并构造服务器权威消息。</summary>
        public static AcceptResult TryAccept(string epoch, long senderUserId, string senderUsername, string senderTeam,
            string channel, string body, string clientMessageId, DateTime now)
        {
            if (string.IsNullOrEmpty(epoch))
                return Rejected("无有效比赛，局内聊天不可用");
            if (!ChatRules.IsValidChannel(channel))
                return Rejected("频道非法");
            if (!ChatRules.IsValidBody(body))
                return Rejected("正文为空、含换行/控制字符或超过 100 个字符");
            if (channel == ChatRules.ChannelTeam && !ChatRules.CanUseTeamChannel(senderTeam))
                return Rejected("该账号未加入队伍，不能使用队聊");

            var state = GetEpoch(epoch);
            body = body.Trim();

            // 端到端去重（先于扣桶；同 id 异内容拒绝——R05 同语义）
            if (!string.IsNullOrWhiteSpace(clientMessageId))
            {
                var key = DedupKey(senderUserId, clientMessageId);
                if (state.Dedup.TryGetValue(key, out var record))
                {
                    if (now - record.At < DedupWindow)
                    {
                        if (record.Message.Body == body)
                            return new AcceptResult { Outcome = AcceptOutcome.Duplicate, Message = record.Message };
                        return Rejected("同一 clientMessageId 已用于不同内容");
                    }
                    state.Dedup.Remove(key);
                    var keys = state.DedupKeys.TryGetValue(senderUserId, out var k) ? k : null;
                    keys?.Remove(clientMessageId);
                }
            }

            var (allowed, tokens, refilledAt, retryAfter) = ChatRules.TryConsumeToken(
                state.Buckets.TryGetValue(senderUserId, out var b) ? b.Tokens : ChatRules.TokenCapacity,
                state.Buckets.TryGetValue(senderUserId, out var b2) ? b2.RefilledAt : now,
                now, ChatRules.TokenCapacity, ChatRules.TokenRefillSeconds);
            state.Buckets[senderUserId] = (tokens, refilledAt);
            if (!allowed)
                return new AcceptResult { Outcome = AcceptOutcome.RateLimited, RetryAfterSeconds = retryAfter };

            state.Seq++;
            var message = new ChatMessage
            {
                Epoch = epoch,
                Seq = state.Seq,
                Channel = channel,
                SenderUserId = senderUserId,
                SenderUsername = senderUsername ?? string.Empty,
                TeamId = string.IsNullOrEmpty(senderTeam) ? MatchRules.TeamNone : senderTeam,
                Body = body,
                ClientMessageId = clientMessageId ?? string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(clientMessageId))
            {
                var key = DedupKey(senderUserId, clientMessageId);
                state.Dedup[key] = (now, message);
                var keys = state.DedupKeys.TryGetValue(senderUserId, out var list) ? list : null;
                if (keys is null)
                {
                    keys = new List<string>();
                    state.DedupKeys[senderUserId] = keys;
                }
                keys.Add(clientMessageId);
                while (keys.Count > DedupPerUser)
                {
                    var evicted = keys[0];
                    keys.RemoveAt(0);
                    state.Dedup.Remove(DedupKey(senderUserId, evicted));
                }
            }
            return new AcceptResult { Outcome = AcceptOutcome.Accepted, Message = message };
        }

        /// <summary>系统消息（服务端内部构造：进出/准备/设置/开始失败/倒计时/队伍获胜/返房/补人）。</summary>
        public static ChatMessage BuildSystem(string epoch, string body)
        {
            if (string.IsNullOrEmpty(epoch) || string.IsNullOrWhiteSpace(body))
                return null;
            var state = GetEpoch(epoch);
            state.Seq++;
            return new ChatMessage
            {
                Epoch = epoch,
                Seq = state.Seq,
                Channel = ChatRules.ChannelSystem,
                Body = body.Trim(),
            };
        }

        /// <summary>投递过滤（纯函数）：All/System → 全员；Team → 仅同队。服务器侧筛选，禁止全员广播后客户端隐藏。</summary>
        public static bool IsDeliverable(ChatMessage message, string recipientTeam)
        {
            if (message == null) return false;
            if (message.Channel != ChatRules.ChannelTeam) return true;
            var team = string.IsNullOrEmpty(recipientTeam) ? MatchRules.TeamNone : recipientTeam;
            return message.TeamId == team;
        }

        /// <summary>比赛终局/重臂时清空该局聊天状态（seq 空间随局终结，不跨局）。</summary>
        public static void ResetEpoch(string epoch)
        {
            if (!string.IsNullOrEmpty(epoch)) Epochs.Remove(epoch);
        }

        public static ulong CurrentSeq(string epoch) =>
            !string.IsNullOrEmpty(epoch) && Epochs.TryGetValue(epoch, out var s) ? s.Seq : 0UL;

        /// <summary>测试隔离：清空全部局状态。</summary>
        public static void ResetAllForTests() => Epochs.Clear();

        private static EpochState GetEpoch(string epoch)
        {
            if (!Epochs.TryGetValue(epoch, out var state))
            {
                state = new EpochState();
                Epochs[epoch] = state;
            }
            return state;
        }

        private static AcceptResult Rejected(string reason) =>
            new() { Outcome = AcceptOutcome.Rejected, RejectReason = reason };

        private static string DedupKey(long userId, string clientMessageId) => userId + ":" + clientMessageId;
    }
}
