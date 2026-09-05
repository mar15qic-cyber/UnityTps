using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 房间注册表服务（Docs/19 N4 + 退出对局 Phase E 收口）：房间码→房主直连地址的发现服务。
/// 实时对战不走本服务（FishNet client-hosted 直连，Docs/04）；这里只管
/// 创建/加入/列表/心跳/退出与懒清理（心跳超时 30s 的房间视为废弃）。
/// Phase E 纪律：
/// ① 成员计数唯一真相 = Members 导航集合（JoinedPlayers 全部路径 = Members.Count，
///    杜绝 Join/Leave/Purge 各自维护导致漂移；JoinedPlayers 只作 DTO 投影）；
/// ② LeaveAsync 幂等（重复离开=无成员直接返回）且走显式事务（EnableRetryOnFailure 兼容组合，
///    同 CommerceService 模式：CreateExecutionStrategy + IsRelational 判定）；
/// ③ JoinAsync 并发竞态由 GameRoomMember.UserId 唯一索引兜底（撞索引=他人已先行加入，
///    重读当前房间幂等返回）；
/// ④ 房主离开=房间解散（当前 client-hosted 拓扑的诚实语义：房主进程即服务器，
///    独立服务器拓扑落地前，离开者人数判定以实时比赛层 MatchLeavePolicy 为准，
///    本服务不做比赛终局语义）。
/// </summary>
public sealed class RoomService(AppDbContext db)
{
    private static readonly TimeSpan RoomTtl = TimeSpan.FromSeconds(30);

    public async Task<GameRoomDto> CreateAsync(long userId, CreateRoomRequest request, CancellationToken cancellationToken)
    {
        // 一人只能开一间房：先清理旧房
        await PurgeUserRoomsAsync(userId, cancellationToken);

        var user = await db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
        var room = new GameRoom
        {
            RoomCode = await GenerateUniqueRoomCodeAsync(cancellationToken),
            HostUserId = userId,
            HostUsername = user.Username,
            HostAddress = request.HostAddress.Trim(),
            HostPort = request.HostPort,
            MaxPlayers = Math.Clamp(request.MaxPlayers, 2, 16),
            JoinedPlayers = 1, // Members.Count 投影：创建者即第一名成员
            IsOpen = true,
            CreatedAtUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            Members = [new GameRoomMember { UserId = userId, JoinedAtUtc = DateTime.UtcNow }],
        };
        db.GameRooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(room);
    }

    public async Task<IReadOnlyList<GameRoomDto>> ListAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - RoomTtl;
        // 懒清理：过期房间直接删除（级联删成员）
        var stale = db.GameRooms.Where(x => x.LastHeartbeatUtc < cutoff);
        db.GameRooms.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);

        var rooms = await db.GameRooms.AsNoTracking()
            .Where(x => x.IsOpen && x.LastHeartbeatUtc >= cutoff)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        return rooms.Select(ToDto).ToList();
    }

    public async Task<GameRoomDto> JoinAsync(long userId, string roomCode, CancellationToken cancellationToken)
    {
        // 并发安全组合（Phase E ③）：策略 + 事务 + 唯一索引兜底，同 CommerceService 模式
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                await PurgeUserRoomsUnsafeAsync(userId, cancellationToken);
                var room = await db.GameRooms.Include(x => x.Members)
                    .SingleOrDefaultAsync(x => x.RoomCode == roomCode.ToUpperInvariant(), cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");

                if (!room.IsOpen) throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomClosed, "房间已关闭");
                if (room.Members.Any(x => x.UserId == userId))
                {
                    // 幂等重入（并发重放/重复点击）：已是成员直接返回当前房间
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return ToDto(room);
                }
                if (room.Members.Count >= room.MaxPlayers)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomFull, "房间已满");

                room.Members.Add(new GameRoomMember { UserId = userId, JoinedAtUtc = DateTime.UtcNow });
                room.JoinedPlayers = room.Members.Count; // Members 为真相
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return ToDto(room);
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                // 并发加入竞态：唯一索引(UserId) 撞键=他人/自己已先行落库 → 重读幂等返回
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var room = await db.GameRooms.Include(x => x.Members).AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Members.Any(m => m.UserId == userId), cancellationToken);
                if (room is not null) return ToDto(room);
                throw;
            }
        });
    }

    public async Task HeartbeatAsync(long userId, CancellationToken cancellationToken)
    {
        var room = await db.GameRooms.SingleOrDefaultAsync(x => x.HostUserId == userId, cancellationToken);
        if (room is null) throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "没有主持中的房间");
        room.LastHeartbeatUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveAsync(long userId, CancellationToken cancellationToken)
    {
        // Phase E ②：显式事务 + 幂等；成员计数以 Members 为真相
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var membership = await db.GameRoomMembers.Include(x => x.Room).ThenInclude(r => r.Members)
                    .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
                if (membership is null)
                {
                    // 幂等：未在任何房间（重复离开/未加入）
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return;
                }
                var room = membership.Room;
                var memberRow = room.Members.Single(x => x.UserId == userId);
                room.Members.Remove(memberRow);
                db.GameRoomMembers.Remove(membership);
                if (room.HostUserId == userId)
                {
                    // 房主离开=房间解散（client-hosted：房主进程即服务器，见类注释④）
                    db.GameRooms.Remove(room);
                }
                else
                {
                    room.JoinedPlayers = room.Members.Count; // Members 为真相（不再手工 ±1）
                }
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                // 重放幂等兜底：并发下成员可能已被其他请求移除，确认不存在即成功返回
                var stillThere = await db.GameRoomMembers.AsNoTracking()
                    .AnyAsync(x => x.UserId == userId, cancellationToken);
                if (stillThere) throw;
            }
        });
    }

    private async Task PurgeUserRoomsAsync(long userId, CancellationToken cancellationToken)
        => await PurgeUserRoomsUnsafeAsync(userId, cancellationToken);

    /// <summary>旧房清理（Unsafe=调用方已处于策略/事务上下文）：成员集合删除 + JoinedPlayers 重算。</summary>
    private async Task PurgeUserRoomsUnsafeAsync(long userId, CancellationToken cancellationToken)
    {
        var member = await db.GameRoomMembers.Include(x => x.Room).ThenInclude(r => r.Members)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (member is null) return;
        var room = member.Room;
        var memberRow = room.Members.Single(x => x.UserId == userId);
        room.Members.Remove(memberRow);
        db.GameRoomMembers.Remove(member);
        if (room.HostUserId == userId)
            db.GameRooms.Remove(room);
        else
            room.JoinedPlayers = room.Members.Count; // Members 为真相（修复旧实现缺 Include 的脏计数）
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueRoomCodeAsync(CancellationToken cancellationToken)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 无易混淆字符（I/O/0/1）
        var random = new Random();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var code = new string(Enumerable.Range(0, 6).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (!await db.GameRooms.AnyAsync(x => x.RoomCode == code, cancellationToken))
                return code;
        }
        throw new ApiException(StatusCodes.Status500InternalServerError, ApiErrorCodes.RoomCodeExhausted, "房间码生成失败，请重试");
    }

    private static GameRoomDto ToDto(GameRoom room) => new(
        room.RoomCode, room.HostUsername, room.HostAddress, room.HostPort,
        room.JoinedPlayers, room.MaxPlayers, room.IsOpen, room.CreatedAtUtc);
}
