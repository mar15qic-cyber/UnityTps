using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Dedicated Server 构建工具（Docs/27 Day1 §4.1 C4）。
    /// 可重复构建 Windows x64 Dedicated Server：StandaloneBuildSubtarget.Server（自动定义 UNITY_SERVER，
    /// 运行时由 DedicatedServerBootstrap 检测并只启动 FishNet 服务器）。
    /// 场景仅含正式 Arena——服务器进程启动即进入唯一战斗场景（Docs/25），
    /// Boot/Lobby 属于客户端登录/大厅流程，不进服务器构建。
    /// 产物写入已忽略的 Builds/Server/，不提交二进制；不新增 Unity 包、不修改 Assets/FishNet。
    /// </summary>
    public static class DedicatedServerBuild
    {
        private const string ArenaScene = "Assets/_Project/Scenes/Arena.unity";
        private const string OutputDir = "Builds/Server";
        private const string ExeName = "UnityFpsDedicatedServer.exe";

        /// <summary>Phase 8：全部战斗场景（Arena + 三张新地图）——DS 按 -mapId 经 GameMapCatalog
        /// 解析后加载对应场景；Boot/Lobby 属客户端登录/大厅流程，不进服务器构建。
        /// 新增地图必须同时登记 EditorBuildSettings 与本清单（两侧缺一 DS 无法加载）。
        /// ★ 例外（2026-09-17 热更试点 P4）：热更地图（如 Map_TrainingYard）只登记本清单——
        /// 客户端不进 EditorBuildSettings（走热更 bundle），DS 无法被热更、必须随构建分发。</summary>
        private static readonly string[] CombatScenes =
        {
            ArenaScene,
            "Assets/_Project/Scenes/Map_Stackyard.unity",
            "Assets/_Project/Scenes/Map_Depot55.unity",
            "Assets/_Project/Scenes/Map_Ridgeline.unity",
            "Assets/_Project/Scenes/Map_TrainingYard.unity",
            "Assets/_Project/Scenes/Map_NightRelay.unity",
        };

        [MenuItem("Tools/Dedicated Server/Build Windows Server (Release)")]
        public static void BuildWindowsServerRelease()
        {
            BuildWindowsServer(development: false);
        }

        [MenuItem("Tools/Dedicated Server/Build Windows Server (Development)")]
        public static void BuildWindowsServerDevelopment()
        {
            BuildWindowsServer(development: true);
        }

        private static void BuildWindowsServer(bool development)
        {
            foreach (var scene in CombatScenes)
            {
                if (!File.Exists(scene))
                {
                    Debug.LogError($"[DedicatedServerBuild] 找不到战斗场景 {scene}，拒绝构建");
                    return;
                }
            }

            Directory.CreateDirectory(OutputDir);
            var options = new BuildPlayerOptions
            {
                scenes = CombatScenes,
                locationPathName = Path.Combine(OutputDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            Debug.Log($"[DedicatedServerBuild] building → {options.locationPathName} (development={development}) scenes={CombatScenes.Length}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[DedicatedServerBuild] 构建失败：result={report.summary.result} "
                    + $"errors={report.summary.totalErrors} size={report.summary.totalSize} bytes");
                return;
            }

            Debug.Log($"[DedicatedServerBuild] BUILD_OK {options.locationPathName} "
                + $"size={report.summary.totalSize} bytes scenes={CombatScenes.Length}({ArenaScene} + maps)");

            // P0-A：产物身份落盘（协议代际 + 业务程序集哈希 + 构建时间）——启动器部署门按此
            // 比对"运行进程 vs 目标构建"，防止旧进程被当成新构建已部署（2026-09-15 事故根因）
            BuildManifestWriter.WriteManifest(OutputDir, "Server" + (development ? "-Development" : string.Empty));
        }
    }
}
