using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 战绩面板纯核心锁定（Docs/23 P1-6 战绩面板）：显示分契约、K/D、计时格式、
    /// FFA 个人击杀/助攻排序、行规整、快照 JSON 往返。视图/网络链路属表现与运行时
    /// （实机验收覆盖），EditMode 只锁纯函数不变量。
    /// </summary>
    public sealed class MatchScoreboardSnapshotTests
    {
        // ---- 显示分契约 ----

        [Test]
        public void ComputeScore_KillsAndAssists()
        {
            Assert.That(MatchScoreboardSnapshot.ComputeScore(18, 5), Is.EqualTo(2050)); // 18*100 + 5*50
            Assert.That(MatchScoreboardSnapshot.ComputeScore(0, 0), Is.EqualTo(0));
            Assert.That(MatchScoreboardSnapshot.ComputeScore(20, 0), Is.EqualTo(2000));
        }

        [Test]
        public void ComputeScore_AssistWorthHalfKill()
        {
            Assert.That(MatchScoreboardSnapshot.ComputeScore(0, 2), Is.EqualTo(MatchScoreboardSnapshot.ComputeScore(1, 0)));
        }

        // ---- K/D ----

        [Test]
        public void FormatRatio_TwoDecimals()
        {
            Assert.That(MatchScoreboardSnapshot.FormatRatio(18, 7), Is.EqualTo("2.57"));
            Assert.That(MatchScoreboardSnapshot.FormatRatio(8, 10), Is.EqualTo("0.80"));
        }

        [Test]
        public void FormatRatio_ZeroDeaths_FallsBackToOne()
        {
            Assert.That(MatchScoreboardSnapshot.FormatRatio(3, 0), Is.EqualTo("3.00"));
            Assert.That(MatchScoreboardSnapshot.FormatRatio(0, 0), Is.EqualTo("0.00"));
        }

        // ---- 计时 ----

        [Test]
        public void FormatClock_MinutesSeconds()
        {
            Assert.That(MatchScoreboardSnapshot.FormatClock(522), Is.EqualTo("08:42"));
            Assert.That(MatchScoreboardSnapshot.FormatClock(600), Is.EqualTo("10:00"));
            Assert.That(MatchScoreboardSnapshot.FormatClock(601), Is.EqualTo("10:01"));
        }

        [Test]
        public void FormatClock_NegativeClampsToZero()
        {
            Assert.That(MatchScoreboardSnapshot.FormatClock(-5), Is.EqualTo("00:00"));
        }

        // ---- BuildEntries：FFA 单榜排序 + 字段规整 ----

        [Test]
        public void BuildEntries_TwoPlayers_StayInOnePersonalLeaderboard()
        {
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "b", displayName = "B", kills = 1, deaths = 0, assists = 0, pingMs = 10 },
                new MatchScoreboardInput { playerId = "a", displayName = "A", kills = 0, deaths = 0, assists = 0, pingMs = 10 },
            });
            Assert.That(entries, Has.Length.EqualTo(2));
            Assert.That(entries[0].kills, Is.GreaterThanOrEqualTo(entries[1].kills));
        }

        [Test]
        public void BuildEntries_SortsByKillsThenAssists()
        {
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "p1", displayName = "Low", kills = 1, deaths = 0, assists = 0, pingMs = 1 },
                new MatchScoreboardInput { playerId = "p2", displayName = "High", kills = 3, deaths = 0, assists = 0, pingMs = 1 },
                new MatchScoreboardInput { playerId = "p3", displayName = "Mid", kills = 2, deaths = 0, assists = 0, pingMs = 1 },
            });
            Assert.That(entries[0].displayName, Is.EqualTo("High"));
            Assert.That(entries[1].displayName, Is.EqualTo("Mid"));
            Assert.That(entries[2].displayName, Is.EqualTo("Low"));

            var tie = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "a", displayName = "Assist", kills = 2, deaths = 0, assists = 5 },
                new MatchScoreboardInput { playerId = "b", displayName = "Kills", kills = 3, deaths = 0, assists = 0 },
            });
            Assert.That(tie[0].displayName, Is.EqualTo("Kills"), "个人击杀优先于助攻显示分");
        }

        [Test]
        public void BuildEntries_EmptyDisplayName_FallsBackToPlayerId()
        {
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "solo", displayName = "", kills = 0, deaths = 0, assists = 0, pingMs = 0 },
            });
            Assert.That(entries, Has.Length.EqualTo(1));
            Assert.That(entries[0].displayName, Is.EqualTo("solo"));
        }

        [Test]
        public void BuildEntries_CopiesIsDeadFlag()
        {
            // Phase 4：阵亡标识透传（服务器 _dead 权威投影 → 面板置灰展示）
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "alive", displayName = "Alive", kills = 1, deaths = 0, assists = 0, pingMs = 1, isDead = false },
                new MatchScoreboardInput { playerId = "dead", displayName = "Dead", kills = 0, deaths = 1, assists = 0, pingMs = 1, isDead = true },
            });
            Assert.That(entries[0].isDead, Is.False);
            Assert.That(entries[1].isDead, Is.True);
        }

        [Test]
        public void BuildEntries_NegativeValuesClamped()
        {
            var entries = MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>
            {
                new MatchScoreboardInput { playerId = "x", displayName = "X", kills = -1, deaths = -2, assists = -3, pingMs = -9 },
            });
            Assert.That(entries[0].kills, Is.EqualTo(0));
            Assert.That(entries[0].deaths, Is.EqualTo(0));
            Assert.That(entries[0].assists, Is.EqualTo(0));
            Assert.That(entries[0].pingMs, Is.EqualTo(0));
        }

        [Test]
        public void BuildEntries_EmptyOrNull_ReturnsEmpty()
        {
            Assert.That(MatchScoreboardSnapshot.BuildEntries(null), Has.Length.EqualTo(0));
            Assert.That(MatchScoreboardSnapshot.BuildEntries(new System.Collections.Generic.List<MatchScoreboardInput>()),
                Has.Length.EqualTo(0));
        }

        // ---- 快照 JSON 往返（JsonUtility 与运行时通道同序列化器） ----

        [Test]
        public void Payload_JsonRoundTrip_PreservesEntries()
        {
            var payload = new MatchScoreboardPayload
            {
                timeLeftSeconds = 522,
                entries = new[]
                {
                    new MatchScoreboardEntry { playerId = "u1", displayName = "ChenQi", kills = 18, deaths = 7, assists = 5, pingMs = 32 },
                    new MatchScoreboardEntry { playerId = "u2", displayName = "Viper", kills = 17, deaths = 8, assists = 4, pingMs = 39 },
                }
            };
            var json = JsonUtility.ToJson(payload);
            var restored = JsonUtility.FromJson<MatchScoreboardPayload>(json);

            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.timeLeftSeconds, Is.EqualTo(522));
            Assert.That(restored.entries, Has.Length.EqualTo(2));
            Assert.That(restored.entries[0].playerId, Is.EqualTo("u1"));
            Assert.That(restored.entries[0].displayName, Is.EqualTo("ChenQi"));
            Assert.That(restored.entries[0].kills, Is.EqualTo(18));
            Assert.That(restored.entries[0].deaths, Is.EqualTo(7));
            Assert.That(restored.entries[0].assists, Is.EqualTo(5));
            Assert.That(restored.entries[0].pingMs, Is.EqualTo(32));
        }
    }
}
