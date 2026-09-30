using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// Dedicated Server 控制面（Docs/27 §2）：实例注册/心跳/过期判定 + 一次性 join ticket 签发与消费。
/// 密钥鉴权在端点层（RequireServerKeyAttribute），本服务只处理业务。
/// 保密纪律：票据明文只在签发返回值中出现一次，库内只存 SHA-256 hash；
/// 任何日志/异常消息不得包含票据明文或服务器密钥（日志仅 instance/room/user 级信息）。
/// 并发纪律：与 RoomService 同款 CreateExecutionStrategy + Serializable 事务；
/// 票据消费/实例租用另由 Version 乐观并发令牌兜底（InMemory 测试提供方无事务时的唯一防线）。
/// </summary>
public sealed class ServerInstanceService(AppDbContext db, IOptions<ServerInstanceOptions> options, ILogger<ServerInstanceService> logger)
{
    private readonly ServerInstanceOptions options = options.Value;

    /// <summary>实例心跳 TTL（租用判定与房间懒清理共用）。</summary>
    public TimeSpan InstanceTtl => TimeSpan.FromSeconds(Math.Clamp(options.InstanceTtlSeconds, 1, 3600));

    /// <summary>Waiting 成员心跳 TTL（Docs/27 v1 §2；RoomService 懒维护共用）。</summary>
    public TimeSpan MemberTtl => options.MemberTtl;

    /// <summary>Starting 超时（Docs/27 v1 §3）。</summary>
    public TimeSpan StartingTimeout => options.StartingTimeout;

    /// <summary>Returning→Waiting 宽限（Docs/27 v1 §3）。</summary>
    public TimeSpan ReturningTimeout => options.ReturningTimeout;

