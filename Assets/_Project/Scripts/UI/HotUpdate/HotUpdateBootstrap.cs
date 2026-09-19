using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.UI
{
    /// <summary>
    /// Boot 阶段热更下载器（稳定 seam）：拉远端 manifest → 纯逻辑决策（HotUpdatePlan）→
    /// 差异下载到 persistentDataPath/HotFiles/&lt;version&gt;/ → SHA256 校验 → 全部成功才写
    /// installed.json 指针（原子性：失败/中途退出都不会污染已装版本）→ 设置
    /// HotUpdateRuntime.HotFilesRoot 供 LuaEnv loader 使用。
    /// 失败策略（可玩性优先）：远端不可达/校验失败 → 用已装版本；从未装过 → 内置 Resources/Lua。
    /// ★ 时序红线：必须在 AppRoot.Ensure() 之前完成——AppRoot.Awake 挂 HotUpdateRuntime 时
    /// LuaEnv loader 读 HotFilesRoot。
    /// 编辑器默认跳过（快速迭代用内置脚本），可用 -hotupdateUrl=&lt;url&gt; 强制启用（off=禁用）。
    /// </summary>
    public static class HotUpdateBootstrap
    {
        public const string DefaultBaseUrl = "http://127.0.0.1:5080/hotupdate";

        public sealed class Result
        {
            public string Kind = "skipped-editor"; // applied/uptodate/fallback-installed/fallback-builtin/min-client-gate/downgrade-rejected/disabled/error
            public string Version;
            public int FilesDownloaded;
            public string Error;
        }

        public static string HotFilesRootBase => Path.Combine(Application.persistentDataPath, "HotFiles");

        /// <summary>命令行覆盖（-hotupdateUrl=...；off=禁用）。返回 null = 未指定。</summary>
        public static string ResolveUrlOverride()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-hotupdateUrl", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>热更检查主入口。urlOverride：显式指定热更服务地址（测试/编辑器验证用）；
        /// null = 走命令行 -hotupdateUrl，再退默认值。</summary>
        public static async Task<Result> CheckAndApplyAsync(string urlOverride = null)
        {
            var result = new Result();
            try
            {
                if (string.IsNullOrEmpty(urlOverride)) urlOverride = ResolveUrlOverride();
                if (string.Equals(urlOverride, "off", StringComparison.OrdinalIgnoreCase))
                {
                    result.Kind = "disabled";
                    return Finish(result);
                }
                if (Application.isEditor && string.IsNullOrEmpty(urlOverride))
                {
                    result.Kind = "skipped-editor"; // 编辑器快速迭代：直接用内置脚本
                    return Finish(result);
                }
                var baseUrl = (string.IsNullOrEmpty(urlOverride) ? DefaultBaseUrl : urlOverride).TrimEnd('/');

                // 1) 远端清单（不可达 → 回退已装/内置，不阻塞进大厅）
                var manifestJson = await DownloadTextAsync(baseUrl + "/manifest.json", 5f);
                if (manifestJson == null)
                {
                    result.Kind = InstalledVersion() != null ? "fallback-installed" : "fallback-builtin";
                    return Finish(result);
                }
                HotUpdateManifest remote = null;
                try { remote = JsonUtility.FromJson<HotUpdateManifest>(manifestJson); } catch { /* 解析失败按不可达处理 */ }
                if (remote == null || remote.files == null || remote.files.Length == 0)
                {
                    result.Kind = InstalledVersion() != null ? "fallback-installed" : "fallback-builtin";
                    return Finish(result);
                }

                // 2) 决策（纯逻辑，可测）
                var installed = LoadInstalledManifest();
                var plan = HotUpdatePlan.Decide(remote, installed, HotUpdateRuntime.ClientVersion);
                switch (plan.Kind)
                {
                    case HotUpdatePlan.DecisionKind.MinClientGate:
                        result.Kind = "min-client-gate";
                        ApplyInstalledRoot();
                        return Finish(result);
                    case HotUpdatePlan.DecisionKind.DowngradeRejected:
                        result.Kind = "downgrade-rejected";
                        result.Version = installed.version;
                        ApplyInstalledRoot();
                        return Finish(result);
                    case HotUpdatePlan.DecisionKind.UpToDate:
                        result.Kind = "uptodate";
                        result.Version = remote.version;
                        ApplyRootFor(remote.version);
                        return Finish(result);
                }

                // 3) 差异下载 → 校验 → 落盘（版本目录内容不可变；失败立即回退，不写指针）
                var targetDir = Path.Combine(HotFilesRootBase, remote.version);
                foreach (var file in plan.Changed)
                {
                    // 超时按体积缩放（100KB/s 下限带宽假设）：小文件 20s、大 bundle 按比例放宽
                    var timeoutSeconds = Mathf.Max(20f, file.size / (100f * 1024f));
                    var data = await DownloadBytesAsync($"{baseUrl}/{remote.version}/{file.path}", timeoutSeconds);
                    if (data == null || data.Length != file.size || !HotUpdatePlan.VerifyHash(data, file.hash))
                    {
                        result.Kind = InstalledVersion() != null ? "fallback-installed" : "fallback-builtin";
                        result.Error = $"file verify failed: {file.path}";
                        ApplyInstalledRoot();
                        return Finish(result);
                    }
                    var filePath = Path.Combine(targetDir, HotUpdatePlan.SanitizeRelativePath(file.path));
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    File.WriteAllBytes(filePath, data);
                    result.FilesDownloaded++;
                }

                // 4) 指针提交（最后一步：此前任何失败都保持旧指针）
                File.WriteAllText(InstalledManifestPath, manifestJson);
                result.Kind = "applied";
                result.Version = remote.version;
                ApplyRootFor(remote.version);
                return Finish(result);
            }
            catch (Exception e)
            {
                result.Kind = "error";
                result.Error = e.Message;
                ApplyInstalledRoot();
                return Finish(result);
            }
        }

        private static Result Finish(Result result)
        {
            Debug.Log($"[HotUpdate] kind={result.Kind} version={result.Version ?? "-"} downloaded={result.FilesDownloaded}"
                + (string.IsNullOrEmpty(result.Error) ? string.Empty : " error=" + result.Error)
                + $" luaRoot={(HotUpdateRuntime.HotFilesRoot ?? "<builtin>")}");
            return result;
        }

        private static string InstalledManifestPath => Path.Combine(HotFilesRootBase, "installed.json");

        private static HotUpdateManifest LoadInstalledManifest()
        {
            try
            {
                if (!File.Exists(InstalledManifestPath)) return null;
                return JsonUtility.FromJson<HotUpdateManifest>(File.ReadAllText(InstalledManifestPath));
            }
            catch { return null; }
        }

        private static string InstalledVersion() => LoadInstalledManifest()?.version;

        /// <summary>指针落地：HotFilesRoot 指向已装版本目录（目录缺失/为空则回退内置 null）。</summary>
        private static void ApplyInstalledRoot()
        {
            var installed = LoadInstalledManifest();
            if (installed != null) ApplyRootFor(installed.version);
        }

        private static void ApplyRootFor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            var dir = Path.Combine(HotFilesRootBase, version);
            if (Directory.Exists(dir)) HotUpdateRuntime.HotFilesRoot = dir;
        }

        private static async Task<string> DownloadTextAsync(string url, float timeoutSeconds)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Max(1, (int)timeoutSeconds);
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) return null;
                return request.downloadHandler.text;
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(string url, float timeoutSeconds)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Max(1, (int)timeoutSeconds);
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) return null;
                return request.downloadHandler.data;
            }
        }
    }
}
