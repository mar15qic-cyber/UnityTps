using System;
using System.IO;
using System.Security.Cryptography;
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
        }

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
                };
                var path = Path.Combine(outputDir, Game.Gameplay.Network.GameProtocolIdentity.ManifestFileName);
                File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
                Debug.Log($"[BuildManifest] written {path} protocol={manifest.protocolId} buildId={manifest.buildId} "
                    + $"gameplay={manifest.gamePlayDllSha256.Substring(0, Math.Min(12, manifest.gamePlayDllSha256.Length))}");
            }
            catch (Exception exception)
            {
                // 清单是部署门证据，不是构建产物本身：失败只告警，不使构建红
                Debug.LogWarning($"[BuildManifest] 清单写入失败（部署门将退化为无法比对）：{exception.Message}");
            }
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
