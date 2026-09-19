using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 任务 B 锁定（2026-09-07）：作者假人不得贡献联网比赛成绩。
    /// MatchKillAttributionPolicy 判定矩阵（服务器死亡/击杀归因路径，HandleServerDied 消费）：
    /// ① 双方有资格（真实联网玩家互杀）→ FullAttribution（kills+deaths+Kill 广播+可触 20 杀终局，原行为）；
    /// ② victim 有资格、killer 无资格或无归因（环境伤害/假人"击杀"）→ DeathOnly（仅 victim deaths）；
    /// ③ victim 无资格（authored/server-owned/未认证目标）→ NoScore（零 kills/deaths/广播，表现保留）。
    /// HandleServerDied 的运行时接线（NetworkObject/IsServerInitialized 前置）属服务器运行时，
    /// 由 IT-12 实进程覆盖；本文件锁定判定语义与其用户定案口径。
    /// </summary>
    public sealed class MatchKillAttributionPolicyTests
    {
        [Test]
        public void RealPlayersKillingEachOther_FullAttribution()
        {
            Assert.That(MatchKillAttributionPolicy.Evaluate(killerEligible: true, victimEligible: true),
                Is.EqualTo(MatchKillAttributionPolicy.Outcome.FullAttribution),
                "真实联网玩家互杀保持原行为：kills+deaths+Kill 广播+可触 20 杀终局");
        }

        [Test]
        public void IneligibleVictim_NeverContributesScores_NoMatterKiller()
        {
            // 击杀假人：victim 无资格（authored/server-owned/未认证）→ 零成绩（主案）
            Assert.That(MatchKillAttributionPolicy.Evaluate(killerEligible: true, victimEligible: false),
                Is.EqualTo(MatchKillAttributionPolicy.Outcome.NoScore),
                "击杀 authored 假人不得给击杀者加 kills、不得增联网 deaths、不得广播正式 Kill、不得触发 20 杀终局");

            // 假人"击杀"真实玩家（防御：假人无输入不会开火，但判定层必须封死）→ 仅 victim deaths
            Assert.That(MatchKillAttributionPolicy.Evaluate(killerEligible: false, victimEligible: true),
                Is.EqualTo(MatchKillAttributionPolicy.Outcome.DeathOnly),
                "无资格 killer 不得获得击杀数/广播；真实 victim 的死亡仍计 deaths");
        }

        [Test]
        public void EnvironmentDeathOfRealPlayer_DeathOnly()
        {
            // 无归因死亡（killerEligible=false 由调用方在 killer==null/killer==自身/无资格时归一）
            Assert.That(MatchKillAttributionPolicy.Evaluate(killerEligible: false, victimEligible: true),
                Is.EqualTo(MatchKillAttributionPolicy.Outcome.DeathOnly),
                "真实玩家的环境死亡只计自身 deaths（原语义不变）");
        }

        [Test]
        public void BothIneligible_NoScore()
        {
            Assert.That(MatchKillAttributionPolicy.Evaluate(killerEligible: false, victimEligible: false),
                Is.EqualTo(MatchKillAttributionPolicy.Outcome.NoScore),
                "无资格双方零成绩（authored 对 authored 不产生任何联网比赛数据）");
        }
    }
}
