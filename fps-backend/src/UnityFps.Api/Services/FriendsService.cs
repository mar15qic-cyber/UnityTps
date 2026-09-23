using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 好友系统（2026-09-20 需求2）：用户名#编码 双约束查找 + 请求-同意制 + 在线状态。
/// 关系模型：Friendship 对称两行（复合主键 UserId+FriendId）；FriendRequest 一方一行、
/// accept 后删除并建双向关系。所有"唯一性冲突"由数据库唯一索引兜底，catch DbUpdateException
/// 转业务码（EnableRetryOnFailure 下单次 SaveChanges 自带原子性，无需显式事务）。
/// </summary>
public sealed class FriendsService(AppDbContext db)
{
    /// <summary>在线判定窗口：LastSeenUtc 在此窗口内视为"在线"（客户端壳页 30s 心跳 × 余量）。</summary>
    private static readonly TimeSpan PresenceOnlineWindow = TimeSpan.FromMinutes(2);
    /// <summary>LastSeenUtc 写入节流：热点轮询端点（好友列表/房间心跳）高频命中，30s 内不重复写。</summary>
    private static readonly TimeSpan PresenceWriteThrottle = TimeSpan.FromSeconds(30);

    public async Task<FriendListDto> ListAsync(long userId, CancellationToken cancellationToken)
    {
        await TouchPresenceAsync(userId, cancellationToken);

        var friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.UserId == userId).Select(f => f.FriendId).ToListAsync(cancellationToken);
        var friends = await BuildEntriesAsync(friendIds, cancellationToken);

        var incomingRows = await db.FriendRequests.AsNoTracking()
            .Where(r => r.ToUserId == userId).OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        var outgoingRows = await db.FriendRequests.AsNoTracking()
            .Where(r => r.FromUserId == userId).OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        var requesterIds = incomingRows.Select(r => r.FromUserId)
            .Concat(outgoingRows.Select(r => r.ToUserId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => requesterIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.IdentityTag }).ToDictionaryAsync(u => u.Id, cancellationToken);

