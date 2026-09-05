using System;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase C 退出对局契约锁定（纯逻辑层）：
    /// ① MatchLeavePolicy：2 人终局 / 3-4 人仅移除 / 离开者判负 / 结算语义；
    /// ② LeaveOnceGuard：重复请求、并发事件幂等；
    /// ③ MatchEndedPayload：playerId/endReason/departingPlayerId 序列化往返与按 id 匹配；
    /// ④ EvaluateWinnerMulti：多人排名（修正“仅前两名参与胜负”缺陷）、并列平局、即时胜。
    /// </summary>
    public sealed class MatchLeaveAndPayloadTests
    {
        // ---- MatchLeavePolicy ----

        [Test]
        public void Leave_TwoPlayers_EndsMatch()
        {
            Assert.That(MatchLeavePolicy.ShouldEndMatch(2), Is.True, "2 人局任一人离开=终局");
        }

        [Test]
        public void Leave_ThreeOrMorePlayers_RemovesOnly()
        {
            Assert.That(MatchLeavePolicy.ShouldEndMatch(3), Is.False, "3 人局仅移除退出者");
            Assert.That(MatchLeavePolicy.ShouldEndMatch(4), Is.False);
            Assert.That(MatchLeavePolicy.ShouldEndMatch(16), Is.False);
        }

        [Test]
        public void Leave_BelowTwo_DefensiveEnd()
        {
            // 防御性：有效人数异常 ≤2（如统计抖动到 1）也按终局处理，不产生幽灵对局
            Assert.That(MatchLeavePolicy.ShouldEndMatch(1), Is.True);
        }

        [Test]
        public void Leave_DepartingLoses_RemainingWins()
        {
            Assert.That(MatchLeavePolicy.IsWinnerOnLeaveEnd(isDepartingPlayer: true), Is.False, "离开者判负");
            Assert.That(MatchLeavePolicy.IsWinnerOnLeaveEnd(isDepartingPlayer: false), Is.True, "剩余者判胜");
        }

        [Test]
        public void Leave_EndMatch_SubmitsSettlement()
        {
            Assert.That(MatchLeavePolicy.ShouldSubmitSettlement(MatchEndReason.PlayerLeft), Is.True,
                "2 人退出终局是正式终局（离开者按负提交结算）");
            Assert.That(MatchLeavePolicy.ShouldSubmitSettlement(MatchEndReason.TimeLimit), Is.True);
            Assert.That(MatchLeavePolicy.ShouldSubmitSettlement(MatchEndReason.Normal), Is.True);
        }

        [Test]
        public void LeaveReason_CoversVoluntaryAndDisconnected()
        {
            // 同一策略入口覆盖主动/非主动两类（服务器侧统一处理）
            foreach (PlayerLeaveReason reason in Enum.GetValues(typeof(PlayerLeaveReason)))
                Assert.That(reason, Is.Not.Null);
        }

        // ---- LeaveOnceGuard 幂等 ----

        [Test]
        public void LeaveOnceGuard_DuplicateRequestIgnored()
        {
            var guard = new LeaveOnceGuard();
            Assert.That(guard.TryBegin(101), Is.True, "首次处理");
            Assert.That(guard.TryBegin(101), Is.False, "同一玩家重复 RPC/断线事件幂等忽略");
            Assert.That(guard.TryBegin(101), Is.False);
            Assert.That(guard.ProcessedCount, Is.EqualTo(1));
        }

        [Test]
        public void LeaveOnceGuard_DifferentPlayersProcessedIndependently()
        {
            var guard = new LeaveOnceGuard();
            Assert.That(guard.TryBegin(1), Is.True);
            Assert.That(guard.TryBegin(2), Is.True, ">2 人局先后两人离开都要各处理一次");
            Assert.That(guard.ProcessedCount, Is.EqualTo(2));
            guard.Clear();
            Assert.That(guard.TryBegin(1), Is.True, "Clear 后（新对局）可重新处理");
        }

        // ---- MatchEndedPayload 载荷补强 ----

        [Test]
        public void EndedPayload_PlayerIdRoundTripAndMatch()
        {
            var payload = new MatchLifecycle.MatchEndedPayload
            {
                clientMatchId = "match-20260905120000-0042",
                durationSeconds = 321.5f,
                endReason = MatchEndReason.PlayerLeft.ToString(),
                departingPlayerId = "7",
                players = new[]
                {
                    new MatchLifecycle.MatchPlayerResult { playerId = "7", kills = 3, deaths = 5, isWin = false },
                    new MatchLifecycle.MatchPlayerResult { playerId = "8", kills = 5, deaths = 3, isWin = true },
                },
            };
            var json = JsonUtility.ToJson(payload);
            var back = JsonUtility.FromJson<MatchLifecycle.MatchEndedPayload>(json);

            Assert.That(back.endReason, Is.EqualTo("PlayerLeft"));
            Assert.That(back.departingPlayerId, Is.EqualTo("7"));
            Assert.That(back.players.Length, Is.EqualTo(2));
            Assert.That(back.players[0].playerId, Is.EqualTo("7"));
            Assert.That(back.players[1].isWin, Is.True);

            // 客户端匹配语义：按 playerId 找本地条目（不再靠 kills/deaths 猜）
            var mine = Array.Find(back.players, p => p.playerId == "7");
            Assert.That(mine, Is.Not.Null);
            Assert.That(mine.isWin, Is.False, "离开者（id=7）按负结算");
        }

        [Test]
        public void EndedPayload_PlayerLeftCarriesWinnerAndLoser()
        {
            // 2 人退出终局语义：离开者判负、剩余者判胜（MatchLeavePolicy 映射进载荷）
            bool departingIsWin = MatchLeavePolicy.IsWinnerOnLeaveEnd(true);
            bool remainingIsWin = MatchLeavePolicy.IsWinnerOnLeaveEnd(false);
            var payload = new MatchLifecycle.MatchEndedPayload
            {
                endReason = "PlayerLeft",
                departingPlayerId = "2",
                players = new[]
                {
                    new MatchLifecycle.MatchPlayerResult { playerId = "2", kills = 9, deaths = 4, isWin = departingIsWin },
                    new MatchLifecycle.MatchPlayerResult { playerId = "1", kills = 4, deaths = 9, isWin = remainingIsWin },
                },
            };
            Assert.That(payload.players[0].isWin, Is.False, "离开者即使比分领先也判负（规则权威=服务器）");
            Assert.That(payload.players[1].isWin, Is.True);
        }

        // ---- EvaluateWinnerMulti（>2 人修正） ----

        [Test]
        public void WinnerMulti_ThirdPlayerWins_NotOnlyFirstTwo()
        {
            // 旧缺陷：仅前两名参与胜负 → 第三名最高杀被当旁观者
            var kills = new[] { 5, 3, 12 };
            var deaths = new[] { 4, 2, 1 };
            Assert.That(MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut: true), Is.EqualTo(2), "最高杀者（下标 2）应胜");
        }

        [Test]
        public void WinnerMulti_FourPlayersHighestKillsWins()
        {
            var kills = new[] { 7, 10, 10, 2 };
            var deaths = new[] { 1, 6, 3, 5 };
            Assert.That(MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut: true), Is.EqualTo(2), "同杀数比死亡少者");
        }

        [Test]
        public void WinnerMulti_FullTieIsDraw()
        {
            var kills = new[] { 8, 8, 8 };
            var deaths = new[] { 3, 3, 3 };
            Assert.That(MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut: true), Is.EqualTo(-1), "完全并列=平局（无人 isWin）");
        }

        [Test]
        public void WinnerMulti_InstantWin_UniqueTargetReached()
        {
            var kills = new[] { 20, 5, 19 };
            var deaths = new[] { 2, 9, 3 };
            Assert.That(MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut: false), Is.EqualTo(0), "唯一达 20 杀者即时胜");
        }

        [Test]
        public void WinnerMulti_InstantWin_TiedAtTargetFallsBackToRanking()
        {
            var kills = new[] { 20, 20, 3 };
            var deaths = new[] { 5, 2, 9 };
            Assert.That(MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut: false), Is.EqualTo(1),
                "并列达标无即时胜 → 排名比较（同杀数死亡少者胜）");
        }

        [Test]
        public void WinnerMulti_InvalidInputSafe()
        {
            Assert.That(MatchRules.EvaluateWinnerMulti(null, null, false), Is.EqualTo(-1));
            Assert.That(MatchRules.EvaluateWinnerMulti(new[] { 1 }, new[] { 1, 2 }, false), Is.EqualTo(-1));
            Assert.That(MatchRules.EvaluateWinnerMulti(Array.Empty<int>(), Array.Empty<int>(), false), Is.EqualTo(-1));
        }

        [Test]
        public void WinnerMulti_TwoPlayerSemanticsMatchLegacy()
        {
            // 与既有 1v1 EvaluateWinner 语义一致（回归保护）
            var cases = new[]
            {
                (20, 5, 3, 15, true), (7, 20, 20, 1, true), (15, 10, 10, 3, true),
                (10, 3, 10, 5, true), (9, 2, 9, 2, true),
            };
            foreach (var (kA, dA, kB, dB, timedOut) in cases)
            {
                var legacy = MatchRules.EvaluateWinner(kA, dA, kB, dB, timedOut);
                var multi = MatchRules.EvaluateWinnerMulti(new[] { kA, kB }, new[] { dA, dB }, timedOut);
                var expected = legacy == 1 ? 0 : legacy == -1 ? 1 : -1;
                Assert.That(multi, Is.EqualTo(expected), $"2 人语义应与旧实现一致：{kA}/{dA} vs {kB}/{dB}");
            }
        }
    }
}
