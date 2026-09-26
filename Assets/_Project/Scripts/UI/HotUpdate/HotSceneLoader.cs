using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FishNet.Managing;
using Game.Account;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>客户端整包内置的战斗场景（SceneManager 直载）；不在清单内的地图 = 热更地图（bundle 加载）。
    /// ★ 语义（2026-09-17 热更试点 P4）：新增地图不进客户端 EditorBuildSettings——走热更包。</summary>
    public static class BuiltinScenes
    {
        public static readonly string[] Names =
        {
            "Arena",
            "Map_Stackyard",
            "Map_Depot55",
            "Map_Ridgeline",
        };

        public static bool Contains(string sceneName) =>
            !string.IsNullOrWhiteSpace(sceneName) && Array.IndexOf(Names, sceneName) >= 0;
    }

    /// <summary>/api/maps 目录缓存（后端权威，DTO 含 SceneName）：建房页地图按钮与入场解析的数据源。
    /// 缓存为空时 LobbyPresenter 后台拉取；GameMapCatalog（静态镜像）仅作 DS 侧与缓存缺失的兜底。</summary>
    public static class HotMapCatalog
    {
        private static volatile MapCatalogDto[] cached = Array.Empty<MapCatalogDto>();

        public static IReadOnlyList<MapCatalogDto> Cached => cached;

        public static void Store(MapCatalogDto[] value) => cached = value ?? Array.Empty<MapCatalogDto>();

        public static bool TryGet(string mapId, out MapCatalogDto entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(mapId)) return false;
            foreach (var item in cached)
            {
                if (item != null && string.Equals(item.mapId, mapId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    entry = item;
                    return true;
                }
            }
            return false;
        }

        /// <summary>mapId → 场景名（缓存优先，静态镜像兜底——老客户端语义保留）。</summary>
        public static bool TryGetSceneName(string mapId, out string sceneName)
        {
            sceneName = null;
            if (TryGet(mapId, out var entry) && !string.IsNullOrWhiteSpace(entry.sceneName))
            {
                sceneName = entry.sceneName;
                return true;
            }
            return Game.Gameplay.Network.GameMapCatalog.TryGetSceneName(mapId, out sceneName);
        }
    }

    /// <summary>热更地图场景加载（bundle 通道，稳定 seam）：bundle 文件由 P2 下载链路预先落盘
    /// （manifest 收录 maps/&lt;SceneName&gt;.bundle），此处只做本地加载与切场景，不再联网。
    /// 单 bundle 常驻（切地图时卸旧装新）；热更目录缺失该文件 = fail closed（回大厅提示更新）。</summary>
    public static class HotSceneLoader
    {
        private static AssetBundle loadedMapBundle;

        public static bool IsBundleScene(string sceneName) => !BuiltinScenes.Contains(sceneName);

        /// <summary>bundle 场景是否已就绪（文件存在于当前热更目录）。</summary>
        public static bool IsBundleReady(string sceneName)
        {
            var path = MapContentUpdater.FindBundle(sceneName);
            return path != null && File.Exists(path);
        }

        /// <summary>加载 bundle 场景（Single）。返回 false = 文件缺失/加载失败（调用方 fail closed）。</summary>
        public static async Task<bool> TryLoadBundleSceneAsync(string sceneName, Action<float> onProgress)
        {
            if (!IsBundleReady(sceneName)) return false;
            var path = MapContentUpdater.FindBundle(sceneName);
            try
            {
                // A scene bundle carries its own copy of NetworkSystems and the FishNet prefab
                // collection. If it is the first combat scene, that copy can become the persistent
                // NetworkManager while the dedicated server uses the one from built-in Arena.
                // Both ends must initialize from the same built-in prefab collection before the
                // bundled environment is loaded; the bundle's duplicate manager is then discarded.
                if (UnityEngine.Object.FindFirstObjectByType<NetworkManager>() == null)
                {
                    var bootstrap = SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
                    if (bootstrap == null) throw new InvalidOperationException("Built-in Arena bootstrap is unavailable");
                    while (!bootstrap.isDone)
                    {
                        onProgress?.Invoke(Mathf.Clamp01(bootstrap.progress / .9f) * .1f);
                        await Task.Yield();
                    }
                    if (UnityEngine.Object.FindFirstObjectByType<NetworkManager>() == null)
                        throw new InvalidOperationException("Built-in Arena NetworkManager is unavailable");
                }
                if (loadedMapBundle != null)
                {
                    // 换图卸旧（同图重进幂等：复用已加载 bundle）
                    if (!string.Equals(loadedMapBundle.name, sceneName, StringComparison.OrdinalIgnoreCase))
                    {
                        loadedMapBundle.Unload(false);
                        loadedMapBundle = null;
                    }
                }
                if (loadedMapBundle == null)
                {
                    var bundleRequest = AssetBundle.LoadFromFileAsync(path);
                    while (!bundleRequest.isDone)
                    {
                        onProgress?.Invoke(bundleRequest.progress * .2f);
                        await Task.Yield();
                    }
                    loadedMapBundle = bundleRequest.assetBundle;
                    if (loadedMapBundle == null)
                    {
                        Debug.LogError("[HotUpdate] map bundle load failed: " + path);
                        return false;
                    }
                }
                // Unity 语义：bundle LoadFromFile 后，SceneManager 按场景名即可找到 bundle 内场景
                var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                if (op == null)
                {
                    Debug.LogError("[HotUpdate] bundle scene not found: " + sceneName);
                    return false;
                }
                while (!op.isDone)
                {
                    onProgress?.Invoke(.2f + Mathf.Clamp01(op.progress / 0.9f) * .8f);
                    await Task.Yield();
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[HotUpdate] bundle scene load error (" + sceneName + "): " + e.Message);
                return false;
            }
        }
    }
}
