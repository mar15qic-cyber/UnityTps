using System;
using Game.Account;
using Game.UI;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class AccountClientContractTests
    {
        [Test]
        public void NormalizeBaseUrl_TrimsWhitespaceAndTrailingSlashes()
        {
            Assert.That(ApiClientConfig.NormalizeBaseUrl("  http://127.0.0.1:5080///  "), Is.EqualTo("http://127.0.0.1:5080"));
            Assert.That(ApiClientConfig.NormalizeBaseUrl(null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Session_ApplyAndClearExposeMemoryOnlyAuthState()
        {
            var session = new AccountSession();
            var changed = 0;
            session.Changed += () => changed++;
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "demo", level = 1 },
                loadout = new LoadoutDto { primaryWeaponId = "rifle.day3", secondaryWeaponId = "pistol.day2" }
            });

            Assert.That(session.IsAuthenticated, Is.True);
            Assert.That(session.Profile.username, Is.EqualTo("demo"));
            session.Clear();
            Assert.That(session.IsAuthenticated, Is.False);
            Assert.That(session.Token, Is.Null);
            Assert.That(changed, Is.EqualTo(2));
        }

        [Test]
        public void DtoSerialization_UsesBackendContractFieldNames()
        {
            var json = JsonConvert.SerializeObject(new LoadoutRequest
            {
                primaryWeaponId = "rifle.day3",
                secondaryWeaponId = "pistol.day2",
                throwableId = null
            });

            Assert.That(json, Does.Contain("primaryWeaponId"));
            Assert.That(json, Does.Contain("secondaryWeaponId"));
            Assert.That(json, Does.Contain("throwableId"));
        }

        [Test]
        public void LobbyFlowState_ContainsOnlyDay6States()
        {
            Assert.That(Enum.GetNames(typeof(LobbyFlowState)), Is.EqualTo(new[] { "Login", "Main", "Loadout" }));
        }

        // ---- Docs/27 v1.2 CF 等待房间契约（Q03）----

        [Test]
        public void RoomSnapshotDto_RoundTripsBackendContractFieldNames()
        {
            var json = "{\"room\":{\"roomCode\":\"482913\",\"leaderUsername\":\"host\",\"joinedPlayers\":2,\"maxPlayers\":8," +
                       "\"status\":\"Waiting\",\"mode\":\"TDM\",\"mapId\":\"arena\",\"killTarget\":100,\"timeLimitMinutes\":10," +
                       "\"roomVersion\":3,\"matchId\":null,\"matchGeneration\":0}," +
                       "\"members\":[{\"userId\":1,\"username\":\"host\",\"teamId\":\"Red\",\"isReady\":true,\"isLeader\":true," +
                       "\"joinedAtUtc\":\"2026-09-09T02:00:00Z\"}]," +
                       "\"you\":{\"userId\":2,\"teamId\":\"Blue\",\"isReady\":false}," +
                       "\"connection\":null}";

            var snapshot = JsonConvert.DeserializeObject<RoomSnapshotDto>(json);
            Assert.That(snapshot.room.roomCode, Is.EqualTo("482913"));
            Assert.That(snapshot.room.leaderUsername, Is.EqualTo("host"));
            Assert.That(snapshot.room.mode, Is.EqualTo("TDM"));
            Assert.That(snapshot.room.mapId, Is.EqualTo("arena"));
            Assert.That(snapshot.room.roomVersion, Is.EqualTo(3));
            Assert.That(snapshot.members[0].teamId, Is.EqualTo("Red"));
            Assert.That(snapshot.members[0].isReady, Is.True);
            Assert.That(snapshot.you.teamId, Is.EqualTo("Blue"));
            Assert.That(snapshot.connection, Is.Null);
        }

        [Test]
        public void StartMatchDto_RoundTripsConnectionAndRoster()
        {
            var json = "{\"matchId\":\"abc123\",\"matchGeneration\":1,\"roomVersion\":4,\"mapId\":\"arena\",\"mode\":\"TDM\"," +
                       "\"killTarget\":100,\"timeLimitMinutes\":10," +
                       "\"connection\":{\"serverAddress\":\"10.0.0.8\",\"serverPort\":7770,\"joinTicket\":\"plain\"," +
                       "\"ticketExpiresAtUtc\":\"2026-09-09T02:02:00Z\",\"matchId\":\"abc123\",\"matchGeneration\":1}," +
                       "\"roster\":[{\"userId\":1,\"teamId\":\"Red\"},{\"userId\":2,\"teamId\":\"Blue\"}]}";

            var start = JsonConvert.DeserializeObject<StartMatchDto>(json);
            Assert.That(start.connection.serverPort, Is.EqualTo(7770));
            Assert.That(start.connection.matchId, Is.EqualTo("abc123"));
            Assert.That(start.roster.Length, Is.EqualTo(2));
            Assert.That(start.roster[1].teamId, Is.EqualTo("Blue"));

            // 回程序列化（发给后端的请求体）字段名逐字小驼峰
            var requestJson = JsonConvert.SerializeObject(new RoomSettingsRequest { killTarget = 150 });
            Assert.That(requestJson, Does.Contain("killTarget"));
            Assert.That(requestJson, Does.Not.Contain("KillTarget"));
        }

        [Test]
        public void Session_WaitingRoomFlow_DoesNotTouchConnectionGeneration()
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "jwt",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
                profile = new PlayerProfileDto { username = "host" },
            });
            var json = "{\"room\":{\"roomCode\":\"482913\",\"leaderUsername\":\"host\",\"joinedPlayers\":1,\"maxPlayers\":8," +
                       "\"status\":\"Waiting\",\"mode\":\"TDM\",\"mapId\":\"arena\",\"killTarget\":100,\"timeLimitMinutes\":10," +
                       "\"roomVersion\":1},\"members\":[],\"you\":{\"userId\":1,\"teamId\":\"Red\",\"isReady\":false},\"connection\":null}";

            long gen0 = session.ConnectionGeneration;
            session.ApplyRoomSnapshot(JsonConvert.DeserializeObject<RoomSnapshotDto>(json));
            Assert.That(session.Room, Is.Not.Null);
            Assert.That(session.Room.IsHost, Is.True, "队长身份按 leaderUsername 与本地档案比对");
            Assert.That(session.Room.Status, Is.EqualTo("Waiting"));
            Assert.That(session.ConnectionGeneration, Is.EqualTo(gen0), "Waiting 会话不递增战斗连接代际");

            session.ClearRoom();
            Assert.That(session.Room, Is.Null);
        }
    }
}
