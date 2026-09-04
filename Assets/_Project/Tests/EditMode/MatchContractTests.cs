using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/23 P2 结算契约锁定（G5）：请求/响应 DTO 字段名与后端 Contracts.cs 逐字对齐
    /// （camelCase、无 score、含 durationSeconds/isWin 与通行证/成就三组新字段）、
    /// 提交预校验边界、待提交记录 PlayerPrefs 往返。
    /// 说明：ApiClient 实际序列化用 Newtonsoft（公共字段，同名同面）；JsonUtility 往返
    /// 与之共享同一字段名集合，此处按票据约定用 JsonUtility 锁名。
    /// </summary>
    public sealed class MatchContractTests
    {
        // ---- 请求契约：与后端 MatchSubmissionRequest（Contracts.cs L77-84）逐字对齐 ----

        [Test]
        public void Request_Json_FieldsMatchBackendContract()
        {
            var json = JsonUtility.ToJson(new MatchSubmissionRequest
            {
                clientMatchId = "match-20260905020101-0007",
                kills = 12,
                deaths = 3,
                durationSeconds = 480,
                isWin = true
            });

            StringAssert.Contains("\"clientMatchId\":\"match-20260905020101-0007\"", json);
            StringAssert.Contains("\"kills\":12", json);
            StringAssert.Contains("\"deaths\":3", json);
            StringAssert.Contains("\"durationSeconds\":480", json);
            StringAssert.Contains("\"isWin\":true", json);
            // 旧契约字段 score 必须已删除（后端 422 MATCH_PAYLOAD_REJECTED 的多余字段防线）
            StringAssert.DoesNotContain("score", json);
        }

        // ---- 响应契约：与后端 MatchResultDto（Contracts.cs L89-94）12 成员对齐 ----

        [Test]
        public void Response_Deserializes_BackendShapedJson()
        {
            const string backendJson = "{\"xpEarned\":120,\"levelUps\":1,\"coins\":500,\"coinsEarned\":85," +
                "\"passXpEarned\":60,\"passLevel\":3,\"passXp\":40,\"passXpToNextLevel\":100," +
                "\"passLevelUps\":[{\"level\":3,\"rewardType\":\"attachment\",\"itemId\":\"attach.rifle.optic\",\"coinsAmount\":0}]," +
                "\"newAttachments\":[\"attach.rifle.muzzle\"]," +
                "\"unlockedAchievements\":[{\"achievementId\":\"first_blood\",\"displayName\":\"首杀\",\"passXpReward\":10}]," +
                "\"replayed\":false," +
                "\"profile\":{\"username\":\"tester\",\"level\":2,\"xp\":10,\"xpToNextLevel\":90,\"skillPoints\":0,\"coins\":500}}";

            var dto = JsonUtility.FromJson<MatchResultDto>(backendJson);

            Assert.That(dto, Is.Not.Null);
            Assert.That(dto.xpEarned, Is.EqualTo(120));
            Assert.That(dto.coins, Is.EqualTo(500L));
            Assert.That(dto.passXpEarned, Is.EqualTo(60));
            Assert.That(dto.passLevel, Is.EqualTo(3));
            Assert.That(dto.passLevelUps, Has.Length.EqualTo(1));
            Assert.That(dto.passLevelUps[0].itemId, Is.EqualTo("attach.rifle.optic"));
            Assert.That(dto.newAttachments, Has.Length.EqualTo(1));
            Assert.That(dto.newAttachments[0], Is.EqualTo("attach.rifle.muzzle"));
            Assert.That(dto.unlockedAchievements[0].displayName, Is.EqualTo("首杀"));
            Assert.That(dto.replayed, Is.False);
            Assert.That(dto.profile.username, Is.EqualTo("tester"));
            // 旧契约字段 clientMatchId 已从响应删除（后端不发送）
            StringAssert.DoesNotContain("clientMatchId", backendJson);
        }

        // ---- 客户端预校验边界（Docs/23 §G.1：kills ≤ 30、duration ≤ 900s） ----

        [Test]
        public void PreValidation_AcceptsBoundaryValues()
        {
            Assert.That(MatchSettlementFlow.IsValidSubmission(30, 900), Is.True);
            Assert.That(MatchSettlementFlow.IsValidSubmission(0, 0), Is.True);
            Assert.That(MatchSettlementFlow.IsValidSubmission(20, 480), Is.True);
        }

        [Test]
        public void PreValidation_RejectsOverLimit()
        {
            Assert.That(MatchSettlementFlow.IsValidSubmission(31, 900), Is.False);
            Assert.That(MatchSettlementFlow.IsValidSubmission(30, 901), Is.False);
            Assert.That(MatchSettlementFlow.IsValidSubmission(-1, 100), Is.False);
        }

        // ---- 待提交记录存取往返（PlayerPrefs） ----

        [Test]
        public void PendingRequest_RoundTripsThroughPlayerPrefs()
        {
            const string matchId = "match-20260905020101-0007";
            var request = new MatchSubmissionRequest
            {
                clientMatchId = matchId,
                kills = 20,
                deaths = 8,
                durationSeconds = 600,
                isWin = true
            };

            try
            {
                MatchSettlementFlow.PersistPending(request);
                var loaded = MatchSettlementFlow.TryLoadPending(matchId);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.clientMatchId, Is.EqualTo(matchId));
                Assert.That(loaded.kills, Is.EqualTo(20));
                Assert.That(loaded.deaths, Is.EqualTo(8));
                Assert.That(loaded.durationSeconds, Is.EqualTo(600));
                Assert.That(loaded.isWin, Is.True);
            }
            finally
            {
                // 清理编辑器 PlayerPrefs，不残留测试键
                MatchSettlementFlow.ClearPending(matchId);
                Assert.That(MatchSettlementFlow.TryLoadPending(matchId), Is.Null);
            }
        }
    }
}
