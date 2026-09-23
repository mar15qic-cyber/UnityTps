using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 热更安装事务（F03/F04/F05/F18，2026-09-19 审计）。纯 IO/纯逻辑（无 UnityWebRequest），
    /// EditMode 以临时目录直驱真实文件系统验证。事务步骤：
    /// ① 清单整体校验（版本严格单段纯数字、路径消毒、size/hash 完备、无重复路径——F18）；
    /// ② staging 新目录物化：未变化文件从已装版本【逐字节复验】后复制（F03：不能只信
    ///    installed.json 的 hash 字段），变化/缺失/损坏文件经 fetch 下载；
    /// ③ 每文件写入前后 SHA256+size 校验，收尾对目标 manifest 全量复验——新版本目录必须
    ///    完整（差量直写新目录会让 v2 缺 bootstrap/地图，Lua 混入内置旧脚本）；
    /// ④ 目录发布：目标已存在（指针写失败孤儿）→ 内容一致复用/不一致替换（同版本异内容
    ///    在 bootstrap 决策层拒绝；此处守卫"目标=已装目录"绝不删除）；
    /// ⑤ 指针原子替换（写 .tmp → File.Replace/Move——F05：直接 WriteAllText 中途退出留截断指针）。
    /// 任何失败：staging 清理、返回 Error、不动已装版本与旧指针（调用方回退已装/内置）。
    /// </summary>
    public static class HotUpdateInstaller
    {
        public sealed class InstallOutcome
        {
            public bool Success;
            public string Error;
            public int FilesCopiedFromInstalled;
            public int FilesDownloaded;
        }

        /// <summary>版本严格校验（F18：版本参与 Path.Combine）：非空、纯数字、可解析、&gt;0、≤32 位。
        /// 返回 null=合法，否则错误描述。</summary>
        public static string ValidateVersionStrict(string version)
        {
            if (string.IsNullOrEmpty(version)) return "version empty";
            if (version.Length > 32) return "version too long: " + version;
            foreach (var character in version)
                if (character < '0' || character > '9') return "version must be digits only: " + version;
            if (!long.TryParse(version, out var value) || value <= 0) return "version invalid: " + version;
            return null;
        }

        /// <summary>清单整体校验（F18）：版本合法、每条 path 可消毒、size≥0、hash 非空、无重复路径。
        /// 返回 null=合法，否则错误描述。</summary>
        public static string ValidateManifest(HotUpdateManifest manifest)
        {
            if (manifest == null || manifest.files == null || manifest.files.Length == 0) return "manifest empty";
            var versionError = ValidateVersionStrict(manifest.version);
            if (versionError != null) return versionError;
            if (manifest.minClientVersion != null && manifest.minClientVersion.Length > 32) return "minClientVersion too long";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.files)
            {
                if (file == null || string.IsNullOrWhiteSpace(file.path)) return "manifest file path empty";
                if (file.size < 0) return "manifest file size negative: " + file.path;
                if (string.IsNullOrWhiteSpace(file.hash)) return "manifest file hash empty: " + file.path;
                try { HotUpdatePlan.SanitizeRelativePath(file.path); }
                catch (Exception e) { return "manifest file path rejected: " + file.path + " (" + e.Message + ")"; }
                if (!seen.Add(file.path)) return "manifest duplicate path: " + file.path;
            }
            return null;
        }

        /// <summary>执行安装事务（语义见类注释）。installedVersionDir=已装版本目录（null=从未装过）。</summary>
        public static async Task<InstallOutcome> InstallAsync(
            HotUpdateManifest remote, string manifestJson, string rootBase,
            HotUpdateManifest installed, string installedVersionDir,
            Func<HotUpdateManifest.HotUpdateFileEntry, Task<byte[]>> fetchAsync,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var outcome = new InstallOutcome();
            string staging = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fetchAsync == null) throw new ArgumentNullException(nameof(fetchAsync));
                var invalid = ValidateManifest(remote);
                if (invalid != null) { outcome.Error = invalid; return outcome; }

                var rootFull = Path.GetFullPath(string.IsNullOrEmpty(rootBase) ? "." : rootBase);
                Directory.CreateDirectory(rootFull);
                var targetDir = Path.GetFullPath(Path.Combine(rootFull, remote.version));
                EnsureUnderRoot(targetDir, rootFull, "target");

                // F05 守卫：目标=当前已装目录（同版本异内容漏网到此）绝不删除/覆盖
                var installedFull = string.IsNullOrEmpty(installedVersionDir) ? null : Path.GetFullPath(installedVersionDir);
                if (installedFull != null && string.Equals(targetDir, installedFull, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Error = "refusing to overwrite the installed version dir: " + remote.version;
                    return outcome;
                }

                // 快路径：目标目录已存在且全量内容匹配（上次发布成功但指针写失败的孤儿）
                // → 直接重建指针，零下载（F05：保留可验证的完整旧版本，缺的只是指针）
                if (Directory.Exists(targetDir) && DirectoryMatches(targetDir, remote))
                {
                    WritePointerAtomic(rootFull, manifestJson);
                    outcome.Success = true;
                    return outcome;
                }

                staging = Path.Combine(rootFull, ".staging-" + remote.version + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var stagingFull = Path.GetFullPath(staging);

                foreach (var file in remote.files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = HotUpdatePlan.SanitizeRelativePath(file.path);
                    var dest = Path.GetFullPath(Path.Combine(stagingFull, relative));
                    EnsureUnderRoot(dest, stagingFull, "staging entry");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                    byte[] data = null;
                    // ① 未变化文件复用前逐字节验证磁盘内容（F03/F05：installed.json 的 hash 字段不可单信）
                    if (installedFull != null && installed?.files != null && InstalledHashMatches(installed, file))
                    {
                        var src = Path.GetFullPath(Path.Combine(installedFull, relative));
                        if (src.StartsWith(installedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(src))
                        {
                            var disk = File.ReadAllBytes(src);
                            if (disk.Length == file.size && HotUpdatePlan.VerifyHash(disk, file.hash))
                            {
                                data = disk;
                                outcome.FilesCopiedFromInstalled++;
                            }
                        }
                    }
                    // ② 变化/缺失/磁盘损坏 → 下载
                    if (data == null)
                    {
                        data = await fetchAsync(file);
                        outcome.FilesDownloaded++;
                    }
                    // ③ 写盘前统一校验（F18）
                    if (data == null || data.Length != file.size || !HotUpdatePlan.VerifyHash(data, file.hash))
                        throw new InvalidOperationException("file content invalid: " + file.path);
                    File.WriteAllBytes(dest, data);
                }

                // ④ 全量复验：新版本目录必须完整物化（F03）
                foreach (var file in remote.files)
                {
                    var relative = HotUpdatePlan.SanitizeRelativePath(file.path);
                    var disk = File.ReadAllBytes(Path.Combine(stagingFull, relative));
                    if (disk.Length != file.size || !HotUpdatePlan.VerifyHash(disk, file.hash))
                        throw new InvalidOperationException("staging verification failed: " + file.path);
                }

                // ⑤ 发布：此处的已存在目录必然是内容不匹配的孤儿（匹配的已走快路径）→ 替换
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, true);
                Directory.Move(stagingFull, targetDir);
                staging = null;

                // ⑥ 指针原子替换（F05）
                cancellationToken.ThrowIfCancellationRequested();
                WritePointerAtomic(rootFull, manifestJson);
                outcome.Success = true;
                return outcome;
            }
            catch (Exception e)
            {
                outcome.Error = e.Message;
                if (staging != null) { try { Directory.Delete(staging, true); } catch { } }
                return outcome;
            }
        }

        /// <summary>指针原子写：先写 .tmp 再 Replace/Move（F05：进程中途退出不残留截断指针）。</summary>
        public static void WritePointerAtomic(string rootFull, string manifestJson)
        {
            var pointer = Path.Combine(rootFull, "installed.json");
            var tmp = pointer + ".tmp";
            File.WriteAllText(tmp, manifestJson ?? string.Empty);
            if (File.Exists(pointer)) File.Replace(tmp, pointer, null);
            else File.Move(tmp, pointer);
        }

        private static bool InstalledHashMatches(HotUpdateManifest installed, HotUpdateManifest.HotUpdateFileEntry file)
        {
            foreach (var candidate in installed.files)
                if (candidate != null && string.Equals(candidate.path, file.path, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(candidate.hash, file.hash, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static bool DirectoryMatches(string dir, HotUpdateManifest manifest)
        {
            try
            {
                foreach (var file in manifest.files)
                {
                    var path = Path.Combine(dir, HotUpdatePlan.SanitizeRelativePath(file.path));
                    if (!File.Exists(path)) return false;
                    var disk = File.ReadAllBytes(path);
                    if (disk.Length != file.size || !HotUpdatePlan.VerifyHash(disk, file.hash)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void EnsureUnderRoot(string fullPath, string rootFull, string what)
        {
            if (!fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{what} path escaped root: {fullPath}");
        }
    }
}
