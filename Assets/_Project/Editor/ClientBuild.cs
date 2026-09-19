using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Windows 客户端构建工具（Day4 后 CF 等待房间契约版本的批处理入口）。
    /// 场景清单取 Build Profiles 的 Scene List（Boot/Lobby/Arena 全流程），
    /// 与 DedicatedServerBuild（仅 Arena）互补；产物写入已忽略的 Builds/ 下，不提交二进制。
    /// 注意：批处理构建会改写渲染管线相关 ProjectSettings，构建前须备份、构建后还原。
    /// </summary>
    public static class ClientBuild
    {
        private const string OutputDir = "Builds/ReleaseClient";
        private const string ExeName = "UnityFpsClient.exe";

        [MenuItem("Tools/Client/Build Windows Client (Release)")]
        public static void BuildWindowsClient() => Build(development: false);

        [MenuItem("Tools/Client/Build Windows Client (Development)")]
        public static void BuildWindowsClientDevelopment() => Build(development: true);

        private static void Build(bool development)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[ClientBuild] Build Profiles 场景清单为空，拒绝构建（请先在 Build Profiles 配置 Boot/Lobby/Arena）");
                return;
            }

            Directory.CreateDirectory(OutputDir);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(OutputDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            Debug.Log($"[ClientBuild] building → {options.locationPathName} (development={development}, scenes={string.Join(",", scenes)})");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[ClientBuild] 构建失败：result={report.summary.result} "
                    + $"errors={report.summary.totalErrors} size={report.summary.totalSize} bytes");
                return;
            }

            Debug.Log($"[ClientBuild] BUILD_OK {options.locationPathName} "
                + $"size={report.summary.totalSize} bytes scenes={scenes.Length}");

            // P0-A：产物身份落盘（协议代际 + 业务程序集哈希 + 构建时间）——部署门/启动日志消费
            BuildManifestWriter.WriteManifest(OutputDir, "Player" + (development ? "-Development" : string.Empty));
        }
    }
}
