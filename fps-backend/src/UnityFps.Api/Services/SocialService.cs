using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class SocialService(AppDbContext db, RoomService rooms)
{
    private static ApiException Error(string code, string message, int status = 409) => new(status, code, message);
    private Task<bool> AreFriends(long user, long peer, CancellationToken ct) => db.Friendships.AnyAsync(f => f.UserId == user && f.FriendId == peer, ct);
    private static DirectMessageDto Message(DirectMessage m) => new(m.Id, m.SenderId, m.RecipientId, m.ClientMessageId, m.Body, m.CreatedAtUtc);
    private IQueryable<DirectMessage> Conversation(long user, long peer) => db.Set<DirectMessage>().Where(m =>
        (m.SenderId == user && m.RecipientId == peer) || (m.SenderId == peer && m.RecipientId == user));

    public async Task<DirectMessagePageDto> History(long user, long peer, long before, long after, CancellationToken ct)
    {
        var query = Conversation(user, peer).AsNoTracking();
        if (before > 0) query = query.Where(m => m.Id < before);
        if (after > 0) query = query.Where(m => m.Id > after);
        var rows = after > 0 ? await query.OrderBy(m => m.Id).Take(51).ToListAsync(ct)
            : await query.OrderByDescending(m => m.Id).Take(51).ToListAsync(ct);
        return new(rows.Take(50).OrderBy(m => m.Id).Select(Message).ToArray(), rows.Count > 50);
    }

    public async Task<DirectMessageDto> Send(long user, long peer, SendDirectMessageRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.ClientMessageId, out var key) || !ChatPolicy.IsValidBody(request.Body))
            throw Error("DM_INVALID", "消息不能为空，最多 100 字且不能包含换行", 400);
        var clientId = key.ToString("D");
        var existing = await db.Set<DirectMessage>().SingleOrDefaultAsync(m => m.SenderId == user && m.ClientMessageId == clientId, ct);
        if (existing != null)
        {
            if (existing.RecipientId != peer || existing.Body != request.Body) throw Error("DM_ID_CONFLICT", "消息标识已被使用");
            return Message(existing);
        }
        if (!await AreFriends(user, peer, ct)) throw Error("DM_NOT_FRIEND", "对方已不是你的好友", 403);
        var since = DateTime.UtcNow.AddSeconds(-6);
        if (await db.Set<DirectMessage>().CountAsync(m => m.SenderId == user && m.CreatedAtUtc > since, ct) >= 3)
            throw Error("DM_RATE_LIMIT", "发送过于频繁，请稍后重试", 429);
        var row = new DirectMessage { SenderId = user, RecipientId = peer, ClientMessageId = clientId, Body = request.Body, CreatedAtUtc = DateTime.UtcNow };
        db.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            existing = await db.Set<DirectMessage>().SingleOrDefaultAsync(m => m.SenderId == user && m.ClientMessageId == clientId, ct);
            if (existing == null) throw;
            if (existing.RecipientId != peer || existing.Body != request.Body) throw Error("DM_ID_CONFLICT", "消息标识已被使用");
            return Message(existing);
        }
        return Message(row);
    }

    public async Task MarkRead(long user, long peer, long requested, CancellationToken ct, int attempt = 0)
    {
        var last = await Conversation(user, peer).Where(m => m.Id <= requested).MaxAsync(m => (long?)m.Id, ct) ?? 0;
        if (last == 0) return;
        var row = await db.Set<DirectMessageRead>().FindAsync([user, peer], ct);
        if (row == null) db.Add(new DirectMessageRead { UserId = user, PeerId = peer, LastReadId = last });
        else row.LastReadId = Math.Max(row.LastReadId, last);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (attempt < 3) { db.ChangeTracker.Clear(); await MarkRead(user, peer, last, ct, attempt + 1); }
    }

    public async Task<SocialInboxDto> Inbox(long user, CancellationToken ct)
    {
        var peerIds = await db.Set<DirectMessage>().Where(m => m.SenderId == user || m.RecipientId == user)
            .Select(m => m.SenderId == user ? m.RecipientId : m.SenderId).Distinct().ToListAsync(ct);
        var users = await db.Users.Where(u => peerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var friends = await db.Friendships.Where(f => f.UserId == user).Select(f => f.FriendId).ToListAsync(ct);
        var reads = await db.Set<DirectMessageRead>().Where(r => r.UserId == user).ToDictionaryAsync(r => r.PeerId, r => r.LastReadId, ct);
        var conversations = new List<ConversationDto>();
        foreach (var peer in peerIds.Where(users.ContainsKey))
        {
            var last = await Conversation(user, peer).OrderByDescending(m => m.Id).FirstAsync(ct);
            var read = reads.GetValueOrDefault(peer);
            var unread = await Conversation(user, peer).CountAsync(m => m.RecipientId == user && m.Id > read, ct);
            var person = users[peer];
            conversations.Add(new(peer, person.Username, person.IdentityTag, last.Id, last.Body, unread, friends.Contains(peer)));
        }
        var now = DateTime.UtcNow;
        var invitations = await db.Set<RoomInvitation>().Where(i => i.RecipientId == user && i.State == "Pending" && i.ExpiresAtUtc > now).ToListAsync(ct);
        var cards = new List<RoomInvitationDto>();
        foreach (var invite in invitations)
        {
            var room = await db.GameRooms.Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == invite.RoomId, ct);
            if (room == null || room.Status != RoomStatus.Waiting || !room.Members.Any(m => m.UserId == invite.SenderId)
                || !friends.Contains(invite.SenderId)) continue;
            var sender = await db.Users.FindAsync([invite.SenderId], ct);
            cards.Add(new(invite.Id, room.Id, invite.SenderId, sender?.Username ?? "", room.HostUsername, room.MapId, room.Mode,
                room.Members.Count, room.MaxPlayers, invite.ExpiresAtUtc, invite.State));
        }
        return new(conversations.OrderByDescending(c => c.LastMessageId).ToArray(), cards.ToArray());
    }

    public async Task Invite(long user, SendRoomInvitationRequest request, CancellationToken ct)
    {
        if (!await AreFriends(user, request.FriendId, ct)) throw Error("INVITE_NOT_FRIEND", "只能邀请好友", 403);
        var room = await db.GameRooms.Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == request.RoomId, ct);
        if (room == null || room.Status != RoomStatus.Waiting || !room.Members.Any(m => m.UserId == user))
            throw Error("INVITE_ROOM_STATE", "仅等待房间成员可以邀请好友");
        if (room.Members.Any(m => m.UserId == request.FriendId)) throw Error("INVITE_ALREADY_MEMBER", "好友已在房间中");
        var peer = await db.Users.FindAsync([request.FriendId], ct);
        var active = await db.GameRoomMembers.AnyAsync(m => m.UserId == request.FriendId, ct);
        if (!active && (peer?.LastSeenUtc == null || peer.LastSeenUtc < DateTime.UtcNow.AddMinutes(-2)))
            throw Error("INVITE_OFFLINE", "好友当前离线");
        var row = await db.Set<RoomInvitation>().SingleOrDefaultAsync(i => i.RoomId == request.RoomId && i.SenderId == user && i.RecipientId == request.FriendId, ct);
        if (row != null && row.State == "Pending" && row.ExpiresAtUtc > DateTime.UtcNow) return;
        if (row == null) { row = new RoomInvitation { RoomId = request.RoomId, SenderId = user, RecipientId = request.FriendId }; db.Add(row); }
        row.State = "Pending"; row.CreatedAtUtc = DateTime.UtcNow; row.ExpiresAtUtc = row.CreatedAtUtc.AddMinutes(5);
        await db.SaveChangesAsync(ct);
    }

    public async Task<RoomSnapshotDto> Accept(long user, long id, string? protocol, CancellationToken ct)
    {
        var invite = await db.Set<RoomInvitation>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.RecipientId == user, ct)
            ?? throw Error("INVITE_GONE", "邀请不存在", 404);
        var room = await db.GameRooms.AsNoTracking().Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == invite.RoomId, ct);
        if ((invite.State != "Pending" && !(invite.State == "Accepted" && room != null && room.Members.Any(m => m.UserId == user))) || invite.ExpiresAtUtc <= DateTime.UtcNow || room?.Status != RoomStatus.Waiting
            || !room.Members.Any(m => m.UserId == invite.SenderId) || !await AreFriends(user, invite.SenderId, ct))
            throw Error("INVITE_EXPIRED", "邀请已失效");
        var current = await db.GameRoomMembers.AsNoTracking().Include(m => m.Room).SingleOrDefaultAsync(m => m.UserId == user, ct);
        if (current != null && current.RoomId != room.Id) throw Error("INVITE_LEAVE_FIRST", "请先退出当前房间");
        var snapshot = await rooms.JoinAsync(user, room.RoomCode, null, protocol, ct, waitingOnly: true, invitationId: id);
        var saved = await db.Set<RoomInvitation>().FindAsync([id], ct);
        if (saved != null) { saved.State = "Accepted"; await db.SaveChangesAsync(ct); }
        return snapshot;
    }

    public async Task Reject(long user, long id, CancellationToken ct)
    {
        var row = await db.Set<RoomInvitation>().SingleOrDefaultAsync(i => i.Id == id && i.RecipientId == user, ct);
        if (row == null) return;
        row.State = "Rejected"; await db.SaveChangesAsync(ct);
    }
}
