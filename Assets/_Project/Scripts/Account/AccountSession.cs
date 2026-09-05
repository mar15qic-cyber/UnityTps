using System;

namespace Game.Account
{

public sealed class AccountSession
{
    public string Token { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public PlayerProfileDto Profile { get; private set; }
    public LoadoutDto Loadout { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && ExpiresAtUtc > DateTime.UtcNow;
    public string GameplayError { get; private set; }

    /// <summary>当前所在房间（Phase E：建房/加入后缓存；离开/清空会话时清除）。</summary>
    public RoomSessionState Room { get; private set; }

    public event Action Changed;

    public void Apply(AuthSessionDto session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        Token = session.token;
        ExpiresAtUtc = DateTime.TryParse(session.expiresAtUtc, out var expiry) ? expiry.ToUniversalTime() : DateTime.UtcNow.AddHours(12);
        Profile = session.profile;
        Loadout = session.loadout;
        Changed?.Invoke();
    }

    public void ApplyProfile(PlayerProfileDto profile)
    {
        Profile = profile;
        Changed?.Invoke();
    }

    public void ApplyLoadout(LoadoutDto loadout)
    {
        Loadout = loadout;
        Changed?.Invoke();
    }

    /// <summary>缓存当前房间（isHost = 后端 hostUsername 与本地档案 username 一致）。</summary>
    public void ApplyRoom(GameRoomDto room)
    {
        if (room == null) { Room = null; Changed?.Invoke(); return; }
        Room = new RoomSessionState
        {
            RoomCode = room.roomCode,
            HostUsername = room.hostUsername,
            HostAddress = room.hostAddress,
            HostPort = room.hostPort,
            MemberCount = room.joinedPlayers,
            MaxPlayers = room.maxPlayers,
            IsHost = Profile != null && !string.IsNullOrEmpty(room.hostUsername)
                && string.Equals(Profile.username, room.hostUsername, StringComparison.Ordinal),
        };
        Changed?.Invoke();
    }

    public void ClearRoom()
    {
        if (Room == null) return;
        Room = null;
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
        GameplayError = null;
        Room = null;
        Changed?.Invoke();
    }
}

/// <summary>房间会话快照（Phase E）：退出对局时 LeaveRoomAsync 的调用依据与 UI 展示数据。</summary>
public sealed class RoomSessionState
{
    public string RoomCode { get; set; }
    public string HostUsername { get; set; }
    public string HostAddress { get; set; }
    public int HostPort { get; set; }
    public int MemberCount { get; set; }
    public int MaxPlayers { get; set; }
    public bool IsHost { get; set; }
}
}
