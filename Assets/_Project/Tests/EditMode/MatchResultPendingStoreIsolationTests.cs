using System.IO;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// F09/F10（2026-09-19 审计）回归锁定：补偿队列每实例隔离 + 保存结果显式可见。
    /// 反例背景：五个 DS 曾共享 &lt;persistent&gt;/match-result-pending.json，后端不可达时
    /// 后写者覆盖先写者（丢单），且同名 .tmp 竞争；构造函数读 persistentDataPath 使
    /// MonoBehaviour 字段初始化器在 EditMode 抛异常（9 项固定红）。
    /// 修复后：路径纯逻辑构建（可测）、目录按 instanceId+环境指纹隔离、保存失败保留内存
    /// 且 LastSaveSucceeded=false（调用方不得声称"已落盘"）。
    /// </summary>
    public sealed class MatchResultPendingStoreIsolationTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp() => _tempRoot = Path.Combine(Path.GetTempPath(), "unityfps-store-test-" + Path.GetRandomFileName());

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }

        [Test]
        public void BuildInstanceFilePath_IsStable_AndIsolatesInstances()
        {
            var arena = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://127.0.0.1:5000", "arena-01");
            var map01 = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://127.0.0.1:5000", "map01-01");

            Assert.AreEqual(arena, MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://127.0.0.1:5000", "arena-01"),
                "同输入同输出（稳定键，不用随机 PID）");
            Assert.AreNotEqual(Path.GetDirectoryName(arena), Path.GetDirectoryName(map01),
                "不同实例不同目录（互不覆盖）");
            StringAssert.Contains("server-results", arena);
        }

        [Test]
        public void BuildInstanceFilePath_SeparatesBackendEnvironments()
        {
            var prod = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "https://api.prod.example", "arena");
            var dev = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://127.0.0.1:5000", "arena");

            Assert.AreNotEqual(Path.GetDirectoryName(Path.GetDirectoryName(prod)),
                Path.GetDirectoryName(Path.GetDirectoryName(dev)),
                "同一实例 id 在不同后端环境落不同目录（服务环境绑定）");
        }

        [Test]
        public void SafeSegment_RejectsTraversal_AndIsDeterministic()
        {
            StringAssert.AreEqualIgnoringCase("unknown", MatchResultPendingStore.SafeSegment(""));
            StringAssert.AreEqualIgnoringCase("unknown", MatchResultPendingStore.SafeSegment(null));
            var traversal = MatchResultPendingStore.SafeSegment("../../etc");
            Assert.IsFalse(traversal.Contains("/"), traversal);
            Assert.IsFalse(traversal.Contains("\\"), traversal);
            Assert.AreEqual(MatchResultPendingStore.SafeSegment("a/b"), MatchResultPendingStore.SafeSegment("a/b"));
            // 消毒段回拼后不得越出预期根
            var path = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://x", "../evil");
            StringAssert.StartsWith(Path.GetFullPath(_tempRoot).ToLowerInvariant(), Path.GetFullPath(path).ToLowerInvariant(),
                "实例 id 含 .. 时消毒后仍落在根内");
        }

        [Test]
        public void Append_SaveFailure_RetainsInMemory_AndReportsNotPersisted()
        {
            // 不可写路径：把目录建成一个文件 → 目录创建必然失败
            var blockingFile = Path.Combine(_tempRoot, "blocked");
            Directory.CreateDirectory(_tempRoot);
            File.WriteAllText(blockingFile, "not a directory");
            var store = new MatchResultPendingStore(Path.Combine(blockingFile, "match-result-pending.json"));

            var saved = store.Append(new ServerMatchResultReportRequest { matchId = "m1" });

            Assert.IsFalse(saved, "磁盘写入失败必须如实返回未持久化");
            Assert.IsFalse(store.LastSaveSucceeded);
            Assert.IsTrue(store.HasUnsavedChanges);
            Assert.AreEqual(1, store.Count, "保存失败内容保留内存待重试（不得丢弃）");
        }

        [Test]
        public void AppendAndRemove_PersistToPerInstanceFile_WithoutTmpResidue()
        {
            var filePath = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://x:1", "arena");
            var store = new MatchResultPendingStore(filePath);

            Assert.IsTrue(store.Append(new ServerMatchResultReportRequest { matchId = "m1", winnerTeam = "A" }));
            Assert.IsTrue(store.LastSaveSucceeded);
            Assert.IsFalse(store.HasUnsavedChanges);
            Assert.IsTrue(File.Exists(filePath), "按实例路径落盘");
            Assert.IsFalse(File.Exists(filePath + ".tmp"), "临时文件替换后不残留");

            var reloaded = new MatchResultPendingStore(filePath);
            Assert.AreEqual(1, reloaded.Count, "重启后重放队列仍在");

            Assert.IsTrue(reloaded.Remove("m1"));
            Assert.AreEqual(0, new MatchResultPendingStore(filePath).Count);
        }

        [Test]
        public void TwoInstances_AppendIndependently_NoClobber()
        {
            var pathA = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://x:1", "arena");
            var pathB = MatchResultPendingStore.BuildInstanceFilePath(_tempRoot, "http://x:1", "map01");
            var storeA = new MatchResultPendingStore(pathA);
            var storeB = new MatchResultPendingStore(pathB);

            Assert.IsTrue(storeA.Append(new ServerMatchResultReportRequest { matchId = "mA" }));
            Assert.IsTrue(storeB.Append(new ServerMatchResultReportRequest { matchId = "mB1" }));
            Assert.IsTrue(storeB.Append(new ServerMatchResultReportRequest { matchId = "mB2" }));

            Assert.AreEqual(1, new MatchResultPendingStore(pathA).Count, "B 的追加不得覆盖 A（旧共享文件丢单反例）");
            Assert.AreEqual(2, new MatchResultPendingStore(pathB).Count);
        }
    }
}
