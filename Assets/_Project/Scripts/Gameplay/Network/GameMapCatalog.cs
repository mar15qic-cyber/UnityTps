using System;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 地图目录（Phase 8，Docs/27 §4 的 Unity 侧只读镜像）：mapId → 场景名解析。
    /// 与后端 MapCatalog.cs 的键值逐字对齐（sceneName 双端必须一致）；
    /// asmdef 不引用后端工程，故本地镜像——修改任一侧必须同步另一侧（本注释即同步契约）。
    /// 未匹配 mapId 回退 arena（不 fail 启动：非法参数只让实例被后端租用匹配闲置，日志可见）。
    /// </summary>
    public static class GameMapCatalog
    {
        private static readonly (string MapId, string SceneName)[] Maps =
        {
            ("arena", "Arena"),
            ("map_01", "Map_Stackyard"),
            ("map_02", "Map_Depot55"),
            ("map_03", "Map_Ridgeline"),
            // map_04（Map_TrainingYard，2026-09-17 热更试点 P4）：客户端不进 EditorBuildSettings——
            // 走热更 bundle 双通道（HotSceneLoader）；本行供 DS 解析与客户端 /api/maps 缓存缺失兜底。
            ("map_04", "Map_TrainingYard"),
            ("map_05", "Map_NightRelay"),
        };

        public static bool IsGameplayScene(string sceneName)
        {
            if (!string.IsNullOrEmpty(sceneName) && sceneName == Environment.GetEnvironmentVariable("FPS_MAP_SCENE")) return true;
            foreach (var map in Maps)
                if (string.Equals(map.SceneName, sceneName, StringComparison.Ordinal)) return true;
            var launch = NetworkLaunchContext.PeekClientLaunch();
            return (launch != null && launch.SceneName == sceneName) || (!string.IsNullOrEmpty(NetworkLaunchContext.CurrentGameplayScene) && NetworkLaunchContext.CurrentGameplayScene == sceneName);
        }

        public static bool TryGetSceneName(string mapId, out string sceneName)
        {
            sceneName = null;
            if (string.IsNullOrWhiteSpace(mapId)) return false;
            foreach (var map in Maps)
            {
                if (string.Equals(map.MapId, mapId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    sceneName = map.SceneName;
                    return true;
                }
            }
            return false;
        }

        /// <summary>解析失败回退 arena 场景（调用方日志应携带原始 mapId 便于排障）。</summary>
        public static string ResolveSceneName(string mapId)
        {
            if (TryGetSceneName(mapId, out var sceneName)) return sceneName;
            var dynamicMap = Environment.GetEnvironmentVariable("FPS_MAP_ID");
            var dynamicScene = Environment.GetEnvironmentVariable("FPS_MAP_SCENE");
            if (mapId == dynamicMap && System.Text.RegularExpressions.Regex.IsMatch(dynamicScene ?? "", "^[a-zA-Z0-9_]{1,80}$")) return dynamicScene;
            throw new InvalidOperationException("UNKNOWN_MAP: " + mapId);
        }
    }
}
