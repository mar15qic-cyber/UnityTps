using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 F11 普查资格锁定（2026-09-07）：Dedicated/联网比赛只统计
    /// 「NetworkObject 已生成 + 有效 Owner + 连接已认证」的玩家——Arena 作者离线对象
    /// （Owner=FishNet 空连接 ClientId=-1）不再计入开局/终局/胜负/中继/离开人数。
    /// 纯核心矩阵在此锁定；真实网络路径（FindObjectsByType→提取→过滤）由 IT-11 实进程取证
    /// （1 真客户端不开局、2 真客户端开局、终局载荷仅真实 userId）。
    /// </summary>
    public sealed class MatchEligibilityTests
    {
        // ---- 资格矩阵（用户定案三条件逐字对应） ----

        [Test]
        public void AuthenticatedNetworkedPlayer_IsEligible()
        {
            Assert.That(MatchEligibility.Evaluate(
                networkObjectSpawned: true, ownerValid: true, ownerAuthenticated: true), Is.True);
        }

        [Test]
        public void AuthoredOfflineObject_InvalidOwner_IsNotEligible()
        {
            // Arena 作者对象实况：NetworkObject 在服务器上已生成，但 Owner 为空连接（ClientId=-1）
            Assert.That(MatchEligibility.Evaluate(
                networkObjectSpawned: true, ownerValid: false, ownerAuthenticated: true), Is.False);
        }

        [Test]
        public void NotSpawnedObject_IsNotEligible()
        {
            Assert.That(MatchEligibility.Evaluate(
                networkObjectSpawned: false, ownerValid: true, ownerAuthenticated: true), Is.False);
        }

        [Test]
        public void UnauthenticatedConnection_IsNotEligible()
        {
            // 认证窗口内（票据未通过）的连接不计入——半连接玩家不得开局/进终局
            Assert.That(MatchEligibility.Evaluate(
                networkObjectSpawned: true, ownerValid: true, ownerAuthenticated: false), Is.False);
        }

        [Test]
        public void AllThreeMissing_IsNotEligible()
        {
            Assert.That(MatchEligibility.Evaluate(false, false, false), Is.False);
        }

        // ---- 普查门槛（Update Idle 分支语义：有效人数 ≥ 2 才开局倒计时） ----

        [Test]
        public void OneRealPlayerPlusOneAuthored_CannotStartMatch()
        {
            // 1 真人 + 1 authored 离线对象：有效人数 1 < 2 → 不开局（F11 主案）
            bool[] census = { true, false };
            Assert.That(MatchEligibility.CountEligible(census), Is.EqualTo(1));
            Assert.That(MatchEligibility.CountEligible(census) >= 2, Is.False,
                "1 真人 + authored 假人不得开始比赛");
        }

        [Test]
        public void TwoAuthenticatedPlayers_StartNormally()
        {
            bool[] census = { true, true };
            Assert.That(MatchEligibility.CountEligible(census), Is.EqualTo(2));
            Assert.That(MatchEligibility.CountEligible(census) >= 2, Is.True,
                "2 个已认证真人正常开始");
        }

        [Test]
        public void OnlyAuthoredObjects_NeverStart()
        {
            // 全 authored（极端：0 真人）恒不开局
            bool[] census = { false, false, false };
            Assert.That(MatchEligibility.CountEligible(census), Is.EqualTo(0));
        }

        // ---- 玩家实例提取映射（非联网环境下 NetworkObject 未生成 → 无资格，防御性） ----

        [Test]
        public void EvaluatePlayer_NullPlayer_IsNotEligible()
        {
            Assert.That(MatchEligibility.EvaluatePlayer(null), Is.False);
            Assert.That(MatchLifecycle.IsEligibleNetworkPlayer(null), Is.False);
        }

        [Test]
        public void EvaluatePlayer_WithoutNetworkObject_IsNotEligible()
        {
            // EditMode 无运行中服务器：仅挂 NetworkCombatAuthority（无 NetworkObject 组件）→
            // NetworkObject 解析为 null → 无资格。锁定提取链路的空引用防御分支
            //（真实「已生成+已认证」路径由 IT-11 实进程取证；各资格维度语义已在上面矩阵锁定）
            var go = new UnityEngine.GameObject("EligibilityProbe_NoNetworkObject");
            try
            {
                var authority = go.AddComponent<NetworkCombatAuthority>();
                Assert.That(MatchLifecycle.IsEligibleNetworkPlayer(authority), Is.False,
                    "无 NetworkObject 的实例不得计入联网比赛普查");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