    public async Task EnterMaintenanceAsync(string instanceId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var row = await db.ServerInstances.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct)
                ?? throw new ApiException(404, ApiErrorCodes.ServerInstanceNotFound, "实例未注册");
            if (row.CurrentPlayers != 0 || row.RoomCode != null || row.State is not ("Ready" or "Offline" or "Maintenance"))
                throw new ApiException(409, "MAP_BUSY", "地图仍有对局，不能切换版本");
            row.State = "Maintenance";
            row.Version++;
            await db.SaveChangesAsync(ct);
            if (tx != null) await tx.CommitAsync(ct);
        });
    }

    /// <summary>注册（幂等 upsert）：新实例直接 Ready；重注册时保留仍存活的房间绑定（服务器快速重启场景）。</summary>
    public async Task<ServerInstanceRegisterDto> RegisterAsync(ServerInstanceRegisterRequest request, CancellationToken cancellationToken)
    {
        var instanceId = request.InstanceId.Trim();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var now = DateTime.UtcNow;
                var instance = await db.ServerInstances
                    .SingleOrDefaultAsync(x => x.InstanceId == instanceId, cancellationToken);
                if (instance is null)
                {
                    instance = new ServerInstance { InstanceId = instanceId, RegisteredAtUtc = now };
                    db.ServerInstances.Add(instance);
                }
                if (instance.State == InstanceState.Fenced)
                    throw new ApiException(409, ApiErrorCodes.ServerInstanceStateConflict, "实例身份已隔离，请以新实例 ID 启动进程");
                instance.Address = request.Address.Trim();
                instance.Port = request.Port;
                instance.Capacity = request.Capacity;
                instance.BuildVersion = request.BuildVersion;
                // P0-A（2026-09-15）：实例申报的应用协议代际——空串归一 null（旧 DS 未申报）
                instance.ProtocolId = string.IsNullOrWhiteSpace(request.ProtocolId) ? null : request.ProtocolId.Trim();
                // Phase 8：实例绑定地图——空串归一 null（旧 DS，租用匹配按 arena 处理）
                instance.MapId = string.IsNullOrWhiteSpace(request.MapId) ? null : request.MapId.Trim();
                instance.LastHeartbeatUtc = now;
                instance.Version++; // 并发令牌：注册改写与新实例插入同样参与乐观并发检查
                if (instance.RoomCode is not null)
                {
                    // 绑定以数据库为准（审计 P0-1）：房间仍在则保持绑定，状态从房间权威状态推导
                    //（InMatch 不因重注册回落 Reserved）；房间已没 → 释放为 Ready
                    var room = await db.GameRooms.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.RoomCode == instance.RoomCode, cancellationToken);
                    if (room is not null && room.Status != RoomStatus.Closed)
                    {
                        // F1：Waiting 只可能在本局所有真实连接退场（或明确从未消费票据）
                        // 后形成。DS 因旧连接/旧 tracker 迟到而走 409→重注册时，允许这次
                        // 重注册完成同一代际的清绑定；Returning 仍绝不因重注册回池。
                        if (instance.State == InstanceState.Draining
                            && RoomStatus.Normalize(room.Status) == RoomStatus.Waiting
                            && room.CurrentMatchId is null)
                        {
                            instance.State = InstanceState.Ready;
                            instance.RoomCode = null;
                            instance.CurrentPlayers = 0;
                        }
                        else
                        {
                            instance.State = room.Status == RoomStatus.InMatch
                                ? InstanceState.InMatch : InstanceState.Reserved;
                        }
                    }
                    else
                    {
                        instance.State = InstanceState.Ready;
                        instance.RoomCode = null;
                    }
                }
                else
                {
                    instance.State = InstanceState.Ready;
                }
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                // Docs/27 §6 验收日志标记（不含密钥/票据）
                logger.LogInformation("[ServerRegistry] REGISTERED instance={InstanceId} state={State} endpoint={Address}:{Port}",
                    instance.InstanceId, instance.State, instance.Address, instance.Port);
                return new ServerInstanceRegisterDto(instance.InstanceId, instance.State, options.HeartbeatIntervalSeconds);
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    /// <summary>
    /// 心跳（审计 P0-1 状态机）：数据库中的房间绑定是权威真相，心跳只做存活证明与合法转换。
    /// 合法转换表（闭集，其余一律 409 SERVER_INSTANCE_STATE_CONFLICT，不改写状态/绑定）：
    ///   未绑定（RoomCode=null）：Ready→Ready、Ready→Offline，且心跳不得携带 roomCode（不得凭空认领绑定）；
    ///   Reserved：Reserved→Reserved（roomCode 为空或与绑定一致）；Reserved→InMatch（roomCode 须精确匹配，同步房间状态）；
    ///   InMatch：InMatch→InMatch（roomCode 须精确匹配）。
    ///   已绑定实例的 Ready/Offline 心跳是降级尝试，一律拒绝（Codely 现状"持续发 Ready"由此拦截）；
    ///   roomCode 与绑定不一致一律拒绝，绑定只能由后端租用/释放路径改写。
    /// 拒绝路径仍刷新 LastHeartbeatUtc/CurrentPlayers（存活证明），但不改状态——
    /// 行为有误的服务器不应导致其绑定房间被 TTL 误清（房间拆除归 Day2 权威链）。
    /// </summary>
    public async Task HeartbeatAsync(string instanceId, ServerInstanceHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var reportedState = InstanceState.All.Contains(request.State.Trim())
            ? request.State.Trim()
            : throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                $"state 必须是 {string.Join("/", InstanceState.All)} 之一");
        var reportedRoom = NormalizeRoomCode(request.RoomCode);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            string? rejectionDetail = null;
            try
            {
                var instance = await db.ServerInstances
                    .SingleOrDefaultAsync(x => x.InstanceId == instanceId, cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.ServerInstanceNotFound, "实例未注册");

                if (instance.State == InstanceState.Fenced)
                    throw new ApiException(409, ApiErrorCodes.ServerInstanceStateConflict, "实例身份已隔离");
                if (instance.State == "Maintenance")
                    throw new ApiException(409, "INSTANCE_MAINTENANCE", "实例正在更新，旧心跳不能恢复入场");
                // 存活证明先落库：无论转换是否合法，TTL 都要刷新
                instance.LastHeartbeatUtc = DateTime.UtcNow;
                instance.CurrentPlayers = request.CurrentPlayers;
                // Phase 8：随心跳同步实例地图（空 = 旧 DS，不覆盖已存值——重注册语义归 register）
                if (!string.IsNullOrWhiteSpace(request.MapId))
                    instance.MapId = request.MapId.Trim();

                var boundRoomCode = instance.RoomCode;
                bool legal;
                if (boundRoomCode is null)
                {
                    legal = (reportedState == InstanceState.Ready || reportedState == InstanceState.Offline)
                            && reportedRoom is null;
                }
                else if (reportedState == InstanceState.Reserved)
                {
                    // 绑定态只允许确认绑定；InMatch 不得经心跳回退 Reserved
                    legal = instance.State == InstanceState.Reserved
                            && (reportedRoom is null || reportedRoom == boundRoomCode);
                }
                else if (reportedState == InstanceState.InMatch)
                {
                    legal = (instance.State == InstanceState.Reserved || instance.State == InstanceState.InMatch)
                            && reportedRoom == boundRoomCode;
                }
                else
                {
                    legal = false; // 绑定态的 Ready/Offline = 降级尝试
                }

                // A03（V0）退役中（Draining：比赛终局/返房后、DS 旧连接清理与重臂未获权威确认）：
                // 不可租用；仅 DS 重臂后的 Ready 心跳（CurrentPlayers=0，roomCode 为空或与旧绑定一致）
                // 翻回 Ready 并清绑定回池——迟到的 Ready ack 绝不释放新租约（新租约只可能出现在
                // 已翻回 Ready 之后）；旧连接未清零（players>0）的 Ready 仅作存活证明、维持 Draining；
                // Offline = DS 退场清绑定；Reserved/InMatch 属终局后状态回跳，拒绝。
                bool drainingHold = false;
                if (instance.State == InstanceState.Draining
                    && reportedState is InstanceState.Ready or InstanceState.Offline)
                {
                    bool roomMatches = reportedRoom is null || reportedRoom == boundRoomCode;
                    if (reportedState == InstanceState.Ready && roomMatches && request.CurrentPlayers == 0)
                    {
                        // F1：Ready/0 只有在房间已由真实 disconnect facts 归档到 Waiting
                        //（或房间已删除）时才是重臂事实。Returning 中的客户端 ack/超时
                        // 不得让 DS 自报 Ready 抢先清租约。
                        var boundRoom = await db.GameRooms.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.RoomCode == boundRoomCode, cancellationToken);
                        bool roomArchiveReady = boundRoom is null
                            || (RoomStatus.Normalize(boundRoom.Status) == RoomStatus.Waiting
                                && boundRoom.CurrentMatchId is null);
                        legal = roomArchiveReady;
                        if (legal)
                            instance.RoomCode = null; // 重臂确认：清绑定回池（State 由下方统一写 Ready）
                        else
                            drainingHold = true;
                    }
                    else if (reportedState == InstanceState.Offline && roomMatches)
                    {
                        legal = true;
                        instance.RoomCode = null; // DS 退场：清绑定（State 由下方统一写 Offline）
                    }
                    else if (reportedState == InstanceState.Ready && roomMatches)
                    {
                        legal = true;
                        drainingHold = true; // 人未清零：存活刷新已生效，维持 Draining
                    }
                    else
                    {
                        legal = false;
                    }
                }

                if (legal)
                {
                    if (drainingHold)
                    {
                        // 维持退役态：不写 reportedState
                    }
                    else
                    {
                        if (reportedState == InstanceState.InMatch && boundRoomCode is not null)
                        {
                            // Day1 仅做展示级同步；比赛终局/回 WaitingForPlayers 的完整生命周期归 Day2 服务器权威链。
                            // R1（审计修复）：只允许 Starting→InMatch 的开局方向同步——DS 在终局后（房间已
                            // Returning/Waiting）仍会按本地 tracker 心跳 InMatch，无条件回写会把返房窗口
                            // 打回 InMatch，返房 ack 全部 409、Returning 超时失效，整条返房链死锁。
                            var room = await db.GameRooms
                                .SingleOrDefaultAsync(x => x.RoomCode == boundRoomCode, cancellationToken);
                            if (room is not null && RoomStatus.Normalize(room.Status) == RoomStatus.Starting)
                                room.Status = RoomStatus.InMatch;
                        }
                        instance.State = reportedState;
                    }
                }
                else
                {
                    // 非法转换：状态/绑定保持原样，仅存活刷新生效；稳定业务错误在事务提交后抛出
                    rejectionDetail = boundRoomCode is null
                        ? "实例未绑定房间，心跳不得携带 roomCode 或进入 Reserved/InMatch"
                        : $"实例已绑定房间 {boundRoomCode}，心跳不得降级到 {reportedState} 或改写绑定";
                }

                instance.Version++; // 并发令牌：心跳改写（含存活刷新）参与乐观并发检查
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
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

            if (rejectionDetail is not null)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ServerInstanceStateConflict, rejectionDetail);
            }
        });
    }

    /// <summary>
    /// 消费一次性票据（Docs/27 §2.3 + 审计 P0-2）：事务内检查并写 ConsumedAtUtc。
    /// 失败码顺序：格式/查无 → TICKET_INVALID；调用实例未知/票据绑定其他实例 → TICKET_INSTANCE_MISMATCH；
    /// 已消费 → TICKET_REPLAYED；过期 → TICKET_EXPIRED；
    /// 权威复核（房间存在且未 Closed、实例仍绑定该房间、签发者仍是成员）不满足 → TICKET_INVALID。
    /// 永远 200 + valid 标记（带内错误码）。
    /// </summary>
    public async Task<JoinTicketConsumeDto> ConsumeTicketAsync(JoinTicketConsumeRequest request, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var ticketHash = HashTicket(request.Ticket);
            if (ticketHash is null)
                return Invalid();

            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var instance = await db.ServerInstances.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.InstanceId == request.InstanceId, cancellationToken);
                if (instance is null || instance.State == InstanceState.Fenced)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Mismatch();
                }

                var ticket = await db.ServerJoinTickets
                    .SingleOrDefaultAsync(x => x.TicketHash == ticketHash, cancellationToken);
                if (ticket is null)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Invalid();
                }
                if (ticket.ServerInstanceId != instance.Id)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Mismatch();
                }
                if (ticket.ConsumedAtUtc is not null)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Replayed();
                }
                if (ticket.ExpiresAtUtc <= DateTime.UtcNow)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Expired();
                }

                // 权威复核（审计 P0-2）：绑定与成员资格以数据库当前状态为准，票据本身不作数。
                // Leave/换房/房间关闭/实例重新分配后，此处 fail closed 且不写 ConsumedAtUtc；
                // 与 Leave 等路径同处 Serializable 事务，复核结果不存在竞态窗口（无需批量撤销票据）。
                var room = await db.GameRooms.AsNoTracking()
                    .Include(x => x.Members)
                    .SingleOrDefaultAsync(x => x.RoomCode == ticket.RoomCode, cancellationToken);
                if (room is null || room.Status == RoomStatus.Closed)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Invalid(); // 房间已解散/关闭：绑定失效
                }
                if (instance.RoomCode != ticket.RoomCode)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Invalid(); // 实例已解绑或被重新分配给其他房间
                }
                if (await db.Users.AnyAsync(u => u.Id == ticket.UserId && u.Disabled, cancellationToken))
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Invalid();
                }
                var member = room.Members.FirstOrDefault(m => m.UserId == ticket.UserId);
                if (member is null)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return Invalid(); // 签发后已离开/换房，不再是该房间成员
                }

                // CF 比赛身份带出（Docs/27 §6 扩展 2）：比赛进行中时附 matchId/generation/队伍，
                // DS 据此做队伍初始化与 Pending 名额核验；Waiting/无比赛时为 null（旧语义）。
                // R01：比赛身份以票据签发时冻结值为准严格比对——旧票在下一局一律失效，绝不补当前值。
                if (ticket.MatchId != room.CurrentMatchId)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    logger.LogInformation(
                        "[ServerRegistry] TICKET_MATCH_MISMATCH instance={InstanceId} room={RoomCode} user={UserId}",
                        request.InstanceId, ticket.RoomCode, ticket.UserId);
                    return Invalid();
                }

                var rosterTeam = ticket.MatchId is null
                    ? (string?)null
                    : (await db.RoomMatchRosters.AsNoTracking()
                        .SingleOrDefaultAsync(
                            x => x.RoomId == room.Id && x.UserId == ticket.UserId && x.MatchId == ticket.MatchId,
                            cancellationToken))?.TeamId;

                // 账号权威配装快照（2026-09-08 追加 P0 §6 二.1）：权威复核通过后、同一 Serializable 事务内
                // 从 PlayerLoadout 读取——Dedicated Server 据此把网络玩家 Arsenal 严格配置为账号实际两槽
                //（primary/secondary + 影响属性/枪模的附件）。读不到行属异常态（注册必建行）：返回 null，
                // 由 DS 侧 fail closed（拒绝生成），绝不回退调试 Arsenal。
                // CF 三背包（2026-09-30 Phase A）：一并携带三背包全集（懒默认合成），DS 据此支持对局内
                // 按规则换背包；Loadout 字段保留 = 活动背包（index 0）镜像给旧版 DS。
                var loadoutRows = await db.Loadouts.AsNoTracking()
                    .Include(x => x.Attachments)
                    .Where(x => x.UserId == ticket.UserId)
                    .ToListAsync(cancellationToken);
                var loadoutDto = loadoutRows.FirstOrDefault(x => x.BackpackIndex == 0)?.ToDto()
                    ?? loadoutRows.FirstOrDefault()?.ToDto();
                if (loadoutDto is null)
                    logger.LogWarning("[ServerRegistry] TICKET_CONSUME_LOADOUT_MISSING user={UserId}——配装行缺失，DS 应拒绝生成", ticket.UserId);
                var backpackDtos = loadoutRows.Count == 0 ? null : BackpackPolicy.BuildBackpackSet(loadoutRows);

                ticket.ConsumedAtUtc = DateTime.UtcNow;
                ticket.Version++; // 并发令牌：双并发 consume 只有一方落库成功，另一方按 REPLAYED 拒绝
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("[ServerRegistry] TICKET_CONSUMED instance={InstanceId} room={RoomCode} user={UserId}",
                    request.InstanceId, ticket.RoomCode, ticket.UserId);
                return new JoinTicketConsumeDto(true, ticket.RoomCode, ticket.UserId, ticket.Username, ticket.ExpiresAtUtc,
                    ticket.Id, null, loadoutDto, ticket.MatchId, ticket.MatchGeneration, rosterTeam ?? member.TeamId,
                    room.Mode, room.KillTarget, room.TimeLimitMinutes, room.MaxPlayers,
                    backpackDtos, BackpackPolicy.DefaultActiveIndex);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 双并发消费竞态：另一方已写入 ConsumedAtUtc（Version 令牌拦截），本请求按重放拒绝
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return Replayed();
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    // ==== 实例池开发诊断（P0 租约闭环 2026-09-08，审计 §4 第一段 6）====

    /// <summary>
    /// 实例池状态查询（GET /api/server-instances/pool，RequireServerKey 保护——开发诊断命令）：
    /// requestedCapacity&gt;0 时给出"容量不足"视角分解；Fresh = 心跳未超 TTL。不含密钥/票据。
    /// expectedProtocol（可选，P0-A）：给出"协议不匹配"视角分解（房间侧 NO_SERVER_AVAILABLE 归因）。
    /// </summary>
    public async Task<ServerInstancePoolDto> GetPoolDiagnosticsAsync(int requestedCapacity, CancellationToken cancellationToken,
        string? expectedProtocol = null)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - InstanceTtl;
        var rows = await db.ServerInstances.AsNoTracking()
            .OrderBy(x => x.InstanceId)
            .ToListAsync(cancellationToken);
        var instances = rows.Select(x => new ServerInstanceDiagnosticDto(
            x.InstanceId, x.State, x.RoomCode, x.CurrentPlayers, x.Capacity,
            (int)Math.Max(0, (now - x.LastHeartbeatUtc).TotalSeconds), x.LastHeartbeatUtc >= cutoff,
            x.ProtocolId, x.BuildVersion, x.MapId, x.Address, x.Port))
            .ToArray();
        return new ServerInstancePoolDto(SummarizePool(instances, requestedCapacity, expectedProtocol), instances);
    }

    /// <summary>纯汇总（服务级测试锁定分类语义；Fresh/State 由诊断行携带）。
    /// expectedProtocol 非空时统计"新鲜且容量足够但协议不匹配"的 Ready 实例数（P0-A 归因）。</summary>
    public static ServerInstancePoolSummaryDto SummarizePool(
        IReadOnlyList<ServerInstanceDiagnosticDto> instances, int requestedCapacity, string? expectedProtocol = null)
    {
        int readyFresh = 0, readyStale = 0, reserved = 0, inMatch = 0, offline = 0, staleTotal = 0, capacityShort = 0, protocolMismatch = 0;
        foreach (var x in instances)
        {
            if (!x.Fresh) staleTotal++;
            switch (x.State)
            {
                case InstanceState.Ready:
                    if (x.Fresh)
                    {
                        readyFresh++;
                        if (requestedCapacity > 0 && x.Capacity < requestedCapacity) capacityShort++;
                        else if (expectedProtocol != null
                                 && !string.Equals(x.ProtocolId ?? string.Empty, expectedProtocol, StringComparison.Ordinal))
                            protocolMismatch++;
                    }
                    else readyStale++;
                    break;
                case InstanceState.Reserved: reserved++; break;
                case InstanceState.InMatch: inMatch++; break;
                case InstanceState.Offline: offline++; break;
            }
        }
        return new ServerInstancePoolSummaryDto(instances.Count, readyFresh, readyStale, reserved, inMatch, offline,
            staleTotal, capacityShort, requestedCapacity, protocolMismatch);
    }

    /// <summary>
    /// NO_SERVER_AVAILABLE 归因分类（审计五类 + P0-A 协议类）：
    /// no-process → protocol-mismatch（有新鲜且容量足够的 Ready，但协议全部与房间期望不一致——
    /// 部署了旧 DS/新客户端混跑）→ race-transient（快照瞬间有可租实例——租用竞态，非池枯竭）→
    /// capacity-insufficient → all-occupied（进程在线但全部被房间占用——僵尸 Reserved 即此类）→
    /// heartbeat-stale → loop-anomaly。
    /// </summary>
    public static string ClassifyNoServer(ServerInstancePoolSummaryDto summary)
    {
        if (summary.Total == 0) return "no-process";
        int capacityOk = summary.ReadyFresh - summary.ReadyFreshCapacityShort;
        if (capacityOk > 0 && summary.ReadyFreshProtocolMismatch >= capacityOk) return "protocol-mismatch";
        if (capacityOk - summary.ReadyFreshProtocolMismatch > 0) return "race-transient";
        if (summary.ReadyFresh > 0 && summary.ReadyFreshCapacityShort == summary.ReadyFresh) return "capacity-insufficient";
        if (summary.Reserved + summary.InMatch > 0) return "all-occupied";
        if (summary.ReadyStale > 0) return "heartbeat-stale";
        return "loop-anomaly";
    }

    // ==== 供 RoomService 使用的签发工具 ====

    /// <summary>
    /// 签发一次性票据（Docs/27 §2.3：32 字节随机数 Base64Url，TTL 冻结 90s）。
    /// 通过导航挂接实例（同事务内新建实例的 Id 尚未生成时 EF 会自动补 FK）。
    /// 明文只随返回值下发一次；调用方负责与房间写入同事务落库。
    /// R01：签发时冻结比赛身份（matchId/generation），消费严格比对；同 (房,人,实例) 的旧未消费票据
    /// 被新票顶替删除——详情轮询不无界积累，且旧票即时作废（不依赖 TTL）。
    /// </summary>
    internal async Task<(string Ticket, DateTime ExpiresAtUtc)> IssueTicketAsync(ServerInstance instance, string roomCode,
        long userId, string username, string? matchId, int? matchGeneration)
    {
        var now = DateTime.UtcNow;
        // 顶替同键旧未消费票据（消费过的行保留——断线上报按 SessionId 引用）
        var superseded = await db.ServerJoinTickets
            .Where(x => x.RoomCode == roomCode && x.UserId == userId
                     && x.ServerInstanceId == instance.Id
                     && x.ConsumedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync();
        if (superseded.Count > 0) db.ServerJoinTickets.RemoveRange(superseded);

        var plain = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + TimeSpan.FromSeconds(Math.Clamp(options.TicketTtlSeconds, 1, 600));
        db.ServerJoinTickets.Add(new ServerJoinTicket
        {
            TicketHash = HashTicket(plain)!,
            Instance = instance,
            RoomCode = roomCode,
            UserId = userId,
            Username = username,
            IssuedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            MatchId = matchId,
            MatchGeneration = matchGeneration,
        });
        return (plain, expiresAt);
    }

    /// <summary>票据明文 → SHA-256 小写十六进制（64 字符）；格式非法（无法还原 32 字节）返回 null。</summary>
    internal static string? HashTicket(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.Length > 128) return null;
        var bytes = TryBase64UrlDecode(plain);
        if (bytes is null || bytes.Length != 32) return null;
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? TryBase64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder != 0) padded += new string('=', 4 - remainder);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? NormalizeRoomCode(string? roomCode)
    {
        var trimmed = roomCode?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static JoinTicketConsumeDto Invalid() => new(false, null, null, null, null, null, ApiErrorCodes.TicketInvalid);
    private static JoinTicketConsumeDto Expired() => new(false, null, null, null, null, null, ApiErrorCodes.TicketExpired);
    private static JoinTicketConsumeDto Replayed() => new(false, null, null, null, null, null, ApiErrorCodes.TicketReplayed);
    private static JoinTicketConsumeDto Mismatch() => new(false, null, null, null, null, null, ApiErrorCodes.TicketInstanceMismatch);
}
