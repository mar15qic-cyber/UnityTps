using UnityFps.Api.Common;
using UnityFps.Api.Data;

namespace UnityFps.Api.Features;

/// <summary>
/// 地图目录（Docs/27 §4）：服务端常量白名单。新地图只加目录项，不新增房间逻辑；
/// 协议与 UI 只允许 mapId，禁止硬编码场景路径。
/// </summary>
public static class MapCatalog
{
    public const string DefaultMapId = "arena";

    public static readonly MapCatalogDto Arena = new(
        MapId: "arena", DisplayName: "Arena", SceneName: "Arena",
        Modes: [GameModes.Tdm, GameModes.KillRace], MaxCapacity: 16, SpawnGroups: ["Red", "Blue", "FFA"]);

    // Phase 8 三张新地图（Docs/27 §4 契约：只加目录项，不新增房间逻辑）；
    // SceneName 必须与 Unity 侧 EditorBuildSettings 登记的场景名逐字一致。
    public static readonly MapCatalogDto Stackyard = new(
        MapId: "map_01", DisplayName: "Stackyard", SceneName: "Map_Stackyard",
        Modes: [GameModes.Tdm], MaxCapacity: 8, SpawnGroups: ["Red", "Blue"]);

    public static readonly MapCatalogDto Depot55 = new(
        MapId: "map_02", DisplayName: "Depot 55", SceneName: "Map_Depot55",
        Modes: [GameModes.KillRace], MaxCapacity: 16, SpawnGroups: ["Red", "Blue", "FFA"]);

    public static readonly MapCatalogDto Ridgeline = new(
        MapId: "map_03", DisplayName: "Ridgeline", SceneName: "Map_Ridgeline",
        Modes: [GameModes.Tdm], MaxCapacity: 8, SpawnGroups: ["Red", "Blue"]);

    // 2026-09-17 热更试点 P4：热更地图——客户端经热更 bundle 加载场景（不进客户端构建），
    // DS 随构建分发；SceneName 与 GameMapCatalog/热更 bundle 命名逐字一致。
    public static readonly MapCatalogDto TrainingYard = new(
        MapId: "map_04", DisplayName: "Training Yard", SceneName: "Map_TrainingYard",
        Modes: [GameModes.Tdm], MaxCapacity: 8, SpawnGroups: ["Red", "Blue"]);

    public static readonly MapCatalogDto NightRelay = new(
        MapId: "map_05", DisplayName: "Night Relay", SceneName: "Map_NightRelay",
        Modes: [GameModes.KillRace], MaxCapacity: 16, SpawnGroups: ["Red", "Blue", "FFA"]);

    private static readonly IReadOnlyList<MapCatalogDto> Builtin = [Arena, Stackyard, Depot55, Ridgeline, TrainingYard, NightRelay];
    public static IReadOnlyList<MapCatalogDto> All => PublishedMapCatalog.Read(Builtin);

    public static MapCatalogDto? Find(string? mapId) =>
        All.FirstOrDefault(x => string.Equals(x.MapId, mapId?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 房间设置白名单校验（Docs/27 §1/§5.4）：逐字段判定，全部非法取值抛 422 并带字段名；
/// 纯静态便于单测。容量白名单 {2,4,8,12,16}；TDM 每队上限 = 容量/2。
/// </summary>
public static class RoomSettingRules
{
    public static readonly int[] CapacityWhitelist = [2, 4, 8, 12, 16];

    public static bool IsValidCapacity(int maxPlayers) => CapacityWhitelist.Contains(maxPlayers);

    public static bool IsValidKillTarget(string mode, int killTarget) => mode switch
    {
        GameModes.Tdm => killTarget is 50 or 100 or 150,
        GameModes.KillRace => killTarget is 10 or 20 or 30,
        _ => false,
    };

    public static bool IsValidTimeLimit(string mode, int minutes) => mode switch
    {
        GameModes.Tdm => minutes is 5 or 10 or 15,
        GameModes.KillRace => minutes is 5 or 10,
        _ => false,
    };

    public static int PerTeamCapacity(int maxPlayers) => maxPlayers / 2;

    /// <summary>整体校验一组设置（创建用）；模式白名单外抛 MODE_INVALID，其余字段抛 SETTING_INVALID，消息含字段名。</summary>
    public static (string Mode, string MapId, int KillTarget, int TimeLimitMinutes, int MaxPlayers) Validate(
        string mode, string mapId, int killTarget, int timeLimitMinutes, int maxPlayers)
    {
        if (!GameModes.All.Contains(mode))
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.ModeInvalid,
                $"模式 {mode} 不在白名单内");
        if (MapCatalog.Find(mapId) is not { } map)
            throw Error("mapId");
        if (!map.Modes.Contains(mode))
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.MapNotAllowed,
                $"地图 {map.MapId} 不支持模式 {mode}");
        if (!IsValidCapacity(maxPlayers) || maxPlayers > map.MaxCapacity)
            throw Error("maxPlayers");
        if (!IsValidKillTarget(mode, killTarget))
            throw Error("killTarget");
        if (!IsValidTimeLimit(mode, timeLimitMinutes))
            throw Error("timeLimitMinutes");
        return (mode, map.MapId, killTarget, timeLimitMinutes, maxPlayers);
    }

    private static ApiException Error(string field) =>
        new(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.SettingInvalid, $"设置项 {field} 不在白名单内");
}
