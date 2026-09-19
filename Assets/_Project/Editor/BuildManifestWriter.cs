using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 构建清单写入器（2026-09-15 P0-A）：每次 Client/DS 构建成功后，把「本产物身份」落盘到
    /// 产物目录根的 build-manifest.json——协议代际（GameProtocolIdentity，两端同源相同）+
    /// 各业务程序集 SHA-256（两端分别记录，不要求相同）+ 构建时间/Unity 版本/subtarget。
    /// 部署门消费链：
    /// ① 启动器 Start-LocalServer.ps1 读取 DS 清单 → 与运行进程日志的 APP_PROTOCOL 行比对——
    ///    活跃旧进程（清单缺 APP_PROTOCOL 行/协议不一致）不得被当成新构建已部署；
    /// ② 运行中的进程启动时读自身清单打印 APP_PROTOCOL/buildId（DedicatedServerBootstrap / AppRoot）。
    /// F16（2026-09-19 审计）增量：清单新增 <see cref="BuildManifest.inputDigest"/>——构建输入
    /// 集合（Scripts/Editor 全部文件 + Packages/manifest.json + ProjectSettings/ProjectVersion +
    /// 内置 Lua）的逐文件内容摘要再归并摘要。BuildStatus 门据此做「源码 vs 构建」的【内容】比对
    /// （mtime 只是廉价提示）：缺字段/无法解析一律 Unknown/Invalid 且门禁阻止（fail closed），
    /// 不再以"没比较出差异"判 OK。热更包的输入清单由 Publish-HotUpdate.ps1 的 files/hash 承载。
    /// 协议 ID 调整纪律：改 RPC 签名/网络 DTO → GameProtocolIdentity.ProtocolId 递增 → 同批重建两端。
    /// </summary>
    public static class BuildManifestWriter
    {
        [Serializable]
        private sealed class BuildManifest
        {
            public string buildId;
            public string protocolId;
            public string builtAtUtc;
            public string unityVersion;
            public string subtarget;
            public string gamePlayDllSha256;
            public string gameUiDllSha256;
            public string gameAccountDllSha256;
            public string inputDigest;
            public int inputFileCount;
        }

        /// <summary>构建输入集合根（相对工程根）：受击体/玩法/UI/账号源码、编辑器构建脚本、
        /// 包依赖声明、Unity 版本、内置热更 Lua（打进 Resources 的部分）。</summary>
        private static readonly string[] InputRoots =
        {
            "Assets/_Project/Scripts",
            "Assets/_Project/Editor",
            "Assets/Resources/Lua",
            "Packages/manifest.json",
            "ProjectSettings/ProjectVersion.asset",
        };

        /// <summary>构建成功后调用：写入产物目录根的 build-manifest.json。产物缺失的程序集记 "&lt;absent&gt;。</summary>
        public static void WriteManifest(string outputDir, string subtarget)
        {
            try
            {
                var managedDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(outputDir) + "_Data", "Managed");
                // 产物 exe 名（UnityFpsDedicatedServer / UnityFpsClient）与目录名解耦：按 *_Data 目录定位
                if (!Directory.Exists(managedDir))
                {
                    var dataDirs = Directory.GetDirectories(outputDir, "*_Data");
                    managedDir = dataDirs.Length > 0 ? Path.Combine(dataDirs[0], "Managed") : managedDir;
                }

                var gameplay = TryHash(Path.Combine(managedDir, "Game.Gameplay.dll"));
                var inputs = ComputeInputDigest();
                var manifest = new BuildManifest
                {
                    buildId = gameplay != null ? gameplay.Substring(0, Math.Min(12, gameplay.Length)) : "unknown",
                    protocolId = Game.Gameplay.Network.GameProtocolIdentity.ProtocolId,
                    builtAtUtc = DateTime.UtcNow.ToString("o"),
                    unityVersion = Application.unityVersion,
                    subtarget = subtarget,
                    gamePlayDllSha256 = gameplay ?? "<absent>",
                    gameUiDllSha256 = TryHash(Path.Combine(managedDir, "Game.UI.dll")) ?? "<absent>",
                    gameAccountDllSha256 = TryHash(Path.Combine(managedDir, "Game.Account.dll")) ?? "<absent>",
                    inputDigest = inputs.Digest ?? "<absent>",
                    inputFileCount = inputs.FileCount,
                };
                var path = Path.Combine(outputDir, Game.Gameplay.Network.GameProtocolIdentity.ManifestFileName);
                File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
                Debug.Log($"[BuildManifest] written {path} protocol={manifest.protocolId} buildId={manifest.buildId} "
                    + $"gameplay={manifest.gamePlayDllSha256.Substring(0, Math.Min(12, manifest.gamePlayDllSha256.Length))} "
                    + $"inputDigest={(inputs.Digest != null ? inputs.Digest.Substring(0, 12) : "<absent>")} files={inputs.FileCount}");
            }
            catch (Exception exception)
            {
                // 清单是部署门证据，不是构建产物本身：失败只告警，不使构建红
                Debug.LogWarning($"[BuildManifest] 清单写入失败（部署门将退化为无法比对）：{exception.Message}");
            }
        }

        /// <summary>构建输入集合内容摘要：逐文件 SHA256 后按稳定顺序归并。任一根缺失按 0 文件计
        /// （ProjectVersion.asset 等恒在）；返回 null=工程根不可用（清单记 &lt;absent&gt;，门禁 fail closed）。</summary>
        public static (string Digest, int FileCount) ComputeInputDigest()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!Directory.Exists(projectRoot)) return (null, 0);
            using var merger = SHA256.Create();
            var fileCount = 0;
            foreach (var root in InputRoots)
            {
                var fullPath = Path.Combine(projectRoot, root.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    FeedFile(merger, projectRoot, fullPath);
                    fileCount++;
                    continue;
                }
                if (!Directory.Exists(fullPath)) continue;
                var files = Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories);
                var sorted = new System.Collections.Generic.List<string>();
                foreach (var f in files) sorted.Add(f);
                sorted.Sort(StringComparer.Ordinal);
                foreach (var file in sorted)
                {
                    FeedFile(merger, projectRoot, file);
                    fileCount++;
                }
            }
            var builder = new StringBuilder(merger.HashSize / 4);
            foreach (var b in merger.Hash) builder.Append(b.ToString("x2"));
            return (builder.ToString(), fileCount);
        }

        private static void FeedFile(HashAlgorithm merger, string projectRoot, string filePath)
        {
            var relative = filePath.Substring(projectRoot.Length + 1).Replace('\\', '/');
            byte[] pathBytes = Encoding.UTF8.GetBytes(relative + "\n");
            merger.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            byte[] content;
            try { content = File.ReadAllBytes(filePath); }
            catch { content = new byte[0]; } // 被占用的生成文件按空字节参与归并（稳定顺序仍成立）
            byte[] contentHash;
            using (var sha = SHA256.Create()) contentHash = sha.ComputeHash(content);
            merger.TransformBlock(contentHash, 0, contentHash.Length, null, 0);
        }

        private static string TryHash(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(File.ReadAllBytes(filePath));
            var builder = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }
}
