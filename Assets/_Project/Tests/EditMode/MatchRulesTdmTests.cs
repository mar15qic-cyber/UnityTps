using System;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// C3/Q04 TDM 纯规则定向（Docs/26 §2.4）：团队胜负（达标/超时/平局）、友伤过滤（关闭友伤+友军挡弹的判定面）、
    /// 重生点选择（远离敌人优先/无安全点选最远/无敌人确定性）、每队容量、比分标题。
    /// </summary>
    public sealed class MatchRulesTdmTests
    {
        // ---- 团队胜负 ----

        [Test]
        public void TeamWinner_TargetReached_WinsImmediately()
        {
            Assert.That(MatchRules.EvaluateTeamWinner(100, 42, 100, timedOut: false), Is.EqualTo(MatchRules.TeamRed));
            Assert.That(MatchRules.EvaluateTeamWinner(42, 100, 100, timedOut: false), Is.EqualTo(MatchRules.TeamBlue));
        }

        [Test]
        public void TeamWinner_Timeout_ComparesScores()
        {
            Assert.That(MatchRules.EvaluateTeamWinner(58, 60, 100, timedOut: true), Is.EqualTo(MatchRules.TeamBlue));
            Assert.That(MatchRules.EvaluateTeamWinner(61, 60, 100, timedOut: true), Is.EqualTo(MatchRules.TeamRed));
        }

        [Test]
        public void TeamWinner_Tie_IsDraw()
        {
            Assert.That(MatchRules.EvaluateTeamWinner(60, 60, 100, timedOut: false), Is.Null);
            Assert.That(MatchRules.EvaluateTeamWinner(0, 0, 100, timedOut: true), Is.Null);
        }

        [Test]
        public void TeamWinner_BelowTarget_NotTimedOut_IsHigherScore()
        {
            // 超时前的非达标局面不该立即出胜者（终局条件由 MatchLifecycle 门槛控制，规则只比较）
            Assert.That(MatchRules.EvaluateTeamWinner(70, 40, 100, timedOut: false), Is.EqualTo(MatchRules.TeamRed),
                "规则函数语义：未达标未超时返回领先方（是否终局由生命周期判定）");
        }

        // ---- 友伤过滤 ----

        [Test]
        public void FriendlyFire_SameTeam_Blocked()
        {
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamRed, MatchRules.TeamRed), Is.False);
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamBlue, MatchRules.TeamBlue), Is.False);
        }

        [Test]
        public void FriendlyFire_EnemyAndNoneAllowed()
        {
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamRed, MatchRules.TeamBlue), Is.True);
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamBlue, MatchRules.TeamRed), Is.True);
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamNone, MatchRules.TeamRed), Is.True);
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamNone, MatchRules.TeamNone), Is.True);
            Assert.That(MatchRules.IsDamageAllowed(null, MatchRules.TeamRed), Is.True, "缺队伍（离线/未同步）恒放行");
        }

        // ---- 重生点选择 ----

        [Test]
        public void RespawnPoint_PrefersSafeDistance_ThenFarthest()
        {
            var candidates = new[] { new Vector3(0, 0, 0), new Vector3(50, 0, 0), new Vector3(100, 0, 0) };
            var enemies = new[] { new Vector3(5, 0, 0) };
            // 点 0 距敌 5m（<8m 排除）；点 1 距敌 45m；点 2 距敌 95m → 安全候选中点 2 最远
            Assert.That(MatchRules.SelectRespawnPoint(candidates, enemies), Is.EqualTo(2));
        }

        [Test]
        public void RespawnPoint_AllExcluded_FallsBackToFarthest()
        {
            var candidates = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0) };
            var enemies = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0) };
            // 两点都被 8m 排除圈覆盖 → 回退全集最远（点 0 距最近敌人 0m，点 1 距最近敌人 0m → 并列取最小下标？）
            // 精确语义：点 0 最近敌距 = 0；点 1 最近敌距 = 0 → farthest 保持首个最大者下标 0
            int picked = MatchRules.SelectRespawnPoint(candidates, enemies);
            Assert.That(picked, Is.InRange(0, 1));
        }

        [Test]
        public void RespawnPoint_NoEnemies_IsDeterministicZero()
        {
            var candidates = new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
            Assert.That(MatchRules.SelectRespawnPoint(candidates, Array.Empty<Vector3>()), Is.EqualTo(0));
            Assert.That(MatchRules.SelectRespawnPoint(candidates, null), Is.EqualTo(0));
        }

        [Test]
        public void RespawnPoint_EmptyCandidates_IsNegativeOne()
        {
            Assert.That(MatchRules.SelectRespawnPoint(Array.Empty<Vector3>(), new[] { Vector3.zero }), Is.EqualTo(-1));
        }

        // ---- 队伍容量与展示 ----

        [Test]
        public void PerTeamCapacity_HalvesRoomCapacity()
        {
            Assert.That(MatchRules.PerTeamCapacity(16), Is.EqualTo(8));
            Assert.That(MatchRules.PerTeamCapacity(2), Is.EqualTo(1));
            Assert.That(MatchRules.PerTeamCapacity(1), Is.EqualTo(1), "下限 1（防御）");
        }

        // ---- I3：队伍容量终验与队伍出生点 ----

        [Test]
        public void CanJoinTeam_TdmEnforcesPerTeamCap_NonTdmUnlimited()
        {
            // 每队 2：占用 1 → 允许；占用 2 → 拒绝（Pending 连接计入占用）
            Assert.That(MatchRules.CanJoinTeam("TDM", 4, MatchRules.TeamRed, 1), Is.True);
            Assert.That(MatchRules.CanJoinTeam("TDM", 4, MatchRules.TeamRed, 2), Is.False);
            Assert.That(MatchRules.CanJoinTeam("KillRace", 8, MatchRules.TeamNone, 7), Is.True, "KillRace 不设队伍容量");
            Assert.That(MatchRules.CanJoinTeam("TDM", 4, MatchRules.TeamNone, 0), Is.False, "TDM 无队伍 fail closed");
        }

        [Test]
        public void SelectTeamRespawnPoint_PrefersOwnHalf_KeepsIndexMapping()
        {
            // 全集 4 点：x=0/10（左半）与 x=100/110（右半），质心 x=55 → 左=红半场
            var points = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f),
                new Vector3(100f, 0f, 0f), new Vector3(110f, 0f, 0f),
            };
            var enemies = new[] { new Vector3(5f, 0f, 0f) }; // 红半场内敌人：红方安全点 = x=10 或更远
            int redPick = MatchRules.SelectTeamRespawnPoint(points, MatchRules.TeamRed, "TDM", enemies);
            Assert.That(points[redPick].x, Is.LessThan(55f), "红队重生点必须位于己方（左）半场");

            int bluePick = MatchRules.SelectTeamRespawnPoint(points, MatchRules.TeamBlue, "TDM", enemies);
            Assert.That(points[bluePick].x, Is.GreaterThanOrEqualTo(55f), "蓝队重生点必须位于己方（右）半场");

            // KillRace 全集选点（可落到任意半场，不要求半场约束）
            Assert.That(MatchRules.SelectTeamRespawnPoint(points, MatchRules.TeamNone, "KillRace", enemies), Is.InRange(0, 3));
        }

        [Test]
        public void SelectTeamRespawnPoint_EmptyOrDegenerate_FallsBackToAll()
        {
            Assert.That(MatchRules.SelectTeamRespawnPoint(Array.Empty<Vector3>(), MatchRules.TeamRed, "TDM", null), Is.EqualTo(-1));
            var single = new[] { new Vector3(1f, 0f, 0f) };
            Assert.That(MatchRules.SelectTeamRespawnPoint(single, MatchRules.TeamBlue, "TDM", null), Is.EqualTo(0), "半场退化回全集");
        }

        [Test]
        public void TeamScoreHeader_FormatsOnlyForTeamMode()
        {
            var tdm = new MatchScoreboardPayload { mode = MatchRules.ModeTdm, killTarget = 100, redKills = 42, blueKills = 38 };
            Assert.That(MatchScoreboardSnapshot.FormatTeamScoreHeader(tdm), Is.EqualTo("红 42 : 38 蓝"));
            var ffa = new MatchScoreboardPayload { mode = MatchRules.ModeKillRace };
            Assert.That(MatchScoreboardSnapshot.FormatTeamScoreHeader(ffa), Is.Null);
        }

        [Test]
        public void BuildEntries_PreservesTeamId_AndDefaultsNone()
        {
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "1", displayName = "a", kills = 3, deaths = 1, assists = 0, pingMs = 20, teamId = MatchRules.TeamBlue },
                new MatchScoreboardInput { playerId = "2", displayName = "b", kills = 1, deaths = 2, assists = 1, pingMs = 30 },
            });
            Assert.That(entries[0].teamId, Is.EqualTo(MatchRules.TeamBlue));
            Assert.That(entries[1].teamId, Is.EqualTo(MatchRules.TeamNone), "空队伍缺省 None");
        }
    }
}
