using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// F03/F04/F05/F18（2026-09-19 审计）热更安装事务回归——真实临时文件系统直驱。
    /// 反例背景：差量只写 HotFiles/&lt;新版本&gt;/ 后整根切换 → 新目录缺未变化文件
    /// （bootstrap/地图丢失、Lua 混入内置旧脚本）；失败早返回不恢复已装根；同版本
    /// 异内容直接覆盖当前使用包；指针 WriteAllText 可截断；版本串未校验可越目录。
    /// </summary>
    public sealed class HotUpdateInstallerTests
    {
        private string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "unityfps-hotinst-" + Path.GetRandomFileName());

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        // ---- 工具 ----

        private static byte[] Bytes(string content) => System.Text.Encoding.UTF8.GetBytes(content);

        private static HotUpdateManifest MakeManifest(string version, params (string path, string content)[] files)
        {
            var manifest = new HotUpdateManifest { version = version };
            var list = new List<HotUpdateManifest.HotUpdateFileEntry>();
            foreach (var (path, content) in files)
            {
                var bytes = Bytes(content);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                    list.Add(new HotUpdateManifest.HotUpdateFileEntry { path = path, hash = hash, size = bytes.Length });
                }
            }
            manifest.files = list.ToArray();
            return manifest;
        }

        private static Dictionary<string, byte[]> ContentOf(params (string path, string content)[] files)
        {
            var map = new Dictionary<string, byte[]>();
            foreach (var (path, content) in files) map[path] = Bytes(content);
            return map;
        }

        private static System.Func<HotUpdateManifest.HotUpdateFileEntry, Task<byte[]>> Fetcher(
            Dictionary<string, byte[]> map, List<string> requested)
        {
            return file =>
            {
                requested?.Add(file.path);
                return Task.FromResult(map.TryGetValue(file.path, out var data) ? data : null);
            };
        }

        private static string ReadInstalledVersion(string root)
        {
            var pointer = Path.Combine(root, "installed.json");
            return File.ReadAllText(pointer);
        }

        // ---- 用例 ----

        [Test]
        public async Task FreshInstall_WritesFullDirectoryAndPointer()
        {
            var remote = MakeManifest("1", ("hot_bootstrap.lua", "boot1"), ("maps/m.bundle", "mapdata1"));
            var fetchMap = ContentOf(("hot_bootstrap.lua", "boot1"), ("maps/m.bundle", "mapdata1"));
            var requested = new List<string>();

            var outcome = await HotUpdateInstaller.InstallAsync(remote, JsonUtility.ToJson(remote), _root, null, null, Fetcher(fetchMap, requested));

            Assert.IsTrue(outcome.Success, outcome.Error);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "1", "hot_bootstrap.lua")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "1", "maps", "m.bundle")));
            StringAssert.Contains("\"version\":\"1\"", ReadInstalledVersion(_root).Replace(" ", string.Empty));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".staging-1")), "staging 必须被消费/清理");
        }

        [Test]
        public async Task UpgradeOnlyChangedFile_MaterializesUnchangedFilesFromVerifiedDisk()
        {
            // v1 安装
            var v1 = MakeManifest("1", ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career1"), ("maps/m.bundle", "mapdata1"));
            var v1Fetch = Fetcher(ContentOf(
                ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career1"), ("maps/m.bundle", "mapdata1")), null);
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null, v1Fetch)).Success);

            // v2 只改 career：bootstrap 与地图应从 v1 磁盘复验后复制，不依赖下载
            var v2 = MakeManifest("2", ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career2"), ("maps/m.bundle", "mapdata1"));
            var v2Content = ContentOf(("career_page.lua", "career2")); // 远端只提供变化文件
            var requested = new List<string>();
            var installedDir = Path.Combine(_root, "1");

            var outcome = await HotUpdateInstaller.InstallAsync(v2, JsonUtility.ToJson(v2), _root, v1, installedDir, Fetcher(v2Content, requested));

            Assert.IsTrue(outcome.Success, outcome.Error);
            Assert.AreEqual(new[] { "career_page.lua" }, requested.ToArray(), "只下载变化文件");
            Assert.AreEqual(2, outcome.FilesCopiedFromInstalled, "未变化文件从已装目录复制");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "2", "hot_bootstrap.lua")), "新版本目录必须完整（bootstrap 不缺）");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "2", "maps", "m.bundle")), "新版本目录必须完整（地图不缺）");
            Assert.AreEqual("career2", File.ReadAllText(Path.Combine(_root, "2", "career_page.lua")));
            // 旧版本文件逐个不变（失败回退边界）
            Assert.AreEqual("career1", File.ReadAllText(Path.Combine(_root, "1", "career_page.lua")));
        }

        [Test]
        public async Task CorruptInstalledFile_IsNotTrustedAndReFetched()
        {
            var v1 = MakeManifest("1", ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career1"));
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career1")), null))).Success);

            // 磁盘上的 bootstrap 被篡改（installed.json 的 hash 字段仍说它正常）
            File.WriteAllText(Path.Combine(_root, "1", "hot_bootstrap.lua"), "TAMPERED");

            var v2 = MakeManifest("2", ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career2"));
            var requested = new List<string>();
            var outcome = await HotUpdateInstaller.InstallAsync(v2, JsonUtility.ToJson(v2), _root, v1,
                Path.Combine(_root, "1"), Fetcher(ContentOf(("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career2")), requested));

            Assert.IsTrue(outcome.Success, outcome.Error);
            CollectionAssert.Contains(requested, "hot_bootstrap.lua", "磁盘字节与 hash 不符的文件必须重下");
            Assert.AreEqual(0, outcome.FilesCopiedFromInstalled, "被篡改文件不得计入复用");
            Assert.AreEqual("boot1", File.ReadAllText(Path.Combine(_root, "2", "hot_bootstrap.lua")));
        }

        [Test]
        public async Task DownloadFailure_FailsCleanly_OldVersionAndPointerUntouched_StagingRemoved()
        {
            var v1 = MakeManifest("1", ("hot_bootstrap.lua", "boot1"));
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("hot_bootstrap.lua", "boot1")), null))).Success);
            var pointerBefore = ReadInstalledVersion(_root);
            var v1FileBefore = File.ReadAllBytes(Path.Combine(_root, "1", "hot_bootstrap.lua"));

            // v2 下载中途失败（远端缺 career）
            var v2 = MakeManifest("2", ("hot_bootstrap.lua", "boot1"), ("career_page.lua", "career2"));
            var outcome = await HotUpdateInstaller.InstallAsync(v2, JsonUtility.ToJson(v2), _root, v1,
                Path.Combine(_root, "1"), Fetcher(ContentOf(("hot_bootstrap.lua", "boot1")), null));

            Assert.IsFalse(outcome.Success);
            StringAssert.Contains("career_page.lua", outcome.Error);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "2")), "失败不得产生半成品版本目录");
            Assert.AreEqual(pointerBefore, ReadInstalledVersion(_root), "失败不得改写指针");
            CollectionAssert.AreEqual(v1FileBefore, File.ReadAllBytes(Path.Combine(_root, "1", "hot_bootstrap.lua")));
            Assert.IsFalse(Directory.GetFileSystemEntries(_root).Any(e => Path.GetFileName(e).StartsWith(".staging-")), "staging 必须清理");
        }

        [Test]
        public async Task SameVersionDifferentContent_Refused_InstalledDirUntouched()
        {
            var v2 = MakeManifest("2", ("hot_bootstrap.lua", "boot2"));
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v2, JsonUtility.ToJson(v2), _root, null, null,
                Fetcher(ContentOf(("hot_bootstrap.lua", "boot2")), null))).Success);
            var before = File.ReadAllBytes(Path.Combine(_root, "2", "hot_bootstrap.lua"));

            // 同版本异内容（决策层应在 bootstrap 拒绝；此处验证安装器自身守卫不覆盖已装目录）
            var v2Tampered = MakeManifest("2", ("hot_bootstrap.lua", "boot2-EVIL"));
            var outcome = await HotUpdateInstaller.InstallAsync(v2Tampered, JsonUtility.ToJson(v2Tampered), _root, v2,
                Path.Combine(_root, "2"), Fetcher(ContentOf(("hot_bootstrap.lua", "boot2-EVIL")), null));

            Assert.IsFalse(outcome.Success);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_root, "2", "hot_bootstrap.lua")));
            Assert.AreEqual(0, outcome.FilesDownloaded, "拒绝不得发生任何下载");
        }

        [Test]
        public async Task IllegalVersions_RejectedBeforeAnyIo()
        {
            foreach (var version in new[] { "../evil", "a/b", "..", "0", "-1", "1.5", "", "abc" })
            {
                var manifest = MakeManifest(version, ("f.lua", "x"));
                var requested = new List<string>();
                var outcome = await HotUpdateInstaller.InstallAsync(manifest, JsonUtility.ToJson(manifest), _root, null, null, Fetcher(ContentOf(("f.lua", "x")), requested));
                Assert.IsFalse(outcome.Success, $"version '{version}' 应被拒绝");
                Assert.AreEqual(0, requested.Count, $"version '{version}' 拒绝前不得发起下载");
            }
            Assert.IsFalse(File.Exists(Path.Combine(_root, "installed.json")));
        }

        [Test]
        public async Task ManifestPathTraversal_Rejected()
        {
            var manifest = MakeManifest("3", ("../outside.lua", "x"));
            var outcome = await HotUpdateInstaller.InstallAsync(manifest, JsonUtility.ToJson(manifest), _root, null, null, Fetcher(ContentOf(("../outside.lua", "x")), null));
            Assert.IsFalse(outcome.Success);
            StringAssert.Contains("rejected", outcome.Error);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "outside.lua")));
        }

        [Test]
        public async Task OrphanTargetDirectory_ContentsVerifiedThenReusedOrReplaced()
        {
            // 孤儿目录：内容与目标一致 → 复用（不重新下载）
            var v1 = MakeManifest("1", ("f.lua", "content"));
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("f.lua", "content")), null))).Success);
            // 模拟指针丢失（installed.json 删除）但版本目录完好
            File.Delete(Path.Combine(_root, "installed.json"));

            var requested = new List<string>();
            var outcome = await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("f.lua", "content")), requested));

            Assert.IsTrue(outcome.Success, outcome.Error);
            Assert.AreEqual(0, requested.Count, "孤儿目录内容一致时应复用而非重下");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "installed.json")), "指针重建");

            // 孤儿目录内容损坏 → 替换为已验证 staging
            File.Delete(Path.Combine(_root, "installed.json"));
            File.WriteAllText(Path.Combine(_root, "1", "f.lua"), "CORRUPT");
            var requested2 = new List<string>();
            var outcome2 = await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("f.lua", "content")), requested2));
            Assert.IsTrue(outcome2.Success, outcome2.Error);
            Assert.AreEqual("content", File.ReadAllText(Path.Combine(_root, "1", "f.lua")));
        }

        [Test]
        public async Task PointerWriteLeavesNoTmpResidue()
        {
            var v1 = MakeManifest("1", ("f.lua", "x"));
            Assert.IsTrue((await HotUpdateInstaller.InstallAsync(v1, JsonUtility.ToJson(v1), _root, null, null,
                Fetcher(ContentOf(("f.lua", "x")), null))).Success);
            Assert.IsFalse(Directory.GetFiles(_root).Any(f => f.EndsWith(".tmp")), "原子替换不得残留 .tmp");
        }
    }
}
