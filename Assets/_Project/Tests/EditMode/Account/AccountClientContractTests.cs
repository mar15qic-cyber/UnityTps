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
            Assert.That(Enum.GetNames(typeof(LobbyFlowState)), Is.EqualTo(new[] { "Login", "Main", "Loadout", "Upgrade" }));
        }
    }
}
