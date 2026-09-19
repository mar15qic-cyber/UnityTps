using System;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>热更清单决策纯逻辑（P2 验证门）：版本门/降级保护/内容寻址差异/哈希/路径消毒。
    /// 下载器（HotUpdateBootstrap）只做搬运不藏决策——这里锁死全部判定语义。</summary>
    public sealed class HotUpdateManifestTests
    {
        private static HotUpdateManifest.HotUpdateFileEntry File(string path, string hash, long size = 1) =>
            new HotUpdateManifest.HotUpdateFileEntry { path = path, hash = hash, size = size };

        private static HotUpdateManifest Manifest(string version, string minClient = "0.0.0",
            params HotUpdateManifest.HotUpdateFileEntry[] files) =>
            new HotUpdateManifest { version = version, minClientVersion = minClient, files = files };

        [Test]
        public void HotVersion_Compare_IntegerSemantics()
        {
            Assert.AreEqual(1, HotUpdatePlan.CompareHotVersion("2", "1"));
            Assert.AreEqual(0, HotUpdatePlan.CompareHotVersion("10", "10"));
            Assert.AreEqual(-1, HotUpdatePlan.CompareHotVersion("9", "10"));
        }

        [Test]
        public void ClientVersion_Compare_ThreeSegments()
        {
            Assert.AreEqual(1, HotUpdatePlan.CompareClientVersion("0.2.0", "0.1.9"));
            Assert.AreEqual(0, HotUpdatePlan.CompareClientVersion("0.1.0", "0.1"));
            Assert.AreEqual(-1, HotUpdatePlan.CompareClientVersion("0.9.9", "1.0.0"));
        }

        [Test]
        public void Decide_NoInstalled_PlansAllFiles()
        {
            var remote = Manifest("1", "0.0.0", File("a.lua", "ha"), File("b.lua", "hb"));
            var plan = HotUpdatePlan.Decide(remote, null, "0.1.0");
            Assert.AreEqual(HotUpdatePlan.DecisionKind.Download, plan.Kind);
            Assert.AreEqual(2, plan.Changed.Count);
        }

        [Test]
        public void Decide_AllHashesMatch_IsUpToDate_EvenAcrossVersions()
        {
            // 内容寻址：升级版本但文件没变 → 无需下载
            var installed = Manifest("1", "0.0.0", File("a.lua", "ha"));
            var remote = Manifest("2", "0.0.0", File("a.lua", "ha"));
            var plan = HotUpdatePlan.Decide(remote, installed, "0.1.0");
            Assert.AreEqual(HotUpdatePlan.DecisionKind.UpToDate, plan.Kind);
        }

        [Test]
        public void Decide_PartialChange_DownloadsOnlyChanged()
        {
            var installed = Manifest("1", "0.0.0", File("a.lua", "ha"), File("b.lua", "hb"));
            var remote = Manifest("2", "0.0.0", File("a.lua", "ha"), File("b.lua", "hb-NEW"), File("c.lua", "hc"));
            var plan = HotUpdatePlan.Decide(remote, installed, "0.1.0");
            Assert.AreEqual(HotUpdatePlan.DecisionKind.Download, plan.Kind);
            Assert.AreEqual(2, plan.Changed.Count);
            CollectionAssert.AreEquivalent(new[] { "b.lua", "c.lua" }, plan.Changed.ConvertAll(f => f.path));
        }

        [Test]
        public void Decide_ServerRollback_IsRejected()
        {
            var installed = Manifest("5", files: File("a.lua", "ha"));
            var remote = Manifest("4", "0.0.0", File("a.lua", "ha4"));
            var plan = HotUpdatePlan.Decide(remote, installed, "0.1.0");
            Assert.AreEqual(HotUpdatePlan.DecisionKind.DowngradeRejected, plan.Kind);
        }

        [Test]
        public void Decide_MinClientGate_BlocksHotUpdate()
        {
            var remote = Manifest("2", "0.2.0", File("a.lua", "ha"));
            var plan = HotUpdatePlan.Decide(remote, null, "0.1.0");
            Assert.AreEqual(HotUpdatePlan.DecisionKind.MinClientGate, plan.Kind);
        }

        [Test]
        public void VerifyHash_KnownVector()
        {
            // sha256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            var data = System.Text.Encoding.UTF8.GetBytes("abc");
            Assert.IsTrue(HotUpdatePlan.VerifyHash(data, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.IsTrue(HotUpdatePlan.VerifyHash(data, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"), "大小写不敏感");
            Assert.IsFalse(HotUpdatePlan.VerifyHash(data, "0000000000000000000000000000000000000000000000000000000000000000"));
            Assert.IsFalse(HotUpdatePlan.VerifyHash(null, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void SanitizeRelativePath_RejectsTraversalAndDrive()
        {
            Assert.AreEqual("lua/a.lua", HotUpdatePlan.SanitizeRelativePath("/lua/a.lua"));
            Assert.Throws<InvalidOperationException>(() => HotUpdatePlan.SanitizeRelativePath("../evil.lua"));
            Assert.Throws<InvalidOperationException>(() => HotUpdatePlan.SanitizeRelativePath("a/../../evil.lua"));
            Assert.Throws<InvalidOperationException>(() => HotUpdatePlan.SanitizeRelativePath("C:/evil.lua"));
            Assert.Throws<InvalidOperationException>(() => HotUpdatePlan.SanitizeRelativePath("  "));
        }

        [Test]
        public void Manifest_JsonUtility_RoundTrip()
        {
            var manifest = Manifest("3", "0.1.1", File("hello_page.lua", "deadbeef", 128));
            var json = JsonUtility.ToJson(manifest);
            var parsed = JsonUtility.FromJson<HotUpdateManifest>(json);
            Assert.AreEqual("3", parsed.version);
            Assert.AreEqual("0.1.1", parsed.minClientVersion);
            Assert.AreEqual(1, parsed.files.Length);
            Assert.AreEqual("hello_page.lua", parsed.files[0].path);
            Assert.AreEqual("deadbeef", parsed.files[0].hash);
            Assert.AreEqual(128, parsed.files[0].size);
        }
    }
}
