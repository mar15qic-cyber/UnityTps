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

        public static string HotFilesRootBase => Path.Combine(Application.persistentDataPath, "HotFiles", Game.Core.ClientReleaseEnvironment.Current?.environmentId ?? "local");

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
                var release = Game.Core.ClientReleaseEnvironment.Current;
                if (release?.RequiresReleaseValidation == true) urlOverride = release.hotUpdateBaseUrl;
                else if (string.IsNullOrEmpty(urlOverride)) urlOverride = ResolveUrlOverride();
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
                var baseUrl = (string.IsNullOrEmpty(urlOverride) ? (release?.hotUpdateBaseUrl ?? DefaultBaseUrl) : urlOverride).TrimEnd('/');

                // 1) 远端清单（不可达 → 回退已装/内置，不阻塞进大厅）
                // F04（2026-09-19 审计）：所有失败出口必须恢复已安装根目录——新进程
                // HotFilesRoot 初值为 null，不 ApplyInstalledRoot 会让已装热更包退回内置脚本。
                var manifestJson = await DownloadTextAsync(baseUrl + "/manifest.json", 5f);
                if (manifestJson == null)
                {
                    result.Kind = FallbackKind();
                    ApplyInstalledRoot();
                    return Finish(result);
                }
                HotUpdateManifest remote = null;
                try { remote = JsonUtility.FromJson<HotUpdateManifest>(manifestJson); } catch { /* 解析失败按不可达处理 */ }
                if (remote == null || remote.files == null || remote.files.Length == 0)
                {
                    result.Kind = FallbackKind();
                    ApplyInstalledRoot();
                    return Finish(result);
                }
                // F18：清单结构整体校验（版本严格单段纯数字/路径消毒/size/hash/重复路径）——
                // 校验失败视同坏 manifest，整包拒绝，不做任何下载。
                if (release?.RequiresReleaseValidation == true && remote.releaseId != release.releaseId)
                {
                    result.Kind = "release-mismatch";
                    result.Error = "Client and hot-update release identities differ";
                    return Finish(result);
                }
                var manifestInvalid = HotUpdateInstaller.ValidateManifest(remote);
                if (manifestInvalid != null)
                {
                    result.Kind = FallbackKind();
                    result.Error = manifestInvalid;
                    ApplyInstalledRoot();
                    return Finish(result);
                }

                // 2) 决策（纯逻辑，可测）
                var installed = LoadInstalledManifest();
                var plan = HotUpdatePlan.Decide(remote, installed, HotUpdateRuntime.ClientVersion);
                var installedDirectory = InstalledVersionDir(installed);
                bool repair = plan.Kind == HotUpdatePlan.DecisionKind.UpToDate
                    && !await Task.Run(() => HotUpdateInstaller.DirectoryMatches(installedDirectory, installed));
                if (repair) plan.Kind = HotUpdatePlan.DecisionKind.Download;
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
                        // F03：同内容新版本不得指向未创建的新目录——本地已装版本目录才是根真相；
                        // 缺这步时冷启动 HotFilesRoot=null → 混入内置旧脚本/丢已装地图。
                        result.Kind = "uptodate";
                        result.Version = installed?.version ?? remote.version;
                        ApplyInstalledRoot();
                        return Finish(result);
                    case HotUpdatePlan.DecisionKind.Download:
                        // F05：同版本异内容 = 发布事故/篡改——拒绝安装，保住当前可用包
                        if (installed != null && HotUpdatePlan.CompareHotVersion(installed.version, remote.version) == 0
                            && !HotUpdateInstaller.SameContentIdentity(remote, installed))
                        {
                            result.Kind = "same-version-conflict";
                            result.Version = installed.version;
                            result.Error = "same version republished with different content (server-side republish is forbidden)";
                            ApplyInstalledRoot();
                            return Finish(result);
                        }
                        // 3) 安装事务：staging 完整物化（未变化文件逐字节复验后复制）→ 全量复验 →
                        //    原子发布 → 指针原子替换（F03/F05/F18 的 IO 细节全部在 HotUpdateInstaller）
                        var remoteVersion = remote.version;
                        var baseForFetch = baseUrl;
                        Func<HotUpdateManifest.HotUpdateFileEntry, Task<byte[]>> fetch = async file =>
                        {
                            // 超时按体积缩放（100KB/s 下限带宽假设）：小文件 20s、大 bundle 按比例放宽
                            var timeoutSeconds = Mathf.Max(20f, file.size / (100f * 1024f));
                            return await DownloadBytesAsync($"{baseForFetch}/{remoteVersion}/{file.path}", timeoutSeconds);
                        };
                        var install = await HotUpdateInstaller.InstallAsync(
                            remote, manifestJson, HotFilesRootBase, installed, InstalledVersionDir(installed), fetch);
                        if (!install.Success)
                        {
                            result.Kind = FallbackKind();
                            result.Error = install.Error;
                            result.FilesDownloaded = install.FilesDownloaded;
                            ApplyInstalledRoot();
                            return Finish(result);
                        }
                        result.FilesDownloaded = install.FilesDownloaded;
                        result.Kind = "applied";
                        result.Version = remote.version;
                        ApplyRootFor(remote.version);
                        return Finish(result);
                }

                // 不可达决策分支（Decide 返回穷举）——按失败回退
                result.Kind = FallbackKind();
                ApplyInstalledRoot();
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

        private static string FallbackKind() => InstalledVersion() != null ? "fallback-installed" : "fallback-builtin";

        private static string InstalledVersionDir(HotUpdateManifest installed)
        {
            if (installed == null || HotUpdateInstaller.ValidateVersionStrict(installed.version) != null) return null;
            var dir = Path.Combine(HotFilesRootBase, installed.version);
            return Directory.Exists(dir) ? dir : null;
        }

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
            HotUpdateRuntime.HotFilesRoot = null;
            if (HotUpdateInstaller.DirectoryMatches(InstalledVersionDir(installed), installed)) ApplyRootFor(installed.version);
        }

        private static void ApplyRootFor(string version)
        {
            // F18：版本参与路径拼接——严格单段校验 + 规范化后必须仍位于 HotFiles 根内
            if (HotUpdateInstaller.ValidateVersionStrict(version) != null) return;
            var rootFull = Path.GetFullPath(HotFilesRootBase);
            var dir = Path.GetFullPath(Path.Combine(rootFull, version));
            if (!dir.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
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