        var incoming = incomingRows
            .Where(r => users.ContainsKey(r.FromUserId))
            .Select(r => new FriendRequestEntryDto(r.Id, r.FromUserId, users[r.FromUserId].Username, users[r.FromUserId].IdentityTag, r.CreatedAtUtc))
            .ToArray();
        var outgoing = outgoingRows
            .Where(r => users.ContainsKey(r.ToUserId))
            .Select(r => new FriendRequestEntryDto(r.Id, r.ToUserId, users[r.ToUserId].Username, users[r.ToUserId].IdentityTag, r.CreatedAtUtc))
            .ToArray();
        return new FriendListDto(friends, incoming, outgoing);
    }

    public async Task<FriendRequestEntryDto> SendAsync(long userId, string query, CancellationToken cancellationToken)
    {
        var (name, tag) = ParseQuery(query);
        var normalized = AuthService.Normalize(name);
        var target = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.NormalizedUsername == normalized && u.IdentityTag == tag, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.FriendNotFound, "找不到该 用户名#编码 对应的用户");
        if (target.Id == userId)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.FriendSelf, "不能添加自己为好友");

        var alreadyFriends = await db.Friendships.AsNoTracking().AnyAsync(f =>
            (f.UserId == userId && f.FriendId == target.Id) || (f.UserId == target.Id && f.FriendId == userId), cancellationToken);
        if (alreadyFriends)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.AlreadyFriends, "对方已经是你的好友");

        var requestExists = await db.FriendRequests.AsNoTracking().AnyAsync(r =>
            (r.FromUserId == userId && r.ToUserId == target.Id) || (r.FromUserId == target.Id && r.ToUserId == userId), cancellationToken);
        if (requestExists)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.FriendRequestExists, "已存在待处理的好友申请");

        var request = new FriendRequest { FromUserId = userId, ToUserId = target.Id, CreatedAtUtc = DateTime.UtcNow };
        db.FriendRequests.Add(request);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            // 并发双发撞唯一索引 (FromUserId, ToUserId)：语义与前置检查一致
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.FriendRequestExists, "已存在待处理的好友申请");
        }
        return new FriendRequestEntryDto(request.Id, target.Id, target.Username, target.IdentityTag, request.CreatedAtUtc);
    }

    public async Task AcceptAsync(long userId, long requestId, CancellationToken cancellationToken)
    {
        var request = await db.FriendRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw RequestGone();
        if (request.ToUserId != userId) throw RequestGone();

        // 单次 SaveChanges 原子：删申请 + 建双向关系；并发重复 accept 撞 Friendship 主键 → "已被处理"
        db.FriendRequests.Remove(request);
        var now = DateTime.UtcNow;
        db.Friendships.AddRange(
            new Friendship { UserId = userId, FriendId = request.FromUserId, CreatedAtUtc = now },
            new Friendship { UserId = request.FromUserId, FriendId = userId, CreatedAtUtc = now });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw RequestGone(); }
    }

    /// <summary>拒绝（收件人）/撤销（发件人）共用：两者都只是删除申请行。</summary>
    public async Task RemoveRequestAsync(long userId, long requestId, CancellationToken cancellationToken)
    {
        var request = await db.FriendRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw RequestGone();
        if (request.ToUserId != userId && request.FromUserId != userId) throw RequestGone();
        db.FriendRequests.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveFriendAsync(long userId, long friendUserId, CancellationToken cancellationToken)
    {
        var rows = await db.Friendships
            .Where(f => (f.UserId == userId && f.FriendId == friendUserId) || (f.UserId == friendUserId && f.FriendId == userId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.FriendNotFound, "对方不是你的好友");
        db.Friendships.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- 内部 ----

    private static ApiException RequestGone() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.FriendRequestNotFound, "好友申请不存在或已被处理");

    /// <summary>查询串解析：按最后一个 # 切分；名字 ≤32、编码必须 4 位数字（与展示格式一致，含前导零）。</summary>
    private static (string Name, string Tag) ParseQuery(string query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        var index = trimmed.LastIndexOf('#');
        if (index <= 0 || index == trimmed.Length - 1)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.FriendQueryInvalid, "格式无效：请输入 用户名#编码（如 Player#4821）");
        var name = trimmed[..index].Trim();
        var tag = trimmed[(index + 1)..].Trim();
        if (name.Length == 0 || name.Length > 32 || tag.Length != 4 || !tag.All(char.IsDigit))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.FriendQueryInvalid, "格式无效：请输入 用户名#编码（如 Player#4821）");
        return (name, tag);
    }

    private async Task<FriendEntryDto[]> BuildEntriesAsync(List<long> friendIds, CancellationToken cancellationToken)
    {
        if (friendIds.Count == 0) return Array.Empty<FriendEntryDto>();
        var users = await db.Users.AsNoTracking().Where(u => friendIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.IdentityTag, u.LastSeenUtc })
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var roomStates = await db.GameRoomMembers.AsNoTracking().Where(m => friendIds.Contains(m.UserId))
            .Select(m => new { m.UserId, RoomStatus = m.Room.Status }).ToListAsync(cancellationToken);
        // 一人同时只能在一个房间（UserId 唯一索引）；读取按 Normalize 防御旧状态值
        var stateByUser = roomStates.GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => RoomStatus.Normalize(g.First().RoomStatus));

        var now = DateTime.UtcNow;
        return friendIds.Where(users.ContainsKey).Select(id =>
        {
            var user = users[id];
            return new FriendEntryDto(id, user.Username, user.IdentityTag,
                ResolvePresence(stateByUser.GetValueOrDefault(id), user.LastSeenUtc, now));
        }).OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolvePresence(string? roomStatus, DateTime? lastSeenUtc, DateTime now)
    {
        if (roomStatus == RoomStatus.InMatch) return FriendPresence.InMatch;
        if (roomStatus is RoomStatus.Waiting or RoomStatus.Starting or RoomStatus.Returning) return FriendPresence.InRoom;
        if (lastSeenUtc is { } seen && now - seen <= PresenceOnlineWindow) return FriendPresence.Online;
        return FriendPresence.Offline;
    }

    /// <summary>在场信号：节流刷新本人 LastSeenUtc（单条 UPDATE 原子判重，无读改写竞态）。
    /// InMemory 测试宿主无 SQL 通道，跳过（在线状态测试直接置实体字段）。</summary>
    private async Task TouchPresenceAsync(long userId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational()) return;
        var threshold = DateTime.UtcNow - PresenceWriteThrottle;
        await db.Database.ExecuteSqlAsync(
            $"UPDATE UserAccount SET LastSeenUtc = {DateTime.UtcNow} WHERE Id = {userId} AND (LastSeenUtc IS NULL OR LastSeenUtc < {threshold})",
            cancellationToken);
    }
}
