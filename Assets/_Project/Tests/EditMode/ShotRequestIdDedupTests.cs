using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase 6 定向测试：shotRequestId 重放去重（服务器开火重放防护）。
    /// 锁定语义：首见放行 / 窗口内重复拒绝 / 容量 64 环形逐出最老（正常客户端逐发自增远小于
    /// 窗口，永不误伤；被篡改客户端重放旧 id 在窗口内被拦）。
    /// </summary>
    public sealed class ShotRequestIdDedupTests
    {
        private NetworkCombatAuthority _authority;

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("ShotIdDedupTests_Authority");
            _authority = go.AddComponent<NetworkCombatAuthority>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_authority != null) Object.DestroyImmediate(_authority.gameObject);
        }

        [Test]
        public void FirstSeen_ReturnsFalse()
        {
            Assert.That(_authority.IsDuplicateShotRequest(1ul), Is.False);
            Assert.That(_authority.IsDuplicateShotRequest(2ul), Is.False);
        }

        [Test]
        public void RepeatInsideWindow_ReturnsTrue()
        {
            Assert.That(_authority.IsDuplicateShotRequest(7ul), Is.False);
            Assert.That(_authority.IsDuplicateShotRequest(7ul), Is.True, "同一 shotRequestId 重放必须被拒绝");
            Assert.That(_authority.IsDuplicateShotRequest(7ul), Is.True);
        }

        [Test]
        public void WindowCapacity_EvictsOldest()
        {
            int capacity = NetworkCombatAuthority.SeenShotIdsCapacity;
            for (ulong id = 1; id <= (ulong)capacity; id++)
                Assert.That(_authority.IsDuplicateShotRequest(id), Is.False, $"首见 id={id} 应放行");

            // 第 65 个 id 进窗口 → 最老的 id=1 被逐出
            Assert.That(_authority.IsDuplicateShotRequest((ulong)(capacity + 1)), Is.False);
            // 查询即登记：id=1 重查 = 窗口内首见（False），并把 id=2 挤出窗口（FIFO 滑动）
            Assert.That(_authority.IsDuplicateShotRequest(1ul), Is.False, "已被逐出的最老 id 不再视为重复");
            // 窗口内未逐出的 id 仍重复拒绝（65 与未受影响的 id=3）
            Assert.That(_authority.IsDuplicateShotRequest((ulong)(capacity + 1)), Is.True);
            Assert.That(_authority.IsDuplicateShotRequest(3ul), Is.True, "仍在窗口内的 id 保持重复拒绝");
        }

        [Test]
        public void NormalBurst_NeverMisdetected()
        {
            // 正常客户端逐发自增（FireHeld 每帧提交）：整轮全部首见放行
            for (ulong id = 100; id < 100 + 200ul; id++)
                Assert.That(_authority.IsDuplicateShotRequest(id), Is.False, $"id={id} 正常自增不应被误判");
        }
    }
}
