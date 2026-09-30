using System;

namespace Game.Account
{

public sealed class AccountSession
{
    public string Token { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public PlayerProfileDto Profile { get; private set; }
    /// <summary>活动背包配装（CF 三背包，2026-09-30）：恒等于 Backpacks[ActiveBackpackIndex]。
    /// 存量消费点（大厅预览/枪匠默认值/离线 Arena/补拉缓存）继续只读本属性即可，语义不变。</summary>
    public LoadoutDto Loadout { get; private set; }
    /// <summary>CF 三背包全集（后端懒默认恒长 3；旧后端登录响应不含 backpacks 字段时为 null）。
    /// 仓库/大厅背包页签读本属性；ActiveIndex 本轮恒 0。</summary>
    public BackpackSetDto Backpacks { get; private set; }
    /// <summary>活动背包下标（0/1/2；无全集时回退 0）。</summary>
    public int ActiveBackpackIndex => Backpacks != null
        ? System.Math.Clamp(Backpacks.activeIndex, 0, 2) : 0;
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && ExpiresAtUtc > DateTime.UtcNow;
    public string GameplayError { get; private set; }

    /// <summary>当前所在房间（CF 等待房间语义：创建/加入/详情快照后缓存；离开/清空会话时清除）。</summary>
    public RoomSessionState Room { get; private set; }

    public event Action Changed;

    public void Apply(AuthSessionDto session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        Token = session.token;
        ExpiresAtUtc = DateTime.TryParse(session.expiresAtUtc, out var expiry) ? expiry.ToUniversalTime() : DateTime.UtcNow.AddHours(12);
        Profile = session.profile;
        Loadout = session.loadout;
        if (session.backpacks != null) ApplyBackpackSet(session.backpacks, invokeChanged: false);
        Changed?.Invoke();
    }

    public void ApplyProfile(PlayerProfileDto profile)
    {
        Profile = profile;
        Changed?.Invoke();
    }

    /// <summary>三背包全集写入：Loadout 同步为活动背包（单写者——任何背包更新最终经此或
    /// ApplyLoadout(backpackIndex) 落会话，保证两个视图永不漂移）。</summary>
    public void ApplyBackpackSet(BackpackSetDto set, bool invokeChanged = true)
    {
        if (set == null || set.backpacks == null || set.backpacks.Length == 0) return;
        Backpacks = set;
        int active = ActiveBackpackIndex;
        Loadout = set.backpacks[active];
        if (invokeChanged) Changed?.Invoke();
    }

    /// <summary>按背包下标读取配装（越界/无全集回退活动背包——组件不需要自己判空）。</summary>
    public LoadoutDto LoadoutForBackpack(int index)
    {
        if (Backpacks != null && Backpacks.backpacks != null
            && index >= 0 && index < Backpacks.backpacks.Length
            && Backpacks.backpacks[index] != null)
            return Backpacks.backpacks[index];
        return Loadout;
    }

    public void ApplyLoadout(LoadoutDto loadout)
    {
        ApplyLoadout(loadout, -1);
        Changed?.Invoke();
    }

    /// <summary>单背包更新（仓库/枪匠保存成功回写）：backpackIndex&lt;0 时按 dto 自带下标（无则活动背包）。</summary>
    public void ApplyLoadout(LoadoutDto loadout, int backpackIndex)
    {
        if (loadout == null) return;
        int index = backpackIndex >= 0 ? backpackIndex : loadout.backpackIndex;
        if (Backpacks != null && Backpacks.backpacks != null
            && index >= 0 && index < Backpacks.backpacks.Length)
            Backpacks.backpacks[index] = loadout;
        if (index == ActiveBackpackIndex || Backpacks == null)
            Loadout = loadout;
    }

    /// <summary>
    /// 配件保存接口只返回版本与配件数组；必须把它合并回当前会话，否则下一次更换武器仍会
    /// 携带旧 expectedVersion，后端会误判为“配装已被其他窗口修改”。武器槽位保持不变。
    /// backpackIndex&lt;0 = 活动背包（三背包 Phase B 增参，旧调用零改动）。
    /// </summary>
    public void ApplyLoadoutAttachments(LoadoutAttachmentsDto update, int backpackIndex = -1)
    {
        if (update == null) throw new ArgumentNullException(nameof(update));
        int index = backpackIndex >= 0 ? backpackIndex : ActiveBackpackIndex;
        var target = LoadoutForBackpack(index);
        if (target == null)
        {
            if (index != ActiveBackpackIndex) return; // 目标背包尚未在会话（理论不可达：后端恒下发全集）
            target = Loadout = new LoadoutDto { backpackIndex = index };
        }
        target.version = update.version;
        target.attachments = update.attachments ?? Array.Empty<LoadoutAttachmentDto>();
        ApplyLoadout(target, index);
        Changed?.Invoke();
    }

    /// <summary>
    /// 连接/房间会话代际（Docs/27 §11 CF 修订）：**仅在战斗连接启动时递增**——
    /// 即上层把 start ack / InMatch 重连的 connection 写入 NetworkLaunchContext、即将加载 Arena 时
    /// （AdvanceConnectionGeneration）。等待房间的创建/加入/详情轮询不递增；退房 ClearRoom 不递增。
    /// 退出事务（LeaveTransactionCoordinator）以此判定「新战斗会话已替代旧会话」。
    /// </summary>
    public long ConnectionGeneration { get; private set; }

    /// <summary>战斗连接启动调用（唯一递增点）：上下文已写入、Arena 即将加载。</summary>
    public void AdvanceConnectionGeneration()
    {
        ConnectionGeneration++;
        Changed?.Invoke();
    }

    /// <summary>
    /// 建立房间会话（创建/加入成功后的首份快照；Docs/27 §11：等待房间不推进 ConnectionGeneration）。
    /// 不同房间码的快照同样只替换状态——代际语义已收归战斗连接，房间切换不再承担连接代际职责。
    /// </summary>
    public void ApplyRoomSnapshot(RoomSnapshotDto snapshot)
    {
        if (snapshot?.room == null) return;
        Room = BuildRoomState(snapshot.room, snapshot.you);
        Changed?.Invoke();
    }

    /// <summary>同一会话的房间快照刷新（CF 等待房间轮询）：原地更新字段，不推进代际。
    /// 无会话或房间码变化时退化为 ApplyRoomSnapshot（视为建立会话）。</summary>
    public void RefreshRoomSnapshot(RoomSnapshotDto snapshot)
    {
        if (snapshot?.room == null) return;
        if (Room == null || !string.Equals(Room.RoomId, snapshot.room.roomId.ToString(), StringComparison.Ordinal))
        {
            ApplyRoomSnapshot(snapshot);
            return;
        }
        var state = BuildRoomState(snapshot.room, snapshot.you);
        // 原地写回：保持 Room 对象引用稳定（观测方按引用缓存不被打断）
        Room.RoomCode = state.RoomCode;
        Room.HostUsername = state.HostUsername;
        Room.MemberCount = state.MemberCount;
        Room.MaxPlayers = state.MaxPlayers;
        Room.IsHost = state.IsHost;
        Room.Status = state.Status;
        Room.Mode = state.Mode;
        Room.MapId = state.MapId;
        Room.KillTarget = state.KillTarget;
        Room.TimeLimitMinutes = state.TimeLimitMinutes;
        Room.RoomVersion = state.RoomVersion;
        Room.MatchId = state.MatchId;
        Room.MatchGeneration = state.MatchGeneration;
        Room.TeamId = state.TeamId;
        Room.IsReady = state.IsReady;
        Changed?.Invoke();
    }

    public void ClearRoom()
    {
        if (Room == null) return;
        Room = null;
        Changed?.Invoke();
    }

    /// <summary>房主 start ack 后的会话同步（Docs/27 §5.5）：状态进入 Starting 并锁定比赛身份。
    /// 不推进代际——代际在票据写上下文（EnterBattleAsync）时推进。</summary>
    public void NoteMatchStart(StartMatchDto start)
    {
        if (Room == null || start == null) return;
        Room.Status = RoomStatus.Starting;
        Room.MatchId = start.matchId;
        Room.MatchGeneration = start.matchGeneration;
        Room.RoomVersion = start.roomVersion;
        Changed?.Invoke();
    }

    public void SetGameplayError(string message)
    {
        GameplayError = message;
        Changed?.Invoke();
    }

    public string ConsumeGameplayError()
    {
        var value = GameplayError;
        GameplayError = null;
        return value;
    }

    public void Clear()
    {
        Token = null;
        ExpiresAtUtc = null;
        Profile = null;
        Loadout = null;
        Backpacks = null;
        GameplayError = null;
        Room = null;
        Changed?.Invoke();
    }

    private RoomSessionState BuildRoomState(GameRoomDto room, RoomSelfDto you)
    {
        return new RoomSessionState
        {
            RoomId = room.roomId.ToString(),
            RoomCode = room.roomCode,
            HostUsername = room.leaderUsername,
            MemberCount = room.joinedPlayers,
            MaxPlayers = room.maxPlayers,
            IsHost = Profile != null && !string.IsNullOrEmpty(room.leaderUsername)
                && string.Equals(Profile.username, room.leaderUsername, StringComparison.Ordinal),
            Status = room.status,
            Mode = room.mode,
            MapId = room.mapId,
            KillTarget = room.killTarget,
            TimeLimitMinutes = room.timeLimitMinutes,
            RoomVersion = room.roomVersion,
            MatchId = room.matchId,
            MatchGeneration = room.matchGeneration,
            TeamId = you?.teamId,
            IsReady = you?.isReady ?? false,
        };
    }
}

/// <summary>房间状态字面值（客户端镜像 Docs/27 §3；与后端 RoomStatus 一致）。</summary>
public static class RoomStatus
{
    public const string Waiting = "Waiting";
    public const string Starting = "Starting";
    public const string InMatch = "InMatch";
    public const string Returning = "Returning";
    public const string Closed = "Closed";
}

/// <summary>队伍标识字面值（Docs/27 §1 冻结值；客户端镜像）。</summary>
public static class TeamId
{
    public const string None = "None";
    public const string Red = "Red";
    public const string Blue = "Blue";
}

/// <summary>对局模式字面值（Docs/27 §1 白名单；客户端镜像）。</summary>
public static class GameModes
{
    public const string Tdm = "TDM";
    public const string KillRace = "KillRace";
    public static bool IsTeamMode(string mode) => mode == Tdm;
}

/// <summary>房间会话快照（CF 等待房间）：退出事务的 leave 依据、等待房间页展示与返房判定数据。</summary>
public sealed class RoomSessionState
{
    public string RoomId { get; set; }
    public string RoomCode { get; set; }
    public string HostUsername { get; set; }
    public int MemberCount { get; set; }
    public int MaxPlayers { get; set; }
    public bool IsHost { get; set; }
    // ---- CF 扩展（Docs/27 v1.2）----
    public string Status { get; set; }
    public string Mode { get; set; }
    public string MapId { get; set; }
    public int KillTarget { get; set; }
    public int TimeLimitMinutes { get; set; }
    public long RoomVersion { get; set; }
    public string MatchId { get; set; }
    public int MatchGeneration { get; set; }
    public string TeamId { get; set; }
    public bool IsReady { get; set; }
}
}
