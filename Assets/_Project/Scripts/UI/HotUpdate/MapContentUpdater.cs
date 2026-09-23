using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.UI
{
    /// <summary>Map-only content channel. Never changes the Lua loader root of a running client.</summary>
    public static class MapContentUpdater
    {
        public static bool Busy { get; private set; }
        public static string Status { get; private set; } = "";
        private static string Root => Path.Combine(HotUpdateBootstrap.HotFilesRootBase, "MapContent");
        public static string FindBundle(string scene)
        {
            if (string.IsNullOrWhiteSpace(scene) || Path.GetFileName(scene) != scene) return null;
            var installed = Installed();
            if (installed != null && HotUpdateInstaller.ValidateVersionStrict(installed.version) == null)
            {
                var path = Path.Combine(Root, installed.version, "maps", scene.ToLowerInvariant() + ".bundle");
                if (File.Exists(path)) return path;
            }
            var fallback = HotUpdateRuntime.HotFilesRoot;
            return string.IsNullOrEmpty(fallback) ? null : Path.Combine(fallback, "maps", scene + ".bundle");
        }
        private static HotUpdateManifest Installed()
        {
            try { return JsonUtility.FromJson<HotUpdateManifest>(File.ReadAllText(Path.Combine(Root, "installed.json"))); }
            catch { return null; }
        }
        public static async Task<bool> CheckAsync(CancellationToken token)
        {
            if (Busy) return false;
            var env = Game.Core.ClientReleaseEnvironment.Current;
            if (env == null) return false;
            Busy = true;
            try
            {
                Status = "正在检查地图更新…";
                var data = await Download(env.hotUpdateBaseUrl.TrimEnd('/') + "/maps-manifest.json", token);
                var json = System.Text.Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
                var remote = JsonUtility.FromJson<HotUpdateManifest>(json);
                if (HotUpdateInstaller.ValidateManifest(remote) != null || remote.releaseId != env.releaseId
                    || remote.protocolId != Game.Gameplay.Network.GameProtocolIdentity.ProtocolId
                    || remote.files.Any(f => !f.path.StartsWith("maps/", StringComparison.Ordinal) || !f.path.EndsWith(".bundle", StringComparison.Ordinal)))
                    throw new InvalidOperationException("地图更新不兼容");
                var old = Installed();
                var plan = HotUpdatePlan.Decide(remote, old, HotUpdateRuntime.ClientVersion);
                if (plan.Kind == HotUpdatePlan.DecisionKind.UpToDate) { Status = "地图已是最新"; return true; }
                if (plan.Kind != HotUpdatePlan.DecisionKind.Download || old != null && HotUpdatePlan.CompareHotVersion(remote.version, old.version) <= 0)
                    throw new InvalidOperationException("地图版本不兼容，请联系主机");
                int done = 0;
                var result = await HotUpdateInstaller.InstallAsync(remote, json, Root, old,
                    old == null ? null : Path.Combine(Root, old.version), async file =>
                    {
                        Status = "正在下载地图 " + (++done) + "/" + remote.files.Length;
                        return await Download(env.hotUpdateBaseUrl.TrimEnd('/') + "/" + remote.version + "/" + file.path, token);
                    }, token);
                if (!result.Success) throw new InvalidOperationException("地图下载未完成，原版本已保留");
                Status = "地图更新完成"; return true;
            }
            catch (OperationCanceledException) { Status = "地图更新已取消"; return false; }
            catch { Status = "地图更新暂不可用，已保留当前版本"; return false; }
            finally { Busy = false; }
        }
        private static async Task<byte[]> Download(string url, CancellationToken token)
        {
            using var request = UnityWebRequest.Get(url);
            request.timeout = 300;
            var operation = request.SendWebRequest();
            while (!operation.isDone) { if (token.IsCancellationRequested) { request.Abort(); token.ThrowIfCancellationRequested(); } await Task.Yield(); }
            token.ThrowIfCancellationRequested();
            if (request.result != UnityWebRequest.Result.Success) throw new IOException("MAP_DOWNLOAD_FAILED");
            return request.downloadHandler.data;
        }
    }
}
