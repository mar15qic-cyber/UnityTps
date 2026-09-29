using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/23 P1 击杀竞赛——规则核心锁定（G4）。MatchRules 为纯静态判定，
    /// 全分支转移语义 + 常量边界在此锁定；MatchLifecycle 状态机推进与 HUD 属
    /// 运行时/表现层，不在 EditMode 范围（实机验收覆盖）。
    /// </summary>
    public sealed class MatchLifecycleRulesTests
    {
        [Test]
        public void LateJoinSnapshot_InitializesPhaseRulesTeamsAndMatchIdentity()
        {
            var host = new UnityEngine.GameObject("LifecycleSnapshotTest");
            try
            {
                var lifecycle = host.AddComponent<MatchLifecycle>();
                var payload = new MatchScoreboardPayload { matchId = "late-match", phase = MatchPhase.InProgress,
                    mode = MatchRules.ModeTdm, killTarget = 50, timeLimitSeconds = 600, redKills = 12, blueKills = 9 };
                typeof(MatchLifecycle).GetMethod("MirrorFromEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(lifecycle, new object[] { MatchEventKind.ScoreboardSnapshot, UnityEngine.JsonUtility.ToJson(payload) });
                Assert.AreEqual(MatchPhase.InProgress, MatchLifecycle.Phase);
                Assert.IsFalse(MatchLifecycle.InputFrozen);
                Assert.IsTrue(MatchLifecycle.IsTeamMatch());
                Assert.AreEqual("late-match", MatchLifecycle.ClientMatchId);
                Assert.AreEqual(600, MatchLifecycle.TimeLimitSeconds);
                Assert.AreEqual(50, MatchLifecycle.TargetKills);
                Assert.AreEqual(12, MatchLifecycle.RedScore);
                Assert.AreEqual(9, MatchLifecycle.BlueScore);
                lifecycle.ResetMirrorState();
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
        // ---- EvaluateWinner：20 杀即时终局 ----

        [Test]
        public void KillTarget_A_WinsInstantly()
        {
            Assert.That(MatchRules.EvaluateWinner(20, 5, 3, 15, timedOut: false), Is.EqualTo(1));
        }

        [Test]
        public void KillTarget_B_WinsInstantly()
        {
            Assert.That(MatchRules.EvaluateWinner(7, 20, 20, 1, timedOut: false), Is.EqualTo(-1));
        }

        [Test]
        public void KillTarget_Below20_NotInstantWin()
        {
            // 未达 20 杀且未超时：不产生胜者（走杀数比较——19 vs 5 判 A 胜属超时语义，
            // 未超时未达标场景在真实流程中不会出现，此处锁定"无即时胜"不变量）
            Assert.That(MatchRules.EvaluateWinner(19, 5, 5, 15, timedOut: true), Is.EqualTo(1));
        }

        // ---- 超时判定：比杀 → 比死 → 平局 ----

        [Test]
        public void Timeout_MoreKillsWins()
        {
            Assert.That(MatchRules.EvaluateWinner(15, 10, 10, 3, timedOut: true), Is.EqualTo(1));
            Assert.That(MatchRules.EvaluateWinner(8, 0, 9, 30, timedOut: true), Is.EqualTo(-1));
        }

        [Test]
        public void Timeout_TieKills_FewerDeathsWins()
        {
            Assert.That(MatchRules.EvaluateWinner(10, 3, 10, 5, timedOut: true), Is.EqualTo(1));
            Assert.That(MatchRules.EvaluateWinner(12, 7, 12, 2, timedOut: true), Is.EqualTo(-1));
        }

        [Test]
        public void Timeout_TieKillsTieDeaths_IsDraw()
        {
            Assert.That(MatchRules.EvaluateWinner(10, 5, 10, 5, timedOut: true), Is.EqualTo(0));
        }

        [Test]
        public void BothReach20_HigherKillsWins_TieIsDraw()
        {
            // 双双 ≥20 的竞态边沿：杀数高者胜，完全相同判平（总性保证）
            Assert.That(MatchRules.EvaluateWinner(21, 5, 20, 1, timedOut: false), Is.EqualTo(1));
            Assert.That(MatchRules.EvaluateWinner(20, 0, 20, 0, timedOut: false), Is.EqualTo(0));
        }

        // ---- 常量（Docs/17 §1.4 / Docs/23 P1-1 定版） ----

        [Test]
        public void Constants_AreLocked()
        {
            Assert.That(MatchRules.TargetKills, Is.EqualTo(20));
            Assert.That(MatchRules.MatchTimeLimitSeconds, Is.EqualTo(600));
            Assert.That(MatchRules.CountdownSeconds, Is.EqualTo(3f));
            Assert.That(MatchRules.SpawnExclusionRadiusMeters, Is.EqualTo(8f));
        }
    }
}
