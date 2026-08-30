using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 房间注册表服务（Docs/19 N4）：房间码→房主直连地址的发现服务。
/// 实时对战不走本服务（FishNet client-hosted 直连，Docs/04）；这里只管
/// 创建/加入/列表/心跳/退出与懒清理（心跳超时 30s 的房间视为废弃）。
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
            JoinedPlayers = 1,
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
        await PurgeUserRoomsAsync(userId, cancellationToken);
        var room = await db.GameRooms.Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.RoomCode == roomCode.ToUpperInvariant(), cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");

        if (!room.IsOpen) throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomClosed, "房间已关闭");
        if (room.HostUserId == userId) return ToDto(room); // 房主重入
        if (room.Members.Count >= room.MaxPlayers)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomFull, "房间已满");

        room.Members.Add(new GameRoomMember { UserId = userId, JoinedAtUtc = DateTime.UtcNow });
        room.JoinedPlayers = room.Members.Count;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(room);
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
        var membership = await db.GameRoomMembers.Include(x => x.Room)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (membership is null) return;
        var room = membership.Room;
        db.GameRoomMembers.Remove(membership);
        if (room.HostUserId == userId)
        {
            // 房主离开=房间解散
            db.GameRooms.Remove(room);
        }
        else
        {
            room.JoinedPlayers = Math.Max(1, room.Members.Count - 1);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PurgeUserRoomsAsync(long userId, CancellationToken cancellationToken)
    {
        var member = await db.GameRoomMembers.Include(x => x.Room)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (member is null) return;
        db.GameRoomMembers.Remove(member);
        if (member.Room.HostUserId == userId)
            db.GameRooms.Remove(member.Room);
        else
            member.Room.JoinedPlayers = Math.Max(1, member.Room.Members.Count - 1);
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
