using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 房间聊天内存存储（Docs/27 §8）：每房间环形缓冲最近 100 条（不落数据库，进程重启清空属可接受）、
/// seq 单调、令牌桶限频（容量 3 / 每 2s 回 1，按 账号+房间）、(userId, clientMessageId) 近窗去重。
/// 单例注册（内存状态跨请求存活）；历史按服务器顺序排列，新成员不补发其 ChatJoinSeq 之前的历史。
/// 复审 R05：①去重先于扣桶——有效重试不消耗额度；②去重记录独立于环形缓冲（有界 per-user 字典），
/// 消息被挤出 Ring 后近窗重试仍命中原消息、不重新广播；③同 id 不同内容按冲突拒绝；
/// ④换队可见水位：SetTeam 记录 (userId, teamId) 的 seq 边界，Fetch 只投递"本次入队之后"的队聊。
/// </summary>
public sealed class RoomChatService
{
    private readonly int ringCapacity;
    private readonly int dedupCapacityPerUser;
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, RoomChatBuffer> rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerInstanceOptions options;

    public RoomChatService(IOptions<ServerInstanceOptions> options)
    {
        this.options = options.Value;
        ringCapacity = Math.Clamp(this.options.ChatRingCapacity, 10, 1000);
        dedupCapacityPerUser = Math.Clamp(this.options.ChatDedupCapacityPerUser, 8, 512);
    }

    /// <summary>成员发言：校验/去重/限频后写入并分配 seq。违规抛 CHAT_*（422/429）。</summary>
    public ChatMessageDto Send(GameRoom room, GameRoomMember member, ChatSendRequest request)
    {
        var channel = request.Channel?.Trim() ?? string.Empty;
        if (!ChatPolicy.IsValidChannel(channel))
            throw Rejected("频道非法");
        if (!ChatPolicy.IsValidBody(request.Body))
            throw Rejected("正文为空、含换行/控制字符或超过 100 个字符");
        var body = request.Body.Trim();
        if (channel == "Team" && !ChatPolicy.CanUseTeamChannel(member.TeamId))
            throw Rejected("该账号未加入队伍，不能使用队聊");

        var buffer = rooms.GetOrAdd(room.RoomCode, _ => new RoomChatBuffer());
        var now = DateTime.UtcNow;
        lock (buffer.Gate)
        {
            // 端到端去重（R05：先于扣桶）——同账号同 clientMessageId 近窗内返回原消息（不重发不计费，
            // Ring 淘汰后仍命中）；同 id 不同内容 = 客户端缺陷或伪造，按冲突拒绝（绝不覆盖历史消息）
            if (!string.IsNullOrWhiteSpace(request.ClientMessageId))
            {
                var dedupKey = DedupKey(member.UserId, request.ClientMessageId);
                if (buffer.Dedup.TryGetValue(dedupKey, out var record))
                {
                    if (now - record.At < DedupWindow)
                    {
                        if (record.Message.Body == body)
                            return record.Message;
                        throw Rejected("同一 clientMessageId 已用于不同内容");
                    }
                    // 超窗：删除过期记录，重新走完整流程
                    buffer.Dedup.Remove(dedupKey);
                    if (buffer.DedupKeys.TryGetValue(member.UserId, out var staleKeys))
                        staleKeys.Remove(request.ClientMessageId);
                }
            }

            // 令牌桶按 账号+房间（仅新消息扣额）
            var capacity = Math.Clamp(options.ChatTokenCapacity, 1, 100);
            var refillSeconds = Math.Clamp(options.ChatTokenRefillSeconds, 1, 3600);
            var bucket = buffer.Buckets.TryGetValue(member.UserId, out var b) ? (Tokens: b.Tokens, RefilledAt: b.RefilledAt) : (Tokens: (double)capacity, RefilledAt: now);
            var (allowed, tokens, refilledAt, retryAfter) = ChatPolicy.TryConsumeToken(bucket.Tokens, bucket.RefilledAt, now, capacity, refillSeconds);
            buffer.Buckets[member.UserId] = (tokens, refilledAt);
            if (!allowed)
                throw new ApiException(StatusCodes.Status429TooManyRequests, ApiErrorCodes.ChatRateLimited,
                    $"发言过于频繁，请 {retryAfter} 秒后重试");

            buffer.Seq++;
            var message = new ChatMessageDto(buffer.Seq, channel, member.UserId, member.User?.Username ?? "?",
                member.TeamId, body, now, request.ClientMessageId);
            buffer.Ring.Add(message);
            TrimLocked(buffer);
            // 去重记录独立于 Ring（R05）：消息被挤出 Ring 后，近窗重试仍按原消息返回
            if (!string.IsNullOrWhiteSpace(request.ClientMessageId))
            {
                buffer.Dedup[DedupKey(member.UserId, request.ClientMessageId)] = (message, now);
                var userKeys = buffer.DedupKeys.TryGetValue(member.UserId, out var keys) ? keys : null;
                if (userKeys is null)
                {
                    userKeys = new List<string>();
                    buffer.DedupKeys[member.UserId] = userKeys;
                }
                userKeys.Add(request.ClientMessageId);
                while (userKeys.Count > dedupCapacityPerUser)
                {
                    var evicted = userKeys[0];
                    userKeys.RemoveAt(0);
                    buffer.Dedup.Remove(DedupKey(member.UserId, evicted));
                }
            }
            return message;
        }
    }

