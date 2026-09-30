using System;
using Game.Account;
using Game.Gameplay.Network;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CF 三背包契约与会话层（2026-09-30 Phase B）：
    /// ① consume 响应 JsonUtility 镜像（backpacks 数组 + activeBackpackIndex，与后端 Contracts.cs 逐字小驼峰对齐）；
    /// ② TicketConsumeResult 透传（旧后端 null 数组 = 单配装回退语义）；
    /// ③ AccountSession 三背包视图（Loadout 恒等活动背包 / 按背包写回 / 配件版本合并按背包）。
    /// </summary>
    public sealed class BackpackContractTests
    {
        private const string ConsumeJson =
            "{\"valid\":true,\"roomCode\":\"AB12CD\",\"userId\":\"42\",\"username\":\"packman\"," +
            "\"expiresAtUtc\":\"2026-09-30T00:00:00Z\",\"sessionId\":7,\"errorCode\":null," +
            "\"loadout\":{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":3,\"attachments\":[],\"backpackIndex\":0}," +
            "\"matchId\":\"m-1\",\"matchGeneration\":1,\"teamId\":\"Red\",\"matchMode\":\"TDM\",\"killTarget\":50,\"timeLimitMinutes\":10,\"maxPlayers\":8," +
            "\"backpacks\":[" +
            "{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":3,\"attachments\":[],\"backpackIndex\":0}," +
            "{\"primaryWeaponId\":\"weapon.ak\",\"secondaryWeaponId\":\"weapon.handgun02\",\"throwableId\":null,\"version\":1,\"attachments\":[],\"backpackIndex\":1}," +
            "{\"primaryWeaponId\":\"weapon.sniper01\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":2,\"attachments\":[],\"backpackIndex\":2}]," +
            "\"activeBackpackIndex\":0}";

        [Test]
        public void ConsumeResponse_ParsesBackpackArrayMirror()
        {
            var response = JsonUtility.FromJson<ServerJoinTicketConsumeResponse>(ConsumeJson);

            Assert.That(response.valid, Is.True);
            Assert.That(response.backpacks, Is.Not.Null);
            Assert.That(response.backpacks.Length, Is.EqualTo(3));
            Assert.That(response.backpacks[0].primaryWeaponId, Is.EqualTo("weapon.m4"));
            Assert.That(response.backpacks[1].primaryWeaponId, Is.EqualTo("weapon.ak"));
            // JsonUtility 把 JSON null 解析为空串（既定行为）——空串/null 语义都为"不带雷"，
            // Phase C 消费侧必须按 IsNullOrEmpty 归一（见 ThrowableLoadoutPolicy）。
            Assert.That(string.IsNullOrEmpty(response.backpacks[1].throwableId), Is.True, "空投掷槽（不带雷）必须可表达");
            Assert.That(response.backpacks[2].backpackIndex, Is.EqualTo(2));
            Assert.That(response.activeBackpackIndex, Is.EqualTo(0));
            // loadout 镜像字段保留 = 活动背包（旧 DS 单配装语义兼容）
            Assert.That(response.loadout.primaryWeaponId, Is.EqualTo(response.backpacks[0].primaryWeaponId));
        }

        [Test]
        public void ConsumeResponse_OldBackendWithoutBackpacks_FallsBackToSingleLoadout()
        {
            var legacy = "{\"valid\":true,\"roomCode\":\"AB12CD\",\"userId\":\"42\",\"username\":\"packman\"," +
                "\"expiresAtUtc\":\"2026-09-30T00:00:00Z\",\"sessionId\":7," +
                "\"loadout\":{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"version\":3,\"attachments\":[]}}";
            var response = JsonUtility.FromJson<ServerJoinTicketConsumeResponse>(legacy);

            Assert.That(response.backpacks, Is.Null, "旧后端无 backpacks 字段 → null（DS 回退单配装语义）");
            Assert.That(response.activeBackpackIndex, Is.EqualTo(0));
            Assert.That(response.loadout, Is.Not.Null);
        }

        [Test]
        public void TicketConsumeResult_PassesBackpacksThrough()
        {
            var response = JsonUtility.FromJson<ServerJoinTicketConsumeResponse>(ConsumeJson);
            var result = TicketConsumeResult.AcceptedFromBackend(response.roomCode, response.userId, response.username,
                response.sessionId, response.loadout,
                response.matchId, response.matchGeneration, response.teamId,
                response.matchMode, response.killTarget, response.timeLimitMinutes, response.maxPlayers,
                response.backpacks, response.activeBackpackIndex);

            Assert.That(result.Backpacks, Is.Not.Null);
            Assert.That(result.Backpacks.Length, Is.EqualTo(3));
            Assert.That(result.ActiveBackpackIndex, Is.EqualTo(0));
            Assert.That(result.Backpacks[2].backpackIndex, Is.EqualTo(2));
        }

        [Test]
        public void AuthSessionDto_NewtonsoftParse_CarriesBackpacks()
        {
            var json = "{\"token\":\"jwt\",\"expiresAtUtc\":\"2026-09-30T00:00:00Z\",\"coins\":100," +
                "\"profile\":{\"username\":\"demo\",\"identityTag\":\"0042\",\"level\":1,\"xp\":0,\"xpToNextLevel\":100,\"coins\":100}," +
                "\"loadout\":{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":1,\"attachments\":[],\"backpackIndex\":0}," +
                "\"backpacks\":{\"backpacks\":[" +
                "{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":1,\"attachments\":[],\"backpackIndex\":0}," +
                "{\"primaryWeaponId\":\"weapon.ak\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":\"throwable.standard\",\"version\":1,\"attachments\":[],\"backpackIndex\":1}," +
                "{\"primaryWeaponId\":\"weapon.m4\",\"secondaryWeaponId\":\"weapon.service_pistol\",\"throwableId\":null,\"version\":1,\"attachments\":[],\"backpackIndex\":2}]," +
                "\"activeIndex\":0}}";
            var dto = JsonConvert.DeserializeObject<AuthSessionDto>(json);

            Assert.That(dto.backpacks, Is.Not.Null);
            Assert.That(dto.backpacks.backpacks.Length, Is.EqualTo(3));
            Assert.That(dto.backpacks.backpacks[2].throwableId, Is.Null);
        }

        // ---- AccountSession 三背包视图 ----

        private static BackpackSetDto BuildSet()
        {
            return new BackpackSetDto
            {
                activeIndex = 0,
                backpacks = new[]
                {
                    new LoadoutDto { primaryWeaponId = "weapon.m4", secondaryWeaponId = "weapon.service_pistol", version = 3, backpackIndex = 0 },
                    new LoadoutDto { primaryWeaponId = "weapon.ak", secondaryWeaponId = "weapon.handgun02", version = 1, backpackIndex = 1 },
                    new LoadoutDto { primaryWeaponId = "weapon.sniper01", secondaryWeaponId = "weapon.service_pistol", version = 2, backpackIndex = 2 },
                }
            };
        }

        [Test]
        public void Session_ApplyWithBackpacks_LoadoutMirrorsActiveBackpack()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "stale", version = 0 },
                backpacks = BuildSet(),
            });

            Assert.That(session.Backpacks, Is.Not.Null);
            Assert.That(session.ActiveBackpackIndex, Is.EqualTo(0));
            Assert.That(session.Loadout.primaryWeaponId, Is.EqualTo("weapon.m4"), "Loadout 必须等价活动背包（旧消费点零改动语义）");
            Assert.That(session.LoadoutForBackpack(2).primaryWeaponId, Is.EqualTo("weapon.sniper01"));
        }

        [Test]
        public void Session_ApplyWithoutBackpacks_KeepsLegacySingleLoadout()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", version = 3 },
            });

            Assert.That(session.Backpacks, Is.Null);
            Assert.That(session.ActiveBackpackIndex, Is.EqualTo(0));
            Assert.That(session.Loadout.primaryWeaponId, Is.EqualTo("weapon.m4"));
            Assert.That(session.LoadoutForBackpack(1).primaryWeaponId, Is.EqualTo("weapon.m4"), "无全集时任意背包读数回退活动背包");
        }

        [Test]
        public void Session_ApplyLoadoutForBackpack_UpdatesOnlyThatBackpack()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", version = 3 },
                backpacks = BuildSet(),
            });

            session.ApplyLoadout(new LoadoutDto { primaryWeaponId = "weapon.rifle03", secondaryWeaponId = "weapon.handgun02", version = 2, backpackIndex = 1 });

            Assert.That(session.LoadoutForBackpack(1).primaryWeaponId, Is.EqualTo("weapon.rifle03"));
            Assert.That(session.Loadout.primaryWeaponId, Is.EqualTo("weapon.m4"), "非活动背包更新不得改动 Loadout 镜像");
            Assert.That(session.LoadoutForBackpack(2).primaryWeaponId, Is.EqualTo("weapon.sniper01"), "其他背包不受影响");
        }

        [Test]
        public void Session_ApplyLoadoutAttachments_ScopesToRequestedBackpack()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", version = 3 },
                backpacks = BuildSet(),
            });

            session.ApplyLoadoutAttachments(new LoadoutAttachmentsDto
            {
                version = 5,
                attachments = new[] { new LoadoutAttachmentDto { weaponSlot = "Primary", attachmentSlot = "Optic", attachmentItemId = "attach.lpfp.optic.01" } },
            }, backpackIndex: 2);

            var target = session.LoadoutForBackpack(2);
            Assert.That(target.version, Is.EqualTo(5));
            Assert.That(target.attachments.Length, Is.EqualTo(1));
            Assert.That(session.Loadout.version, Is.EqualTo(3), "背包 0（活动）版本不受背包 2 配件保存影响");
            Assert.That(session.Loadout.attachments, Is.Null.Or.Empty);
        }

        [Test]
        public void Session_Clear_ResetsBackpacks()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", version = 3 },
                backpacks = BuildSet(),
            });
            session.Clear();

            Assert.That(session.Backpacks, Is.Null);
            Assert.That(session.Loadout, Is.Null);
            Assert.That(session.LoadoutForBackpack(0), Is.Null);
        }
    }
}
