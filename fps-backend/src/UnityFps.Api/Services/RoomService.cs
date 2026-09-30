using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 房间服务（Docs/27 v1 CF 等待房间语义）：房间 = 成员名单 + 规则快照；创建/加入仅进 Waiting，
/// 不再租用 DS/签票（租 DS、建比赛、签个人票全部移至 Start）。生命周期：
/// Waiting → Starting（90s 冻结窗口）→ InMatch → Returning → Waiting，终态 Closed。
/// 纪律（沿 Phase E 并扩展）：
/// ① 成员计数唯一真相 = Members 导航集合（JoinedPlayers 全路径 = Members.Count）；
/// ② 写路径走显式事务（CreateExecutionStrategy + Serializable，同 CommerceService 模式）；
/// ③ 加入竞态由 GameRoomMember.UserId 唯一索引兜底；租用/释放竞态由 ServerInstance.Version
///    乐观并发令牌兜底（InMemory 测试提供方无事务时的唯一防线），冲突后有界重试；
/// ④ 懒维护（Docs/27 §3/§9）：Waiting/Starting 按成员 LastSeenUtc 过期清人；Starting 超时/实例失联回
///    Waiting 保准备；InMatch 实例失联删房（沿用旧语义）；Returning 超时回 Waiting；
/// ⑤ 旧局隔离：写操作携带 matchId 时与 CurrentMatchId 不符一律 409 ROOM_STATE_CONFLICT 零副作用。
/// </summary>
public sealed class RoomService(AppDbContext db, ServerInstanceService instances, MatchService matches, RoomChatService chat, ILogger<RoomService> logger)
{
    public async Task<string> ResolveInternalCodeAsync(long roomId, CancellationToken ct) =>
        await db.GameRooms.Where(r => r.Id == roomId && r.Status != RoomStatus.Closed)
            .Select(r => r.RoomCode).SingleOrDefaultAsync(ct)
        ?? throw new ApiException(404, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");

    public async Task<RoomSnapshotDto> CreateAsync(long userId, CreateRoomRequest request, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await TryCreateOnceAsync(userId, request, cancellationToken);
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    // 房间码竞态：丢弃追踪状态重试
                    db.ChangeTracker.Clear();
                }
            }
        });
    }

    private async Task<RoomSnapshotDto> TryCreateOnceAsync(long userId, CreateRoomRequest request, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            // 一人只能开一间房：先把旧房按 Leave 同款语义处理掉（转移/释放）
            await PurgeUserRoomsUnsafeAsync(userId, cancellationToken);
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, cancellationToken);
            var now = DateTime.UtcNow;
            // 白名单校验（Docs/27 §1/§5.4）：违规整体 422，不做静默钳制
            var settings = RoomSettingRules.Validate(
                (request.Mode ?? GameModes.Tdm).Trim(), (request.MapId ?? MapCatalog.DefaultMapId).Trim(),
                request.KillTarget, request.TimeLimitMinutes, request.MaxPlayers);

            var room = new GameRoom
            {
                RoomCode = await GenerateUniqueRoomCodeAsync(cancellationToken),
                HostUserId = userId,
                HostUsername = user.Username,
                MaxPlayers = settings.MaxPlayers,
                JoinedPlayers = 1, // Members.Count 投影：创建者即第一名成员
                IsOpen = true,     // 旧字段：开闭语义已由 Status 承载
                Status = RoomStatus.Waiting,
                Mode = settings.Mode,
                MapId = settings.MapId,
                KillTarget = settings.KillTarget,
                TimeLimitMinutes = settings.TimeLimitMinutes,
                RoomVersion = 1,
                MatchGeneration = 0,
                // P0-A（2026-09-15）：冻结建房者的应用协议代际——入房者必须一致、实例租用按此筛选；
                // 空白归一 null（旧客户端：只能租未申报协议的旧 DS）
                ExpectedProtocolId = string.IsNullOrWhiteSpace(request.ClientProtocolId) ? null : request.ClientProtocolId.Trim(),
                CreatedAtUtc = now,
                LastHeartbeatUtc = now, // 旧玩家心跳列：仅遗留兼容，不再维护
                StateChangedAtUtc = now,
                Members = [new GameRoomMember
                {
                    UserId = userId,
                    JoinedAtUtc = now,
                    TeamId = GameModes.IsTeamMode(settings.Mode) ? Teams.Red : Teams.None,
                    IsReady = false,
                    LastSeenUtc = now,
                }],
            };
            db.GameRooms.Add(room);
            await db.SaveChangesAsync(cancellationToken);
            // 成员是内联新建的（无 User 导航）：投影用户名前显式加载，避免快照 NRE
            await db.Entry(room.Members[0]).Reference(m => m.User).LoadAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return ToSnapshot(room, userId, null);
        }
        catch (ApiException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<GameRoomDto>> ListAsync(CancellationToken cancellationToken)
    {
        await RunMaintenanceAsync(cancellationToken);

        var rooms = await db.GameRooms.AsNoTracking()
            .Include(x => x.Members)
            .Where(x => x.Status != RoomStatus.Closed && x.Members.Any())
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        return rooms.Select(room => ToDto(room)).ToList();
    }

    public async Task<RoomSnapshotDto> JoinAsync(long userId, string roomCode, string? teamId, string? clientProtocolId, CancellationToken cancellationToken, bool waitingOnly = false, long? invitationId = null)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await TryJoinOnceAsync(userId, normalized, teamId, clientProtocolId, cancellationToken, waitingOnly, invitationId);
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    // 并发加入竞态（UserId 唯一索引/并发令牌拦截）：重跑，重入时走"已是成员"幂等路径
                    db.ChangeTracker.Clear();
                }
            }
        });
    }

    private async Task<RoomSnapshotDto> TryJoinOnceAsync(long userId, string roomCode, string? requestedTeam, string? clientProtocolId, CancellationToken cancellationToken, bool waitingOnly, long? invitationId)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var room = await db.GameRooms
                .Include(x => x.Members).ThenInclude(m => m.User)
                .Include(x => x.ServerInstance)
                .SingleOrDefaultAsync(x => x.RoomCode == roomCode, cancellationToken)
                ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");

            if (invitationId.HasValue)
            {
                var invitation = await db.Set<RoomInvitation>().SingleOrDefaultAsync(i => i.Id == invitationId.Value && i.RecipientId == userId, cancellationToken);
                var alreadyHere = room.Members.Any(m => m.UserId == userId);
                if (invitation == null || invitation.RoomId != room.Id || invitation.ExpiresAtUtc <= DateTime.UtcNow
                    || (invitation.State != "Pending" && !(invitation.State == "Accepted" && alreadyHere))
                    || !room.Members.Any(m => m.UserId == invitation.SenderId)
                    || !await db.Friendships.AnyAsync(f => f.UserId == userId && f.FriendId == invitation.SenderId, cancellationToken))
                    throw new ApiException(409, "INVITE_EXPIRED", "邀请已失效");
                if (await db.GameRoomMembers.AnyAsync(m => m.UserId == userId && m.RoomId != room.Id, cancellationToken))
                    throw new ApiException(409, "INVITE_LEAVE_FIRST", "请先退出当前房间");
                invitation.State = "Accepted";
            }

            if (waitingOnly && room.Status != RoomStatus.Waiting)
                throw new ApiException(409, ApiErrorCodes.RoomStateConflict, "房间已不在等待状态");
            if (room.Status == RoomStatus.Closed)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomClosed, "房间已关闭");

            // P0-A（2026-09-15）：混版本客户端不进同一房——房间冻结协议期望与入房申报不一致即拒绝。
            // 空白归一 null（旧客户端未申报）；旧房（期望 null）对新客户端同样拒绝（无法租到其协议的 DS）。
            var normalizedProtocol = string.IsNullOrWhiteSpace(clientProtocolId) ? null : clientProtocolId.Trim();
            if (!string.Equals(room.ExpectedProtocolId ?? string.Empty, normalizedProtocol ?? string.Empty, StringComparison.Ordinal))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ProtocolMismatch,
                    $"客户端版本与房间不一致（room={room.ExpectedProtocolId ?? "<legacy>"} client={normalizedProtocol ?? "<legacy>"}），请更新后重试");
            }

            var status = RoomStatus.Normalize(room.Status);

            var member = room.Members.FirstOrDefault(x => x.UserId == userId);
            if (status is RoomStatus.Starting or RoomStatus.InMatch or RoomStatus.Returning)
            {
                EnsureInstanceAlive(room);
                if (member is null)
                {
                    if (status != RoomStatus.InMatch)
                        throw new ApiException(StatusCodes.Status409Conflict,
                            status == RoomStatus.Starting ? ApiErrorCodes.RoomStarting : ApiErrorCodes.RoomStateConflict,
                            status == RoomStatus.Starting ? "比赛正在启动，暂不能加入" : "房间正在返房结算，暂不能加入");
                    // InMatch 补人（Docs/26 §2.1）：可加入任一未满队；DS 负责连接期容量终验（Pending 计入名额）
                    await PurgeUserRoomsUnsafeAsync(userId, cancellationToken);
                    member = AddMemberUnsafe(room, userId, requestedTeam, enforceTeamCapacity: true);
                    if (member.User is null)
                        await db.Entry(member).Reference(m => m.User).LoadAsync(cancellationToken); // 新增成员无导航：连接签发前加载
                }
                // roster 成员重进（重连/刷新页面）同样补发新票据：票据一次性，旧票据可能已被消费/过期
                RoomConnectionInfoDto? connection = null;
                if (status is RoomStatus.Starting or RoomStatus.InMatch)
                {
                    // R01：补入者同步写入有效名单（消费按 roster 取队、终局按 roster 校验资格）
                    if (room.CurrentMatchId is not null)
                    {
                        var hasRosterRow = await db.RoomMatchRosters.AnyAsync(
                            x => x.RoomId == room.Id && x.UserId == member.UserId && x.MatchId == room.CurrentMatchId,
                            cancellationToken);
                        if (!hasRosterRow)
                            db.RoomMatchRosters.Add(new RoomMatchRoster
                            {
                                RoomId = room.Id, MatchId = room.CurrentMatchId, UserId = member.UserId,
                                TeamId = member.TeamId, IssuedAtUtc = DateTime.UtcNow,
                            });
                    }
                    connection = await IssueConnectionUnsafe(room, member);
                    await db.SaveChangesAsync(cancellationToken);
                }
                if (member.User is null)
                    await db.Entry(member).Reference(m => m.User).LoadAsync(cancellationToken); // 新增成员无导航：投影前加载
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return ToSnapshot(room, userId, connection);
            }

            // Waiting：成员/新成员统一走选边入房；已是成员（重复点击）零改动幂等
            if (member is null)
            {
                // 一人一房（F13）：仅当并非目标房成员时才清理旧房，同房重进不得触发 purge
                await PurgeUserRoomsUnsafeAsync(userId, cancellationToken);
                if (room.Members.Count >= room.MaxPlayers)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomFull, "房间已满");
                member = AddMemberUnsafe(room, userId, requestedTeam, enforceTeamCapacity: true);
            }
            await db.SaveChangesAsync(cancellationToken);
            var joinedNew = member.User is null;
            if (joinedNew)
                await db.Entry(member).Reference(m => m.User).LoadAsync(cancellationToken); // 新增成员无导航：投影前加载
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            if (joinedNew) chat.SendSystem(room.RoomCode, $"{member.User.Username} 加入了房间");
            return ToSnapshot(room, userId, null);
        }
        catch (ApiException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// 房间详情（Docs/27 §5.3）：先跑懒维护再出快照；Starting/InMatch 为成员补发连接票据。
    /// R01：签发必须落库（原 AsNoTracking 直返导致票据从未持久化、跨请求不可消费）；
    /// Returning 不签战斗票（终局后无需 DS 连接，结果经 §7.3 HTTP 拉取）。
    /// </summary>
    public async Task<RoomSnapshotDto> GetDetailAsync(long userId, string roomCode, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        await RunMaintenanceAsync(cancellationToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () => await ExecuteInTransactionAsync(async () =>
        {
            var (room, member) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken, includeInstance: true);

            RoomConnectionInfoDto? connection = null;
            var status = RoomStatus.Normalize(room.Status);
            if ((status is RoomStatus.Starting or RoomStatus.InMatch)
                && await db.RoomMatchRosters.AnyAsync(x => x.RoomId == room.Id
                    && x.MatchId == room.CurrentMatchId && x.UserId == userId, cancellationToken))
            {
                EnsureInstanceAlive(room);
                connection = await IssueConnectionUnsafe(room, member);
                await db.SaveChangesAsync(cancellationToken);
            }
            return ToSnapshot(room, userId, connection);
        }, cancellationToken));
    }

    public async Task<RoomSnapshotDto> SetTeamAsync(long userId, string roomCode, string teamId, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () => await ExecuteInTransactionAsync(async () =>
        {
            var (room, member) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken);
            RequireStatus(room, RoomStatus.Waiting, "只能在等待阶段换队");
            if (!GameModes.IsTeamMode(room.Mode) || teamId is not (Teams.Red or Teams.Blue))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.TeamInvalid, "该模式不支持选择此队伍");
            if (member.TeamId != teamId)
            {
                var teamCount = room.Members.Count(x => x.TeamId == teamId);
                if (teamCount >= RoomSettingRules.PerTeamCapacity(room.MaxPlayers))
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.TeamFull, "该队伍已满");
                // 换队取消本人准备（Docs/26 §2.1）；房间可见变更递增版本（复审 R06：防前端按版本跳过快照漏变化）
                member.TeamId = teamId;
                member.IsReady = false;
                room.RoomVersion++;
                await db.SaveChangesAsync(cancellationToken);
                // R05 换队可见水位：此后只可见"本次加入该队之后"的队聊（旧队历史隔离）
                chat.MarkTeamSwitch(normalized, userId, teamId);
            }
            return ToSnapshot(room, userId, null);
        }, cancellationToken));
    }

    public async Task<RoomSnapshotDto> SetReadyAsync(long userId, string roomCode, bool isReady, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () => await ExecuteInTransactionAsync(async () =>
        {
            var (room, member) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken);
            RequireStatus(room, RoomStatus.Waiting, "只能在等待阶段改变准备状态");
            if (member.IsReady != isReady)
            {
                // 准备变更是房间可见变更（复审 R06）：递增版本，防前端按版本跳过快照漏变化
                member.IsReady = isReady;
                room.RoomVersion++;
                await db.SaveChangesAsync(cancellationToken);
            }
            return ToSnapshot(room, userId, null);
        }, cancellationToken));
    }

    public async Task<RoomSnapshotDto> UpdateSettingsAsync(long userId, string roomCode, RoomSettingsRequest request, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () => await ExecuteInTransactionAsync(async () =>
        {
            var (room, member) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken);
            if (room.HostUserId != member.UserId)
                throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.NotLeader, "只有房主可以修改房间设置");
            RequireStatus(room, RoomStatus.Waiting, "只能在等待阶段修改房间设置");
            // null = 保持不变；任一白名单外取值整体 422（Docs/27 §5.4）
            var settings = RoomSettingRules.Validate(
                request.Mode ?? room.Mode, request.MapId ?? room.MapId,
                request.KillTarget ?? room.KillTarget, request.TimeLimitMinutes ?? room.TimeLimitMinutes,
                request.MaxPlayers ?? room.MaxPlayers);
            if (room.Members.Count > settings.MaxPlayers)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "房间人数超过所选容量");
            if (room.Mode != settings.Mode)
            {
                var ordered = room.Members.OrderBy(x => x.JoinedAtUtc).ThenBy(x => x.UserId).ToArray();
                for (var i = 0; i < ordered.Length; i++)
                    ordered[i].TeamId = GameModes.IsTeamMode(settings.Mode)
                        ? (i % 2 == 0 ? Teams.Red : Teams.Blue) : Teams.None;
            }
            if (GameModes.IsTeamMode(settings.Mode) && room.Members.GroupBy(x => x.TeamId)
                .Any(g => g.Count() > RoomSettingRules.PerTeamCapacity(settings.MaxPlayers)))
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.TeamFull, "现有队伍人数超过所选容量");
            room.Mode = settings.Mode;
            room.MapId = settings.MapId;
            room.KillTarget = settings.KillTarget;
            room.TimeLimitMinutes = settings.TimeLimitMinutes;
            room.MaxPlayers = settings.MaxPlayers;
            room.RoomVersion++; // 设置变更递增版本（Docs/27 §1）
            foreach (var m in room.Members) m.IsReady = false; // 修改后清空所有准备（Docs/26 §2.1）
            await db.SaveChangesAsync(cancellationToken);
            chat.SendSystem(room.RoomCode, "房主更新了房间设置，全员准备已清空");
            return ToSnapshot(room, userId, null);
        }, cancellationToken));
    }

    /// <summary>
    /// 房主开始（Docs/27 §5.5）：开局条件校验 → 原子租 DS + 建 matchId/名单快照 + 签发起者票据 → Starting。
    /// 无可用 DS → 409 NO_SERVER_AVAILABLE，保持 Waiting 且保留全部准备（允许重试）。
    /// Starting 状态的重复调用幂等：返回同一比赛的重建响应，不重租不重发名单。
    /// </summary>
    public async Task<StartMatchDto> StartAsync(long userId, string roomCode, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        var rearmDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await TryStartOnceAsync(userId, normalized, cancellationToken);
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    db.ChangeTracker.Clear(); // 租用竞态：重跑后租用查询会跳过被抢占的实例
                }
                catch (ApiException ex) when (ex.Code == ApiErrorCodes.NoServerAvailable
                    && DateTime.UtcNow < rearmDeadline)
                {
                    // A solo match may still be disconnecting or proving Ready/0 after
                    // its room closes. Do not hold the transaction while waiting.
                    db.ChangeTracker.Clear();
                    var pool = await instances.GetPoolDiagnosticsAsync(0, cancellationToken);
                    if (!pool.Instances.Any(x => x.Fresh && (x.State == InstanceState.Draining
                        || (x.State == InstanceState.InMatch || x.State == InstanceState.Reserved)
                        && x.CurrentPlayers <= 1))) throw;
                    await Task.Delay(250, cancellationToken);
                }
            }
        });
    }

    private async Task<StartMatchDto> TryStartOnceAsync(long userId, string roomCode, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var (room, member) = await LoadRoomAndMemberAsync(roomCode, userId, cancellationToken,
                includeInstance: true, includeRosters: true);
            if (room.HostUserId != member.UserId)
                throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.NotLeader, "只有房主可以开始比赛");
            var status = RoomStatus.Normalize(room.Status);

            if (status == RoomStatus.Starting && room.CurrentMatchId is not null)
            {
                // 幂等重入（重复点击）：同一比赛，补发发起者票据，不重租实例
                EnsureInstanceAlive(room);
                var connection = await IssueConnectionUnsafe(room, member);
                var roster = room.Rosters.Where(x => x.MatchId == room.CurrentMatchId)
                    .Select(x => new StartRosterEntryDto(x.UserId, x.TeamId)).ToArray();
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return ToStartMatchDto(room, connection, roster);
            }
            if (status != RoomStatus.Waiting)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "房间当前不可开始比赛");

            // The host may start alone. Only the host and ready guests enter this roster.
            var participants = room.Members.Where(x => x.UserId == room.HostUserId || x.IsReady).ToArray();
            if (participants.Any(x => GameModes.IsTeamMode(room.Mode)
                ? x.TeamId is not (Teams.Red or Teams.Blue) : x.TeamId != Teams.None))
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "玩家队伍与比赛模式不一致，请重新选择模式");

            // 原子租用（沿用 Docs/27 旧 §2.4 纪律 + P0-A 协议筛选 + Phase 8 地图匹配）：只租心跳新鲜、
            // 容量足够、应用协议与房间冻结期望一致、且绑定地图与房间 mapId 一致的 Ready 实例；
            // 没有 → 保持 Waiting。旧 DS 未上报地图（MapId=null）按 arena 兼容处理。
            var now = DateTime.UtcNow;
            var instance = await db.ServerInstances
                .Where(x => x.State == InstanceState.Ready
                         && x.LastHeartbeatUtc >= now - instances.InstanceTtl
                         && x.Capacity >= room.MaxPlayers
                         && x.ProtocolId == room.ExpectedProtocolId
                         && (x.MapId ?? MapCatalog.DefaultMapId) == room.MapId)
                .OrderByDescending(x => x.LastHeartbeatUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (instance is null)
            {
                var pool = await instances.GetPoolDiagnosticsAsync(room.MaxPlayers, cancellationToken, room.ExpectedProtocolId);
                logger.LogWarning(
                    "[ServerRegistry] START_NO_SERVER reason={Reason} room={RoomCode} requestedCapacity={RequestedCapacity} readyFresh={ReadyFresh} reserved={Reserved} inMatch={InMatch} protocol={Protocol}",
                    ServerInstanceService.ClassifyNoServer(pool.Summary), room.RoomCode, room.MaxPlayers,
                    pool.Summary.ReadyFresh, pool.Summary.Reserved, pool.Summary.InMatch, room.ExpectedProtocolId ?? "<legacy>");
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.NoServerAvailable, "当前没有可用的对战服务器，请稍后再试");
            }

            var matchId = Guid.NewGuid().ToString("N");
            room.MatchGeneration++;
            room.CurrentMatchId = matchId;
            room.Status = RoomStatus.Starting;
            room.RoomVersion++;
            room.StateChangedAtUtc = now;
            foreach (var m in participants)
                db.RoomMatchRosters.Add(new RoomMatchRoster
                {
                    RoomId = room.Id, MatchId = matchId, UserId = m.UserId,
                    TeamId = m.TeamId, IssuedAtUtc = now,
                });
            instance.State = InstanceState.Reserved;
            instance.RoomCode = room.RoomCode;
            instance.CurrentPlayers = participants.Length;
            instance.Version++; // 并发令牌：两个 Start 同时租用同一实例时，后落库者必须撞异常重试
            room.ServerInstance = instance; // 绑定租约（旧实现构造房间时挂导航；拆分 start 后必须显式回绑）
            logger.LogInformation("[ServerRegistry] INSTANCE_LEASED room={RoomCode} instance={InstanceId} state=Reserved",
                room.RoomCode, instance.InstanceId);
            var leaderConnection = await IssueConnectionUnsafe(room, member);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("[RoomFlow] MATCH_STARTED room={RoomCode} match={MatchId} generation={Generation} players={Players}",
                room.RoomCode, matchId, room.MatchGeneration, room.Members.Count);
            chat.SendSystem(room.RoomCode, "比赛开始，正在进入战场");
            var rosterEntries = room.Rosters
                .Where(x => x.MatchId == matchId)
                .Select(x => new StartRosterEntryDto(x.UserId, x.TeamId))
                .ToArray();
            return ToStartMatchDto(room, leaderConnection, rosterEntries);
        }
        catch (ApiException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>成员保活心跳（Docs/27 §2）：刷新 LastSeenUtc；非成员无副作用（兼容旧端点）。</summary>
    public async Task HeartbeatAsync(long userId, CancellationToken cancellationToken)
    {
        var membership = await db.GameRoomMembers
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (membership is null) return;
        membership.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        // 好友在线状态信号（2026-09-20 需求2）：等待房间 15s 心跳兼作 LastSeenUtc 刷新（节流单条 UPDATE）
        if (db.Database.IsRelational())
        {
            var threshold = DateTime.UtcNow - TimeSpan.FromSeconds(30);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE UserAccount SET LastSeenUtc = {DateTime.UtcNow} WHERE Id = {userId} AND (LastSeenUtc IS NULL OR LastSeenUtc < {threshold})",
                cancellationToken);
        }
    }

    public async Task LeaveAsync(long userId, CancellationToken cancellationToken)
    {
        // 显式事务 + 幂等；成员计数以 Members 为真相（Phase E 模式沿用）
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var membership = await db.GameRoomMembers
                    .Include(x => x.Room).ThenInclude(r => r.Members)
                    .Include(x => x.Room).ThenInclude(r => r.ServerInstance)
                    .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
                if (membership is null)
                {
                    // 幂等：未在任何房间（重复离开/未加入）
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return;
                }
                var leftRoom = membership.Room;
                var leftName = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, cancellationToken)).Username;
                await RemoveMemberUnsafeAsync(membership, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                chat.SendSystem(leftRoom.RoomCode, $"{leftName} 离开了房间");
                if (leftRoom.Members.Count == 0 && RoomStatus.Normalize(leftRoom.Status) != RoomStatus.InMatch)
                    chat.DropRoom(leftRoom.RoomCode);
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

    /// <summary>
    /// 服务器上报玩家掉线（语义不变，见原实现）：真实移除与幂等 no-op 均返回处置后权威事实。
    /// </summary>
    public async Task<ServerPlayerDisconnectReportDto> ReportPlayerDisconnectAsync(string instanceId, ServerPlayerDisconnectReportRequest request,
        DateTime? noticeStartedAtUtc = null, CancellationToken cancellationToken = default)
    {
        var noticeStart = noticeStartedAtUtc ?? DateTime.UtcNow;
        var roomCode = request.RoomCode?.Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var instance = await db.ServerInstances.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.InstanceId == instanceId.Trim(), cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.ServerInstanceNotFound, "实例未注册");

                if (string.IsNullOrEmpty(roomCode) || instance.RoomCode != roomCode)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ServerInstanceStateConflict,
                        "实例未绑定该房间，拒绝移除成员");

                var room = await db.GameRooms
                    .Include(x => x.Members)
                    .Include(x => x.ServerInstance) // ReleaseInstanceUnsafe 需要 ServerInstance 导航（ChangeTracker.Clear 后必须显式加载）
                    .SingleOrDefaultAsync(x => x.RoomCode == roomCode, cancellationToken);
                if (room is null)
                {
                    // 绑定行指向已不存在的房间：实例转入 Draining（A03：DS 重臂确认前不可租），
                    // 但对 DS 返回 Ready+0——这是 DS 清绑定/重臂的握手信号，DS 重臂心跳随后权威回池
                    var trackedInstance = await db.ServerInstances
                        .SingleOrDefaultAsync(x => x.InstanceId == instanceId.Trim(), cancellationToken);
                    if (trackedInstance is not null && trackedInstance.RoomCode == roomCode)
                    {
                        trackedInstance.State = InstanceState.Draining;
                        trackedInstance.Version++;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "[ServerRegistry] PLAYER_DISCONNECT_ROOM_GONE_INSTANCE_DRAINING instance={InstanceId} room={RoomCode}",
                        instanceId, roomCode);
                    return new ServerPlayerDisconnectReportDto(roomCode!, 0, InstanceState.Ready);
                }

                var membership = room.Members.FirstOrDefault(x => x.UserId == request.UserId);
                // R1：解析上报会话的票据行（含签发时冻结的比赛身份）——战斗退场与成员移除的判别依据
                var sessionTicket = request.SessionId > 0
                    ? await db.ServerJoinTickets.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == request.SessionId
                            && x.ServerInstanceId == instance.Id
                            && x.RoomCode == roomCode
                            && x.UserId == request.UserId, cancellationToken)
                    : null;
                var reportedSessionConsumedAt = sessionTicket?.ConsumedAtUtc;
                var newerConsumedSessionExists = reportedSessionConsumedAt != null
                    && await db.ServerJoinTickets.AsNoTracking()
                        .AnyAsync(x => x.ServerInstanceId == instance.Id
                            && x.RoomCode == roomCode
                            && x.UserId == request.UserId
                            && x.ConsumedAtUtc > reportedSessionConsumedAt, cancellationToken);
                var reportedSessionMissing = request.SessionId > 0 && sessionTicket is null;
                if (membership is null && sessionTicket?.ConsumedAtUtc is null
                    || membership is not null && membership.JoinedAtUtc > noticeStart
                    || newerConsumedSessionExists || reportedSessionMissing)
                {
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "[ServerRegistry] PLAYER_DISCONNECT_NOOP instance={InstanceId} room={RoomCode} user={UserId} reason={Reason} remaining={Remaining} state={State}",
                        instanceId, roomCode, request.UserId,
                        membership is null ? "member-absent"
                            : newerConsumedSessionExists ? "newer-consumed-session"
                            : reportedSessionMissing ? "reported-session-missing"
                            : "rejoined-after-notice-start",
                        room.Members.Count, instance.State);
                    return new ServerPlayerDisconnectReportDto(roomCode!, room.Members.Count, instance.State);
                }

                // R1（审计修复）：战斗连接退场与房间成员资格分离。成员移除只允许
                // 「比赛进行中（Starting/InMatch）且上报会话属于当前局」的真实掉线；其余一律战斗退场：
                // ① Returning（终局返房窗口）：连接断开 = 战斗退场，绝不删成员（成员/队长保留，可再开下一局）；
                // ② 上报会话属于旧局（票据 matchId ≠ 当前局）：迟到旧局上报，不得触碰成员与下一局；
                // ③ 成员在本局票据消费之后重新进房（JoinedAtUtc > ConsumedAtUtc）：旧战斗会话已过期。
                var statusNow = RoomStatus.Normalize(room.Status);

                // HTTP leave removes membership before the DS disconnect arrives.
                // A consumed ticket for this match is still authoritative departure
                // evidence; an empty InMatch room must not retain the lease forever.
                if (membership is null && sessionTicket?.ConsumedAtUtc != null
                    && string.Equals(sessionTicket.MatchId, room.CurrentMatchId, StringComparison.Ordinal)
                    && room.Members.Count == 0)
                {
                    ReleaseMatchArchiveUnsafe(room);
                    db.GameRooms.Remove(room);
                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    chat.DropRoom(roomCode!);
                    // Permit rearm only. Database remains Draining until the DS has
                    // actually cleared all connections and sends Ready/0.
                    return new ServerPlayerDisconnectReportDto(roomCode!, 0, InstanceState.Ready);
                }

                // F1 重试口：最后一条断线上报若 HTTP 响应丢失，DS 会用同一 session 重试；
                // 此时房间已经 Waiting、CurrentMatchId 已清空，不能把已确认的旧局退场误报成
                // 普通 Waiting 成员水位。仅当旧票据身份、旧局名单全员 Left、且绑定实例仍
                // Draining 时再次返回 Ready/0 释放许可；绝不直接翻 Ready，也不匹配新局。
                if (statusNow == RoomStatus.Waiting
                    && sessionTicket is not null
                    && !string.IsNullOrEmpty(room.LastMatchId)
                    && string.Equals(sessionTicket.MatchId, room.LastMatchId, StringComparison.Ordinal)
                    && instance.State == InstanceState.Draining
                    && instance.RoomCode == roomCode
                    && await HasFullyExitedRosterAsync(room, room.LastMatchId, cancellationToken))
                {
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "[ServerRegistry] PLAYER_DISCONNECT_RETRY_RELEASE_PERMIT instance={InstanceId} room={RoomCode} user={UserId} match={MatchId}（旧局已全清：重复返回 Ready/0，实例仍 Draining）",
                        instanceId, roomCode, request.UserId, room.LastMatchId);
                    return new ServerPlayerDisconnectReportDto(roomCode!, 0, InstanceState.Ready);
                }

                var removalPermitted = membership is not null && statusNow is RoomStatus.Starting or RoomStatus.InMatch
                    && (sessionTicket is null
                        || string.Equals(sessionTicket.MatchId, room.CurrentMatchId, StringComparison.Ordinal))
                    && (reportedSessionConsumedAt is null || membership.JoinedAtUtc <= reportedSessionConsumedAt);
                if (!removalPermitted)
                {
                    var exitedMatchId = sessionTicket?.MatchId ?? room.CurrentMatchId;
                    var released = false;
                    if (!string.IsNullOrEmpty(exitedMatchId)
                        && string.Equals(exitedMatchId, room.CurrentMatchId, StringComparison.Ordinal))
                    {
                        // 战斗退场：只写入真实连接离开的 LeftAtUtc；客户端返房 ack 的 ReturnedAtUtc
                        // 不能代替断线事实。全员真实离场后才结束返房窗口；实例仍保持 Draining，
                        // 由 DS 完成重臂心跳后才真正回池。
                        // 纪律同 ReturnAsync：先落库本人退场标记再查全员水位——EF 查询看不到未提交变更，
                        // 否则最后一人的退场永远看不到自己（探针实证的 archive 不触发根因）。
                        var rosterRow = await db.RoomMatchRosters
                            .SingleOrDefaultAsync(x => x.RoomId == room.Id && x.UserId == request.UserId
                                && x.MatchId == exitedMatchId, cancellationToken);
                        if (rosterRow is not null && rosterRow.LeftAtUtc is null)
                            rosterRow.LeftAtUtc = DateTime.UtcNow;
                        if (statusNow == RoomStatus.Returning)
                        {
                            // 同时收口“开局名单存在但票据从未 consume”的成员；这类成员没有
                            // DS 连接，永远不会自己产生 disconnect report。
                            var allBattleDone = await MarkNeverConnectedRosterRowsLeftAsync(
                                room, DateTime.UtcNow, cancellationToken);
                            if (allBattleDone)
                            {
                                ReturnToWaitingUnsafe(room, DateTime.UtcNow);
                                released = true;
                                await db.SaveChangesAsync(cancellationToken);
                            }
                        }
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    if (released)
                    {
                        logger.LogInformation(
                            "[ServerRegistry] PLAYER_BATTLE_EXIT_RELEASED instance={InstanceId} room={RoomCode} user={UserId}（战斗连接全清+返房窗口结束：实例保持 Draining，等待 DS 重臂）",
                            instanceId, roomCode, request.UserId);
                        // Ready/0 = DS 清绑定/重臂的释放许可；数据库实例仍为 Draining，
                        // 必须收到 DS 真实 Ready+0 重臂心跳后才翻回 Ready。
                        return new ServerPlayerDisconnectReportDto(roomCode!, 0, InstanceState.Ready);
                    }
                    // 未释放：上报名单未离场水位（剩余战斗连接），DS 绑定保持（fail closed）
                    var battleRemaining = string.IsNullOrEmpty(room.CurrentMatchId)
                        ? room.Members.Count
                        : await db.RoomMatchRosters.CountAsync(
                            x => x.RoomId == room.Id && x.MatchId == room.CurrentMatchId
                               && x.LeftAtUtc == null, cancellationToken);
                    logger.LogInformation(
                        "[ServerRegistry] PLAYER_BATTLE_EXIT instance={InstanceId} room={RoomCode} user={UserId} status={Status} remaining={Remaining} state={State}（战斗退场：成员资格保留）",
                        instanceId, roomCode, request.UserId, statusNow, battleRemaining,
                        room.ServerInstance?.State ?? instance.State);
                    return new ServerPlayerDisconnectReportDto(roomCode!, battleRemaining,
                        room.ServerInstance?.State ?? instance.State);
                }

                var wasLeader = room.HostUserId == membership!.UserId;
                await RemoveMemberUnsafeAsync(membership!, cancellationToken, allowReleaseDuringMatch: true);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                // R1 契约（CFAuditFixTests 锁定，与上方战斗退场路径一致）：全员退场（房间随最后成员删除）
                // → 响应一律返回释放许可 Ready/0——这是 DS 唯一认可的清绑定/重臂信号；数据库实例保持
                // Draining，由 DS 重臂心跳权威回池。返回实际 DB 状态 Draining 会让 DS 滞留旧绑定
                //（DS 侧只认 Ready+0），两条释放路径语义分裂。
                bool roomClosedByLastMember = room.Members.Count == 0;
                var reportedInstanceState = roomClosedByLastMember
                    ? InstanceState.Ready
                    : (room.ServerInstance?.State ?? instance.State);
                logger.LogInformation(
                    "[ServerRegistry] PLAYER_DISCONNECT_REMOVED instance={InstanceId} room={RoomCode} user={UserId} wasLeader={WasLeader} remaining={Remaining} state={Reported}",
                    instanceId, roomCode, request.UserId, wasLeader, room.Members.Count, reportedInstanceState);
                return new ServerPlayerDisconnectReportDto(roomCode!, room.Members.Count, reportedInstanceState);
            }
            catch (ApiException)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    // ===== 终局与返房（Docs/27 §7，Q05）=====

    /// <summary>
    /// 玩家返房 ack（Docs/27 §5.7 + 复审 R03）：客户端 ack 只确认"已由服务器可信终局驱动"的返房，
    /// 绝不创建终局、绝不释放活跃比赛租约（终局与释放归 DS 权威上报链 ReportMatchResultAsync）。
    /// 任何幂等返回之前都先校验比赛身份与成员资格。ack 只写 ReturnedAtUtc；必须等每条
    /// 战斗连接真实断线（LeftAtUtc）后才回 Waiting，实例在 DS 重臂前保持 Draining。
    /// </summary>
    public async Task<RoomSnapshotDto> ReturnAsync(long userId, string roomCode, string matchId, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () => await ExecuteInTransactionAsync(async () =>
        {
            var (room, _) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken, includeInstance: false);
            var status = RoomStatus.Normalize(room.Status);

            // 比赛身份校验先于一切幂等分支：旧代 ack 不得触碰新局
            if (room.CurrentMatchId != matchId && room.LastMatchId != matchId)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "比赛身份与当前房间不符");

            if (status == RoomStatus.Waiting)
            {
                // 已回 Waiting（真实战斗连接全部退场）：同局迟到 ack 幂等安全，零副作用
                return ToSnapshot(room, userId, null);
            }
            if (status != RoomStatus.Returning)
            {
                // InMatch/Starting：比赛未被服务器终局，客户端 ack 不能提前结束战斗或释放 DS
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict,
                    "比赛尚未由服务器终局，暂不能返房");
            }

            // Returning：记录本成员 ack。即使全员 ack，也必须等待 DS 真实连接退场；
            // ack 不得清 CurrentMatchId、不得释放/Ready 实例。
            // 注意先落库本人 ack 再查全员水位，否则最后一人的 ack 永远看不到自己。
            var rosterRow = await db.RoomMatchRosters.SingleOrDefaultAsync(
                x => x.RoomId == room.Id && x.UserId == userId && x.MatchId == matchId, cancellationToken);
            if (rosterRow is not null && rosterRow.ReturnedAtUtc is null)
                rosterRow.ReturnedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("[RoomFlow] MATCH_RETURN_ACK room={RoomCode} match={MatchId} user={UserId} (ack=true; waiting for real disconnect facts)",
                room.RoomCode, matchId, userId);
            await db.SaveChangesAsync(cancellationToken);
            return ToSnapshot(room, userId, null);
        }, cancellationToken));
    }

    /// <summary>
    /// DS 权威终局上报（Docs/27 §7.2，X-Server-Key）：按 matchId 幂等登记 RoomMatchResult，
    /// InMatch→Returning + 释放实例；对 rewardEligible 玩家经 MatchService 逐个结算
    /// （ClientMatchId=ds-{matchId}-{userId} 幂等，失败保留 RewardsAppliedAtUtc=null，重复上报自动补偿）。
    /// 复审 R04：结果持久不可变——首次登记校验来源实例/名单（roster 交集、去重、队伍一致）并记录来源身份；
    /// 重放必须同源且内容一致（改动 winner/players 明确冲突 409）；奖励重试只消费已持久化的结果快照，
    /// 与玩家当前是否在房间无关。
    /// </summary>
    public async Task<MatchResultReportDto> ReportMatchResultAsync(string instanceId, MatchResultReportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WinnerTeam is not null && request.WinnerTeam is not (Teams.Red or Teams.Blue)
            && !string.IsNullOrWhiteSpace(request.WinnerTeam))
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.TeamInvalid, "胜队标识非法");
        // A01（V0）：胜队标识归一——空/空白与 null 同语义 = 平局/无胜队（KillRace、TDM 平局），
        // 合法无胜队可进入可信终局；非空白非法值仍 422。DS 侧同步改为序列化 null（不填空串）。
        string? winnerTeam = string.IsNullOrWhiteSpace(request.WinnerTeam) ? null : request.WinnerTeam;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            GameRoom? room = null;
            RoomMatchResult? existing;
            try
            {
                var instance = await db.ServerInstances.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.InstanceId == instanceId.Trim(), cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.ServerInstanceNotFound, "实例未注册");

                // R3（审计修复）：已持久结果的重放/补偿只按 matchId + 原始来源 + 内容一致性校验，
                // 与活跃房间（CurrentMatchId/LastMatchId）完全脱钩——房间已删除或多局之后历史比赛
                // 仍可进入补偿路径。首次登记仍要求房间锚点（名单/状态/实例绑定校验）。
                existing = await db.RoomMatchResults.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.MatchId == request.MatchId, cancellationToken);
                if (existing is null)
                {
                    // 按比赛找房（而非实例绑定）：首次登记后实例即被释放回池，重放上报不能依赖绑定关系
                    room = await db.GameRooms
                        .Include(x => x.Members)
                        .Include(x => x.ServerInstance)
                        .SingleOrDefaultAsync(x => x.CurrentMatchId == request.MatchId || x.LastMatchId == request.MatchId, cancellationToken)
                        ?? throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "终局与房间当前比赛不符");
                    // 首次登记要求上报者就是该房绑定的实例（防他房 DS 伪造）
                    if (instance.RoomCode != room.RoomCode)
                        throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ServerInstanceStateConflict, "实例未绑定该房间，拒绝登记终局");
                    var status = RoomStatus.Normalize(room.Status);
                    if (status is not (RoomStatus.InMatch or RoomStatus.Returning or RoomStatus.Starting))
                        throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "房间状态不可登记终局");
                    // 名单校验（复审 R04 + R2 胜负一致性）：逐玩家去重、必须在该局名单中、队伍与名单一致
                    var rosterRows = await db.RoomMatchRosters.AsNoTracking()
                        .Where(x => x.RoomId == room.Id && x.MatchId == request.MatchId)
                        .ToDictionaryAsync(x => x.UserId, x => x.TeamId, cancellationToken);
                    ValidateReportedRoster(request, rosterRows, winnerTeam);

                    db.RoomMatchResults.Add(new RoomMatchResult
                    {
                        MatchId = request.MatchId,
                        RoomId = room.Id,
                        WinnerTeam = winnerTeam,
                        DurationSeconds = request.DurationSeconds,
                        EndedAtUtc = DateTime.UtcNow,
                        PlayersJson = JsonSerializer.Serialize(request.Players),
                        ReportedByInstanceId = instance.InstanceId,
                    });
                    if (status is RoomStatus.InMatch or RoomStatus.Starting)
                    {
                        // DS 终局上报是权威事实：Starting 窗口内收到终局（心跳 InMatch 未及同步）同样成立
                        room.Status = RoomStatus.Returning;
                        room.RoomVersion++;
                        room.StateChangedAtUtc = DateTime.UtcNow;
                        MarkInstanceDrainingUnsafe(room); // A03（V0）：退役中不可租，DS 重臂心跳权威回池
                    }
                    await db.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    // 重放一致性（复审 R04 + R3）：必须同一来源实例，且内容与已登记结果一致（防改写历史）；
                    // 不再要求房间仍在/仍是最近局——历史比赛的补偿重放由此获得稳定入口
                    if (!string.Equals(existing.ReportedByInstanceId, instance.InstanceId, StringComparison.Ordinal))
                        throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ServerInstanceStateConflict,
                            "终局重放来源实例与首次登记不符，拒绝");
                    if (existing.WinnerTeam != winnerTeam || existing.DurationSeconds != request.DurationSeconds
                        || !string.Equals(existing.PlayersJson, JsonSerializer.Serialize(request.Players), StringComparison.Ordinal))
                        throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict,
                            "终局重放内容与已登记结果不一致，拒绝");
                }
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            catch (ApiException)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            catch (DbUpdateException)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            // 奖励在状态事务之外逐玩家发放：SubmitAsync 自带事务与幂等，避免嵌套事务。
            // 复审 R04：奖励只消费已持久化的结果快照（不采信本次请求载荷），玩家是否仍在房间不影响补偿。
            var stored = await db.RoomMatchResults.AsNoTracking()
                .SingleOrDefaultAsync(x => x.MatchId == request.MatchId, cancellationToken)
                ?? throw new ApiException(StatusCodes.Status500InternalServerError, ApiErrorCodes.ServerError,
                    "终局结果登记后不可见，拒绝发放奖励");
            var rewardsApplied = await ApplyResultRewardsAsync(stored, cancellationToken);
            if (rewardsApplied)
            {
                var resultRow = await db.RoomMatchResults.SingleOrDefaultAsync(x => x.MatchId == request.MatchId, cancellationToken);
                if (resultRow is not null && resultRow.RewardsAppliedAtUtc is null)
                {
                    resultRow.RewardsAppliedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            logger.LogInformation("[RoomFlow] MATCH_RESULT room={RoomCode} match={MatchId} winner={WinnerTeam} replayed={Replayed} rewards={Rewards}",
                room?.RoomCode ?? "(archived)", request.MatchId, winnerTeam ?? "draw", existing is not null, rewardsApplied);
            return new MatchResultReportDto(request.MatchId, "Final", existing is not null, rewardsApplied);
        });
    }

    /// <summary>
    /// R04 名单校验：重复用户/非名单用户/队伍与名单不符一律 422 拒绝（登记前零副作用）。
    /// R2（审计修复）胜负一致性（模式无关）：有胜队时 isWin 玩家必须属于胜队；
    /// 无胜队（KillRace 个人胜者/TDM 平局）时 isWin=true 的玩家至多一名（唯一个人胜者）。
    /// </summary>
    private static void ValidateReportedRoster(MatchResultReportRequest request, Dictionary<long, string> rosterRows, string? winnerTeam)
    {
        var seen = new HashSet<long>();
        var winClaims = 0;
        foreach (var player in request.Players)
        {
            if (!seen.Add(player.UserId))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MatchPayloadRejected,
                    $"终局名单存在重复用户 {player.UserId}");
            if (!rosterRows.TryGetValue(player.UserId, out var rosterTeam))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MatchPayloadRejected,
                    $"用户 {player.UserId} 不在该局名单中");
            if (!string.Equals(rosterTeam, player.TeamId, StringComparison.Ordinal))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MatchPayloadRejected,
                    $"用户 {player.UserId} 队伍与名单不符");
            if (player.IsWin == true)
            {
                winClaims++;
                if (winnerTeam is not null && !string.Equals(winnerTeam, player.TeamId, StringComparison.Ordinal))
                    throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MatchPayloadRejected,
                        $"用户 {player.UserId} 标记为胜者但与胜队 {winnerTeam} 不符");
            }
        }
        if (winnerTeam is null && winClaims > 1)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MatchPayloadRejected,
                $"无胜队对局存在 {winClaims} 名个人胜者标记（至多一名）");
    }

    /// <summary>
    /// 对 rewardEligible 玩家逐个套用结算引擎；任一失败返回 false（重复上报会自动补偿重放）。
    /// R04：只消费已持久化的结果快照——奖励恢复与玩家当前是否在房间无关。
    /// </summary>
    private async Task<bool> ApplyResultRewardsAsync(RoomMatchResult result, CancellationToken cancellationToken)
    {
        var players = JsonSerializer.Deserialize<List<MatchPlayerResultDto>>(result.PlayersJson) ?? [];
        var failures = 0;
        foreach (var player in players)
        {
            if (!player.RewardEligible) continue; // 终局前退出/确认掉线者不获奖励（Docs/26 §2.4），战绩仍留结果快照
            try
            {
                // A02（V0）：补偿走服务器内部结算入口——按持久化结果快照核验（登记时已完成
                // roster/来源校验），与玩家当前是否在房间/房间状态脱钩；离房后重试仍恰一次补发。
                // R2（审计修复）：IsWin 优先消费逐玩家胜负（KillRace 个人胜者 winnerTeam=null 时
                // 由 PlayersJson 的 isWin 承载）；旧 DS 上报缺字段回退胜队推导。
                await matches.SubmitServerSettlementAsync(player.UserId, new MatchSubmissionRequest
                {
                    ClientMatchId = $"ds-{result.MatchId}-{player.UserId}",
                    Kills = player.Kills,
                    Deaths = player.Deaths,
                    DurationSeconds = result.DurationSeconds,
                    IsWin = player.IsWin ?? (result.WinnerTeam is not null && result.WinnerTeam == player.TeamId),
                    MatchId = result.MatchId,
                    TeamId = player.TeamId,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogError(ex, "[RoomFlow] RESULT_REWARD_FAILED match={MatchId} user={UserId}", result.MatchId, player.UserId);
            }
        }
        return failures == 0;
    }

    /// <summary>玩家侧终局结果查询（Docs/27 §7.3）：TDM 读权威快照；KillRace 按 MatchRecord 聚合；未终局返回 Pending。</summary>
    public async Task<RoomMatchResultViewDto> GetMatchResultAsync(long userId, string roomCode, string matchId, CancellationToken cancellationToken)
    {
        var normalized = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var (room, _) = await LoadRoomAndMemberAsync(normalized, userId, cancellationToken);
        // 只允许查本房当前局/最近一局（防跨房窥探与伪造）
        if (room.CurrentMatchId != matchId && room.LastMatchId != matchId)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, "终局与本房间不符");
        return await BuildMatchResultViewAsync(matchId, cancellationToken);
    }

    /// <summary>
    /// 聊天上下文（Docs/27 §8.4 传输切换）：成员准入；InMatch 双向走战斗 RPC，HTTP 收发仅在
    /// Waiting/Starting/Returning 开放（Starting 聊天允许，系统消息为主——契约 §12 开放项 2 采纳为允许）。
    /// </summary>
    public async Task<(GameRoom Room, GameRoomMember Member)> GetChatContextAsync(
        long userId, string roomCode, bool allowSend, CancellationToken cancellationToken)
    {
        var (room, member) = await LoadRoomAndMemberAsync((roomCode ?? string.Empty).Trim().ToUpperInvariant(), userId, cancellationToken);
        if (RoomStatus.Normalize(room.Status) == RoomStatus.InMatch)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict,
                "局内聊天走战斗内 RPC 通道，HTTP 已关闭");
        return (room, member);
    }

    /// <summary>终局视图组装：权威快照优先；无快照时按 MatchRecord 聚合（KillRace 客户端提交路径）。</summary>
    private async Task<RoomMatchResultViewDto> BuildMatchResultViewAsync(string matchId, CancellationToken cancellationToken)
    {
        var result = await db.RoomMatchResults.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MatchId == matchId, cancellationToken);
        if (result is not null)
        {
            var rows = JsonSerializer.Deserialize<List<MatchPlayerResultDto>>(result.PlayersJson) ?? [];
            var userIds = rows.Select(x => x.UserId).ToList();
            var names = await db.Users.AsNoTracking()
                .Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            return new RoomMatchResultViewDto(matchId, "Final", result.WinnerTeam, result.DurationSeconds, rows
                .OrderBy(x => x.TeamId).ThenByDescending(x => x.Kills)
                .Select(x => new RoomMatchResultPlayerViewDto(x.UserId, names.GetValueOrDefault(x.UserId, "?"), x.TeamId,
                    x.Kills, x.Deaths, x.Assists,
                    // R2：逐玩家胜负——KillRace 个人胜者（winnerTeam=null）由 isWin 承载；旧行回退胜队推导
                    x.IsWin ?? (result.WinnerTeam is not null && result.WinnerTeam == x.TeamId)))
                .ToArray());
        }

        // KillRace：roster 成员各自经 /api/matches 提交（带 matchId），按战绩聚合；未提交者缺席
        var records = await db.Matches.AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.MatchId == matchId)
            .OrderByDescending(x => x.Kills)
            .ToListAsync(cancellationToken);
        if (records.Count == 0)
            return new RoomMatchResultViewDto(matchId, "Pending", null, 0, []);

        var roster = await db.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId).ToDictionaryAsync(x => x.UserId, x => x.TeamId, cancellationToken);
        // R2：聚合路径的个人胜者 = 唯一最高击杀；并列按平局（无人 isWin）
        var topKills = records.Count > 0 ? records[0].Kills : 0;
        var topCount = records.Count(x => x.Kills == topKills);
        return new RoomMatchResultViewDto(matchId, "Final", null, 0, records
            .Select(x => new RoomMatchResultPlayerViewDto(x.UserId, x.User.Username,
                roster.GetValueOrDefault(x.UserId, Teams.None), x.Kills, x.Deaths, 0,
                IsWin: topCount == 1 && x.Kills == topKills))
            .ToArray());
    }

    // ===== 共享内部工具 =====

    /// <summary>策略上下文内的统一事务包装（读改写接口共用）。</summary>
    private async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var result = await body();
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (ApiException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<(GameRoom Room, GameRoomMember Member)> LoadRoomAndMemberAsync(
        string roomCode, long userId, CancellationToken cancellationToken,
        bool includeInstance = false, bool includeRosters = false)
    {
        var query = db.GameRooms
            .Include(x => x.Members).ThenInclude(m => m.User)
            .AsQueryable();
        if (includeInstance) query = query.Include(x => x.ServerInstance);
        if (includeRosters) query = query.Include(x => x.Rosters);
        var room = await query.SingleOrDefaultAsync(x => x.RoomCode == roomCode, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");
        var member = room.Members.FirstOrDefault(x => x.UserId == userId)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.RoomNotFound, "房间不存在或已过期");
        return (room, member);
    }

    private static void RequireStatus(GameRoom room, string expected, string message)
    {
        if (RoomStatus.Normalize(room.Status) != expected)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomStateConflict, message);
    }

    private static void EnsureInstanceAlive(GameRoom room)
    {
        if (room.ServerInstance is null || room.ServerInstance.LastHeartbeatUtc < DateTime.UtcNow - TimeSpan.FromHours(1))
        {
            // 走到连接分支却没有活实例：维护扫描尚未清到的悬空绑定，按房间已关闭拒绝（不猜地址）
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomClosed, "房间绑定的对战服务器已失联");
        }
    }

    /// <summary>补发一次性票据并组装连接信息（调用方保证 ServerInstance 活跃且 CurrentMatchId 非空）。
    /// R01：签发时冻结比赛身份（消费端严格比对），同键旧未消费票据被顶替。</summary>
    private async Task<RoomConnectionInfoDto> IssueConnectionUnsafe(GameRoom room, GameRoomMember member)
    {
        var (ticket, expiresAt) = await instances.IssueTicketAsync(room.ServerInstance!, room.RoomCode, member.UserId,
            member.User.Username, room.CurrentMatchId, room.MatchGeneration);
        return new RoomConnectionInfoDto(room.ServerInstance!.Address, room.ServerInstance.Port, ticket, expiresAt,
            room.CurrentMatchId!, room.MatchGeneration);
    }

    /// <summary>加入成员（Unsafe=调用方处于事务上下文且已做容量/状态校验）：TDM 自动分配未满队。</summary>
    /// <summary>加入成员（Unsafe=调用方处于事务上下文且已做状态校验）：TDM 自动分配未满队，队满整体拒绝。</summary>
    private GameRoomMember AddMemberUnsafe(GameRoom room, long userId, string? requestedTeam, bool enforceTeamCapacity)
    {
        var teamId = Teams.None;
        if (GameModes.IsTeamMode(room.Mode))
        {
            var perTeam = RoomSettingRules.PerTeamCapacity(room.MaxPlayers);
            if (requestedTeam is Teams.Red or Teams.Blue)
            {
                teamId = requestedTeam;
                if (room.Members.Count(x => x.TeamId == teamId) >= perTeam)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.TeamFull, "该队伍已满");
            }
            else
            {
                var red = room.Members.Count(x => x.TeamId == Teams.Red);
                var blue = room.Members.Count(x => x.TeamId == Teams.Blue);
                if (red >= perTeam && blue >= perTeam)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.RoomFull, "双方队伍均已满");
                teamId = red <= blue && red < perTeam || blue >= perTeam ? Teams.Red : Teams.Blue;
            }
        }
        else if (requestedTeam is Teams.Red or Teams.Blue)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.TeamInvalid, "该模式不支持选择队伍");
        }
        var member = new GameRoomMember
        {
            UserId = userId,
            JoinedAtUtc = DateTime.UtcNow,
            TeamId = teamId,
            IsReady = false,
            LastSeenUtc = DateTime.UtcNow,
            ChatJoinSeq = chat.CurrentSeq(room.RoomCode), // 新成员不补发入房前历史（Docs/27 §8.3）
        };
        room.Members.Add(member);
        room.JoinedPlayers = room.Members.Count;
        return member;
    }

    /// <summary>
    /// 移除单个成员的共享语义（Leave/掉线上报/过期清理共用）：
    /// 普通成员离开仅减员；leader 离开且仍有成员 → 确定性转移 leader（JoinedAtUtc 最早 → UserId 最小）；
    /// 最后一名成员离开且比赛未开始 → 删房间并释放实例；最后一名成员离开但 InMatch → 绑定保留（终局语义归服务器权威链）。
    /// 当前比赛进行中时同步回填开局名单 LeftAtUtc（终局资格判定依据，Docs/26 §2.4）。
    /// </summary>
    private async Task RemoveMemberUnsafeAsync(GameRoomMember membership, CancellationToken cancellationToken,
        bool allowReleaseDuringMatch = false)
    {
        var room = membership.Room;
        var memberRow = room.Members.Single(x => x.UserId == membership.UserId);
        room.Members.Remove(memberRow);
        db.GameRoomMembers.Remove(membership);
        room.JoinedPlayers = room.Members.Count;

        if (room.CurrentMatchId is not null)
        {
            var rosterRow = await db.RoomMatchRosters
                .SingleOrDefaultAsync(x => x.RoomId == room.Id && x.UserId == membership.UserId && x.MatchId == room.CurrentMatchId, cancellationToken);
            if (rosterRow is not null) rosterRow.LeftAtUtc = DateTime.UtcNow;
        }

        if (room.Members.Count == 0)
        {
            if (allowReleaseDuringMatch || RoomStatus.Normalize(room.Status) != RoomStatus.InMatch)
            {
                // R1（审计修复）：房间行即将删除——绑定不允许存活于已删除的房间。归档释放
                // 终局后最后成员退房时也不得跳过 DS 重臂：房间删除与实例回池是两件事。
                // 保留 Draining 绑定，DS 后续 Ready+0 心跳（房间已没亦可处理）完成闭环。
                ReleaseMatchArchiveUnsafe(room);
                db.GameRooms.Remove(room);
            }
            return;
        }
        if (room.HostUserId == membership.UserId)
        {
            var next = room.Members.OrderBy(x => x.JoinedAtUtc).ThenBy(x => x.UserId).First();
            var nextUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == next.UserId, cancellationToken);
            room.HostUserId = next.UserId;
            room.HostUsername = nextUser.Username;
        }
    }

    /// <summary>释放房间绑定的实例为 Ready（房间拆除/开局中止路径）。
    /// A03（V0）：退役中（Draining）实例在此不得提前回池——旧连接清理与 DS 重臂确认
    /// 只能由 DS 重臂后的 Ready 心跳权威完成；其余状态沿用原语义。</summary>
    private void ReleaseInstanceUnsafe(GameRoom room)
    {
        var instance = room.ServerInstance;
        if (instance is null || instance.RoomCode != room.RoomCode) return;
        if (instance.State == InstanceState.Draining) return;
        instance.State = InstanceState.Ready;
        instance.RoomCode = null;
        instance.CurrentPlayers = 0;
        instance.Version++; // 并发令牌：与租用方互斥
    }

    /// <summary>终局退役（A03，V0）：实例转入 Draining——保留房间绑定与 CurrentPlayers，
    /// DS 旧连接清理与重臂未获权威确认前不可被新房间租用（租用查询只认 Ready）；
    /// DS 重臂后的 Ready 心跳（0 人）由心跳路径翻回 Ready 并清绑定回池。</summary>
    private void MarkInstanceDrainingUnsafe(GameRoom room)
    {
        var instance = room.ServerInstance;
        if (instance is null || instance.RoomCode != room.RoomCode) return;
        if (instance.State == InstanceState.Draining) return;
        instance.State = InstanceState.Draining;
        instance.Version++; // 并发令牌：与租用方互斥
    }

    /// <summary>"一人一房"清理（Unsafe=调用方已处于策略/事务上下文）：按 Leave 同款语义退出旧房。</summary>
    private async Task PurgeUserRoomsUnsafeAsync(long userId, CancellationToken cancellationToken)
    {
        var membership = await db.GameRoomMembers
            .Include(x => x.Room).ThenInclude(r => r.Members)
            .Include(x => x.Room).ThenInclude(r => r.ServerInstance)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (membership is null) return;
        await RemoveMemberUnsafeAsync(membership, cancellationToken);
    }

    // ===== 懒维护（Docs/27 §3/§9；List/Detail 等高频读路径顺带执行，竞态以并发令牌兜底）=====

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var instanceCutoff = now - instances.InstanceTtl;
        var memberCutoff = now - instances.MemberTtl;
        var startCutoff = now - instances.StartingTimeout;
        var returningCutoff = now - instances.ReturningTimeout;
        List<GameRoom> candidates;
        try
        {
            candidates = await db.GameRooms
                .Include(x => x.Members)
                .Include(x => x.ServerInstance)
                .Where(x => x.Status != RoomStatus.Closed)
                .Where(x =>
                    ((x.Status == RoomStatus.Starting || x.Status == RoomStatus.InMatch || x.Status == RoomStatus.Returning)
                        && (x.ServerInstanceId == null || x.ServerInstance.LastHeartbeatUtc < instanceCutoff))
                    || (x.Status == RoomStatus.Starting && x.StateChangedAtUtc <= startCutoff)
                    || (x.Status == RoomStatus.Returning && x.StateChangedAtUtc <= returningCutoff)
                    || ((x.Status == RoomStatus.InMatch || x.Status == RoomStatus.Returning)
                        && !x.Members.Any() && x.ServerInstance != null && x.ServerInstance.CurrentPlayers == 0)
                    || (x.Status == RoomStatus.Waiting && !x.Members.Any())
                    || (x.Status == RoomStatus.Waiting
                        && x.Members.Any(m => m.LastSeenUtc < memberCutoff))
                    // 未知/空白状态（client-hosted 时代遗留行）同样是清理候选
                    || (x.Status != RoomStatus.Waiting && x.Status != RoomStatus.Starting
                        && x.Status != RoomStatus.InMatch && x.Status != RoomStatus.Returning
                        && x.Status != RoomStatus.Closed))
                .OrderBy(x => x.StateChangedAtUtc)
                .Take(50)
                .ToListAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return;
        }

        foreach (var room in candidates)
        {
            try
            {
                await MaintainRoomUnsafeAsync(room, now, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }
        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // 并发维护竞态无害：下一轮扫描兜底
            }
        }
    }

    private async Task MaintainRoomUnsafeAsync(GameRoom room, DateTime now, CancellationToken cancellationToken)
    {
        var status = RoomStatus.Normalize(room.Status);
        // Old backends could leave empty Waiting rows forever: the stale-member
        // sweep below has no member to inspect. Retire the room and quarantine any
        // lingering lease until the dedicated server confirms Ready/0.
        if (status == RoomStatus.Waiting && room.Members.Count == 0)
        {
            if (room.ServerInstance != null) ReleaseMatchArchiveUnsafe(room);
            db.GameRooms.Remove(room);
            chat.DropRoom(room.RoomCode);
            return;
        }
        // Recover rooms stranded by older leave-before-disconnect implementations.
        // Both membership and DS-reported transport occupancy must be empty. This
        // only retires the binding; normal DS rearm/registration still releases it.
        if (status is RoomStatus.InMatch or RoomStatus.Returning
            && room.Members.Count == 0 && room.ServerInstance?.CurrentPlayers == 0)
        {
            ReleaseMatchArchiveUnsafe(room);
            db.GameRooms.Remove(room);
            chat.DropRoom(room.RoomCode);
            return;
        }
        if (status == RoomStatus.Starting)
        {
            var instanceDead = room.ServerInstanceId is null
                || room.ServerInstance!.LastHeartbeatUtc < now - instances.InstanceTtl;
            if (instanceDead || room.StateChangedAtUtc <= now - instances.StartingTimeout)
            {
                // Starting 失败回 Waiting：释放实例、清准备、比赛作废（Docs/27 §3：保留房间允许重试）
                await AbandonMatchUnsafeAsync(room, now, cancellationToken);
                logger.LogInformation("[RoomFlow] START_ABORTED room={RoomCode} match={MatchId} reason={Reason}",
                    room.RoomCode, room.LastMatchId, instanceDead ? "instance-dead" : "timeout");
                chat.SendSystem(room.RoomCode, "开局失败，已返回等待房间（准备已清空，可重试）");
            }
            else
            {
                await SweepStaleMembersUnsafeAsync(room, now, cancellationToken);
            }
        }
        else if (status == RoomStatus.InMatch)
        {
            var instanceDead = room.ServerInstanceId is null
                || room.ServerInstance!.LastHeartbeatUtc < now - instances.InstanceTtl;
            if (instanceDead)
            {
                // 沿用旧语义：InMatch 房间的 DS 失联即比赛环境消失，房间拆除（客户端走断线回大厅）
                ReleaseInstanceUnsafe(room);
                db.GameRooms.Remove(room);
            }
        }
        else if (status == RoomStatus.Returning)
        {
            if (room.ServerInstance == null || room.ServerInstance.LastHeartbeatUtc < now - instances.InstanceTtl)
            {
                // Fencing is permanent for this process identity: late heartbeats/registers cannot
                // revive it and acknowledge a new lease while old battle connections still exist.
                var expired = room.ServerInstance;
                if (expired != null)
                {
                    expired.State = InstanceState.Fenced;
                    expired.Version++;
                    expired.RoomCode = null;
                    expired.CurrentPlayers = 0;
                }
                var rows = await db.RoomMatchRosters.Where(x => x.RoomId == room.Id
                    && x.MatchId == room.CurrentMatchId && x.LeftAtUtc == null).ToListAsync(cancellationToken);
                foreach (var row in rows) row.LeftAtUtc = now;
                room.ServerInstance = null;
                room.ServerInstanceId = null;
                ReturnToWaitingUnsafe(room, now);
                logger.LogWarning("[RoomFlow] RETURNING_FENCED room={RoomCode} instance={InstanceId}", room.RoomCode, expired?.InstanceId);
                return;
            }
            if (room.StateChangedAtUtc <= now - instances.ReturningTimeout)
            {
                // F1：超时只证明客户端没有按预期完成返房，不能证明已消费票据的 DS
                // 战斗连接退场。仅把“从未成功 consume 的票据”对应名单标为未入场，
                // 这是独立的无连接事实；仍有已消费会话时保持 Returning + Draining。
                var allBattleDone = await MarkNeverConnectedRosterRowsLeftAsync(room, now, cancellationToken);
                if (allBattleDone)
                {
                    ReturnToWaitingUnsafe(room, now);
                    logger.LogInformation("[RoomFlow] RETURNING_TIMEOUT_ARCHIVED_NO_LIVE_CONNECTION room={RoomCode} match={MatchId}（仅无连接名单收口，实例仍 Draining 等待重臂）",
                        room.RoomCode, room.LastMatchId);
                }
                else
                {
                    logger.LogWarning("[RoomFlow] RETURNING_TIMEOUT_HELD room={RoomCode} match={MatchId}（仍有已消费会话，等待 DS 真实断线事实，实例保持 Draining）",
                        room.RoomCode, room.CurrentMatchId);
                }
            }
        }
        else if (status == RoomStatus.Waiting)
        {
            await SweepStaleMembersUnsafeAsync(room, now, cancellationToken);
        }
        else
        {
            // 未知/空白状态 = client-hosted 时代遗留行（迁移回填前的历史形态）：直接清除不残留
            ReleaseInstanceUnsafe(room);
            db.GameRooms.Remove(room);
        }
    }

    /// <summary>开局作废（Starting 超时/实例失联）：回 Waiting、清准备、名单标记离场、释放实例。</summary>
    private async Task AbandonMatchUnsafeAsync(GameRoom room, DateTime now, CancellationToken cancellationToken)
    {
        var matchId = room.CurrentMatchId;
        if (matchId is not null)
        {
            var rosterRows = await db.RoomMatchRosters
                .Where(x => x.RoomId == room.Id && x.MatchId == matchId && x.LeftAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var row in rosterRows) row.LeftAtUtc = now;
        }
        room.LastMatchId = room.CurrentMatchId;
        room.CurrentMatchId = null;
        room.Status = RoomStatus.Waiting;
        room.RoomVersion++;
        room.StateChangedAtUtc = now;
        foreach (var m in room.Members) m.IsReady = false;
        ReleaseInstanceUnsafe(room);
    }

    /// <summary>
    /// Returning→Waiting（全员真实战斗退场；Q05 + F1）：清准备、归档比赛，
    /// 但实例仍保持 Draining，等待 DS 的 Ready+0 重臂心跳后才回池。
    /// </summary>
    private void ReturnToWaitingUnsafe(GameRoom room, DateTime now)
    {
        room.LastMatchId = room.CurrentMatchId;
        room.CurrentMatchId = null;
        room.Status = RoomStatus.Waiting;
        room.RoomVersion++;
        room.StateChangedAtUtc = now;
        foreach (var m in room.Members) m.IsReady = false;
        MarkInstanceDrainingUnsafe(room);
    }

    /// <summary>
    /// 战斗租约归档后的隔离保持（F1）：房间可以回 Waiting，但实例不能因 ack/超时直接 Ready。
    /// 只有 DS 在旧连接清零并完成本地重臂门禁后发送 Ready+0 心跳，ServerInstanceService
    /// 才清绑定并把实例翻回 Ready；这样 Ready/0 的 disconnect 响应只是释放许可，不是数据库回池事实。
    /// </summary>
    private void ReleaseMatchArchiveUnsafe(GameRoom room)
    {
        MarkInstanceDrainingUnsafe(room);
    }

    /// <summary>
    /// 返房超时的无连接收口：开局名单中的成员可能从未成功消费过 DS 票据，
    /// 因而不会产生 players/disconnect。只有明确不存在该局已消费票据时才写 LeftAtUtc；
    /// 已消费玩家必须继续等待真实断线，不能被维护超时猜测为已退场。
    /// </summary>
    private async Task<bool> MarkNeverConnectedRosterRowsLeftAsync(GameRoom room, DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(room.CurrentMatchId)) return false;
        var rosterRows = await db.RoomMatchRosters
            .Where(x => x.RoomId == room.Id && x.MatchId == room.CurrentMatchId)
            .ToListAsync(cancellationToken);
        foreach (var row in rosterRows)
        {
            if (row.LeftAtUtc is not null) continue;
            var consumed = await db.ServerJoinTickets.AsNoTracking().AnyAsync(x =>
                x.RoomCode == room.RoomCode && x.UserId == row.UserId
                && x.MatchId == room.CurrentMatchId && x.ConsumedAtUtc != null, cancellationToken);
            if (!consumed) row.LeftAtUtc = now;
        }
        // 返回本次已追踪的内存水位；不要立即再 AnyAsync——关系型 EF 查询看不到
        // 本事务尚未 SaveChanges 的 LeftAtUtc（此前正是最后一人归档漏触发的根因）。
        return rosterRows.Count > 0 && rosterRows.All(x => x.LeftAtUtc != null);
    }

    /// <summary>已归档旧局的真实退场水位（只读重试口；空名单不构成释放事实）。</summary>
    private async Task<bool> HasFullyExitedRosterAsync(GameRoom room, string matchId,
        CancellationToken cancellationToken)
    {
        var rosterRows = await db.RoomMatchRosters
            .Where(x => x.RoomId == room.Id && x.MatchId == matchId)
            .Select(x => x.LeftAtUtc)
            .ToListAsync(cancellationToken);
        return rosterRows.Count > 0 && rosterRows.All(x => x is not null);
    }

    /// <summary>Waiting/Starting 成员过期清理（LastSeenUtc 超 TTL）：触发 leader 移交/空房删除。</summary>
    private async Task SweepStaleMembersUnsafeAsync(GameRoom room, DateTime now, CancellationToken cancellationToken)
    {
        var stale = room.Members.Where(m => m.LastSeenUtc < now - instances.MemberTtl).ToList();
        foreach (var membership in stale)
            await RemoveMemberUnsafeAsync(membership, cancellationToken);
    }

    // ===== 投影 =====

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

    /// <summary>人数以 Members 集合投影（Phase E 纪律①：并发下 JoinedPlayers 列可能丢失更新，集合才是真相）。</summary>
    private static GameRoomDto ToDto(GameRoom room) => ToDto(room, null);

    private static GameRoomDto ToDto(GameRoom room, long? viewerId) => new(
        viewerId == room.HostUserId ? room.RoomCode : null, room.HostUsername, room.Members.Count, room.MaxPlayers, RoomStatus.Normalize(room.Status),
        room.CreatedAtUtc, room.Mode, room.MapId, room.KillTarget, room.TimeLimitMinutes,
        room.RoomVersion, room.CurrentMatchId, room.MatchGeneration, room.Id);

    private static RoomMemberDto ToMemberDto(GameRoomMember member, GameRoom room) => new(
        member.UserId, member.User.Username, member.TeamId, member.IsReady, room.HostUserId == member.UserId, member.JoinedAtUtc);

    private static RoomSnapshotDto ToSnapshot(GameRoom room, long userId, RoomConnectionInfoDto? connection) => new(
        ToDto(room, userId),
        room.Members
            .OrderBy(m => m.TeamId == Teams.Red ? 0 : m.TeamId == Teams.Blue ? 1 : 2)
            .ThenBy(m => m.JoinedAtUtc)
            .Select(m => ToMemberDto(m, room)).ToArray(),
        new RoomSelfDto(userId,
            room.Members.FirstOrDefault(x => x.UserId == userId)?.TeamId ?? Teams.None,
            room.Members.FirstOrDefault(x => x.UserId == userId)?.IsReady ?? false),
        connection);

    private static StartMatchDto ToStartMatchDto(GameRoom room, RoomConnectionInfoDto connection, StartRosterEntryDto[] roster) => new(
        room.CurrentMatchId!, room.MatchGeneration, room.RoomVersion, room.MapId, room.Mode,
        room.KillTarget, room.TimeLimitMinutes, connection, roster);
}