    /// <summary>
    /// 增量拉取：seq &gt; max(after, 成员 ChatJoinSeq)；Team 消息仅同队可见且不得早于本次入队水位
    /// （R05：红→蓝→红后不补发旧队历史）；单次封顶 50 条。
    /// </summary>
    public ChatFeedDto Fetch(GameRoom room, GameRoomMember member, ulong after)
    {
        var buffer = rooms.TryGetValue(room.RoomCode, out var b) ? b : null;
        if (buffer is null)
            return new ChatFeedDto(after, []);
        lock (buffer.Gate)
        {
            if (after > buffer.Seq)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.ChatCursorInvalid, "聊天游标越界");
            var floor = Math.Max(after, member.ChatJoinSeq);
            var teamFloor = buffer.TeamJoinWatermarks.TryGetValue(TeamWatermarkKey(member.UserId, member.TeamId), out var mark)
                ? Math.Max(floor, mark)
                : floor;
            var messages = buffer.Ring
                .Where(m => m.Seq > floor)
                .Where(m => m.Channel != "Team" || m.TeamId == member.TeamId)
                .Where(m => m.Channel != "Team" || m.Seq > teamFloor)
                .OrderBy(m => m.Seq)
                .TakeLast(50)
                .ToArray();
            return new ChatFeedDto(buffer.Seq, messages);
        }
    }

    /// <summary>系统消息（服务端内部构造；无客户端发送路径——Docs/27 §8.3）。房间无消息时惰性创建。</summary>
    public void SendSystem(string roomCode, string body)
    {
        var buffer = rooms.GetOrAdd(roomCode.Trim().ToUpperInvariant(), _ => new RoomChatBuffer());
        lock (buffer.Gate)
        {
            buffer.Seq++;
            buffer.Ring.Add(new ChatMessageDto(buffer.Seq, "System", null, null, null, body, DateTime.UtcNow, null));
            TrimLocked(buffer);
        }
    }

    /// <summary>
    /// 换队可见水位（R05，RoomService.SetTeamAsync 在实际换队时调用）：记录切换时刻 seq，
    /// 该成员此后只可见"本次加入该队之后"的队聊（旧队历史与新队旧历史一并隔离）。
    /// </summary>
    public void MarkTeamSwitch(string roomCode, long userId, string newTeamId)
    {
        var buffer = rooms.TryGetValue(roomCode.Trim().ToUpperInvariant(), out var b) ? b : null;
        if (buffer is null) return;
        lock (buffer.Gate)
        {
            buffer.TeamJoinWatermarks[TeamWatermarkKey(userId, newTeamId)] = buffer.Seq;
        }
    }

    /// <summary>房间当前 seq 水位（新成员入房时取 ChatJoinSeq 用）。</summary>
    public ulong CurrentSeq(string roomCode) =>
        rooms.TryGetValue(roomCode, out var b) ? b.Seq : 0;

    /// <summary>房间拆除时清空内存聊天（最后成员离开/删房路径调用）。</summary>
    public void DropRoom(string roomCode) => rooms.TryRemove(roomCode.Trim().ToUpperInvariant(), out _);

    private static string DedupKey(long userId, string clientMessageId) => userId + ":" + clientMessageId;
    private static (long UserId, string TeamId) TeamWatermarkKey(long userId, string teamId) => (userId, teamId ?? string.Empty);

    private void TrimLocked(RoomChatBuffer buffer)
    {
        if (buffer.Ring.Count > ringCapacity)
            buffer.Ring.RemoveRange(0, buffer.Ring.Count - ringCapacity);
    }

    private sealed class RoomChatBuffer
    {
        public object Gate { get; } = new();
        public ulong Seq { get; set; }
        public List<ChatMessageDto> Ring { get; } = [];
        public Dictionary<long, (double Tokens, DateTime RefilledAt)> Buckets { get; } = [];
        /// <summary>去重记录（R05）：key=(userId, clientMessageId) → (原消息, at)；有界 per-user。</summary>
        public Dictionary<string, (ChatMessageDto Message, DateTime At)> Dedup { get; } = [];
        /// <summary>每用户 clientMessageId 插入序（FIFO 淘汰用）。</summary>
        public Dictionary<long, List<string>> DedupKeys { get; } = [];
        /// <summary>换队水位（R05）：(userId, teamId) → 切换时刻 seq。</summary>
        public Dictionary<(long UserId, string TeamId), ulong> TeamJoinWatermarks { get; } = [];
    }

    private static ApiException Rejected(string reason) =>
        new(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.ChatRejected, reason);
}
