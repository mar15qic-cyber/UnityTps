using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.UI
{
    /// <summary>热更清单 DTO（manifest.json，由 Tools/HotUpdate/Publish-HotUpdate.ps1 生成）。
    /// JsonUtility 序列化契约：字段名即 json 键。version 为单调递增整数串（"1","2"...），
    /// 服务器端每个版本一个目录（/hotupdate/&lt;version&gt;/&lt;path&gt;），版本目录内容不可变
    /// ——回滚 = 服务器把 manifest.json 换回旧版本，客户端按 DowngradeRejected 保护本地。</summary>
    [Serializable]
    public sealed class HotUpdateManifest
    {
        public string version = "0";
        public string releaseId = "";
        public string protocolId = "";
        public string minClientVersion = "0.0.0";
        public HotUpdateFileEntry[] files = Array.Empty<HotUpdateFileEntry>();

        [Serializable]
        public sealed class HotUpdateFileEntry
        {
            public string path = "";
            public string hash = "";
            public long size;
        }
    }

    /// <summary>清单决策纯逻辑（无 IO/网络，EditMode 直测）：版本门、降级保护、差异计算、
    /// 哈希与相对路径消毒。下载器（HotUpdateBootstrap）只做搬运，不藏决策。</summary>
    public static class HotUpdatePlan
    {
        public enum DecisionKind
        {
            UpToDate,
            Download,
            DowngradeRejected,
            MinClientGate,
        }

        public sealed class Plan
        {
            public DecisionKind Kind;
            public HotUpdateManifest Remote;
            public readonly List<HotUpdateManifest.HotUpdateFileEntry> Changed = new();
        }

        /// <summary>热更版本比较（整数串；解析失败按字符串序，恒定规则）。</summary>
        public static int CompareHotVersion(string a, string b)
        {
            if (long.TryParse(a, out var la) && long.TryParse(b, out var lb)) return la.CompareTo(lb);
            return string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
        }

        /// <summary>客户端三段版本比较（major.minor.patch，非数字段按 0 容错）。</summary>
        public static int CompareClientVersion(string a, string b)
        {
            var pa = (a ?? "0").Split('.');
            var pb = (b ?? "0").Split('.');
            for (int i = 0; i < 3; i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }

        public static Plan Decide(HotUpdateManifest remote, HotUpdateManifest installed, string clientVersion)
        {
            if (remote == null || remote.files == null || remote.files.Length == 0)
                return new Plan { Kind = DecisionKind.UpToDate, Remote = remote }; // 空清单视为无需更新（fail open 到内置）

            // 整包更新门：远端要求的最低客户端版本高于本机 → 热更让路（v1 日志级提示，不阻断）
            if (CompareClientVersion(remote.minClientVersion ?? "0.0.0", clientVersion ?? "0.0.0") > 0)
                return new Plan { Kind = DecisionKind.MinClientGate, Remote = remote };

            if (installed != null && CompareHotVersion(installed.version, remote.version) > 0)
                return new Plan { Kind = DecisionKind.DowngradeRejected, Remote = remote }; // 服务器回滚中：保本地已装版本

            // 差异 = 内容寻址（哈希比对）：已装清单记录的哈希一致即视为本地已有该文件，
            // 不论版本号——installed.json 只在下载校验全部成功后写入，可信。
            var installedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (installed?.files != null)
                foreach (var f in installed.files)
                    if (!string.IsNullOrWhiteSpace(f.path))
                        installedByPath[f.path] = f.hash ?? string.Empty;

            var plan = new Plan { Kind = DecisionKind.Download, Remote = remote };
            foreach (var file in remote.files)
            {
                if (string.IsNullOrWhiteSpace(file.path)) continue;
                installedByPath.TryGetValue(file.path, out var localHash);
                if (string.Equals(localHash, file.hash, StringComparison.OrdinalIgnoreCase)) continue;
                plan.Changed.Add(file);
            }
            if (plan.Changed.Count == 0) plan.Kind = DecisionKind.UpToDate;
            return plan;
        }

        /// <summary>SHA256 十六进制（小写）校验。</summary>
        public static bool VerifyHash(byte[] data, string expectedHex)
        {
            if (data == null || string.IsNullOrWhiteSpace(expectedHex)) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var actual = sha.ComputeHash(data);
                var sb = new System.Text.StringBuilder(actual.Length * 2);
                foreach (var b in actual) sb.Append(b.ToString("x2"));
                return string.Equals(sb.ToString(), expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
            }
        }

        /// <summary>相对路径消毒：拒绝目录穿越/盘符/绝对路径（热更文件只允许落在版本目录内的子路径）。</summary>
        public static string SanitizeRelativePath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("hot update file path empty");
            if (normalized.Contains("..") || normalized.Contains(":")) throw new InvalidOperationException("hot update file path rejected: " + path);
            return normalized;
        }
    }
}
