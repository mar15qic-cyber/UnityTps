using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/23 P2 结算契约锁定（G5）：请求/响应 DTO 字段名与后端 Contracts.cs 逐字对齐
    /// （camelCase、无 score、含 durationSeconds/isWin 与通行证/成就三组新字段）、
    /// 客户端自报与本地重试入口退役。
    /// 说明：ApiClient 实际序列化用 Newtonsoft（公共字段，同名同面）；JsonUtility 往返
    /// 与之共享同一字段名集合，此处按票据约定用 JsonUtility 锁名。
    /// </summary>
    public sealed class MatchContractTests
    {
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
                "\"profile\":{\"username\":\"tester\",\"level\":2,\"xp\":10,\"xpToNextLevel\":90,\"coins\":500}}";

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

        [Test]
        public void ClientCannotSubmitOrPersistOfflineRewards()
        {
            Assert.That(typeof(Game.Account.IApiClient).GetMethod("SubmitMatchAsync"), Is.Null);
            Assert.That(typeof(MatchSettlementFlow).GetMethod("PersistPending"), Is.Null);
        }
    }
}
