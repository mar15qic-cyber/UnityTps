using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 热更地图 bundle 构建（2026-09-17 热更试点 P4）：
    /// 把"热更地图清单"场景构建为 AssetBundle（maps/&lt;SceneName&gt;.bundle，ChunkBasedCompression），
    /// 产物写 Logs/HotUpdate/bundles/，由 Tools/HotUpdate/Publish-HotUpdate.ps1 一并打进热更包。
    /// ★ 这些场景不进客户端 EditorBuildSettings（内置走 SceneManager，热更走 bundle 双通道）；
    /// DS 构建仍需在 DedicatedServerBuild.CombatScenes 登记（服务器无法被客户端热更）。
    /// </summary>
    public static class HotUpdateBundleBuild
    {
        /// <summary>热更地图清单：新增热更地图在此登记（场景文件须存在）。</summary>
        private static readonly string[] HotMapScenes =
        {
            "Assets/_Project/Scenes/Map_TrainingYard.unity",
            "Assets/_Project/Scenes/Map_NightRelay.unity",
        };

        [MenuItem("Tools/HotUpdate/Build Map Bundles")]
        public static void BuildMaps()
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outDir = Path.Combine(projectRoot, "Logs", "HotUpdate", "bundles");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var builds = HotMapScenes.Select(scenePath => new AssetBundleBuild
            {
                assetBundleName = "maps/" + Path.GetFileNameWithoutExtension(scenePath) + ".bundle",
                assetNames = new[] { scenePath },
            }).ToArray();

            foreach (var scene in HotMapScenes)
            {
                if (!File.Exists(scene))
                {
                    Debug.LogError($"[HotUpdateBundleBuild] 找不到场景 {scene}，拒绝构建");
                    return;
                }
            }

            var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null || manifest.GetAllAssetBundles().Length != builds.Length)
            {
                Debug.LogError("[HotUpdateBundleBuild] bundle 构建结果与清单不符");
                return;
            }
            var names = string.Join(", ", manifest.GetAllAssetBundles());
            Debug.Log($"[HotUpdateBundleBuild] BUNDLE_OK dir={outDir} bundles=[{names}]");
        }
    }
}
