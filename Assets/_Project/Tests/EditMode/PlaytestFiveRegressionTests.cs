using Game.Account;
using Game.Gameplay.Combat;
using Game.Presentation.FX;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class PlaytestFiveRegressionTests
    {
        [Test]
        public void SessionRejectionIgnoresOldRequestsAndWrongPassword()
        {
            Assert.True(ApiClient.IsCurrentSessionRejection(401, "/api/social/inbox", "old", "old"));
            Assert.False(ApiClient.IsCurrentSessionRejection(401, "/api/profile", "old", "new"));
            Assert.False(ApiClient.IsCurrentSessionRejection(401, "/api/auth/login", "old", "old"));
            Assert.False(ApiClient.IsCurrentSessionRejection(503, "/api/profile", "old", "old"));
            Assert.False(ApiClient.IsCurrentSessionRejection(401, "/api/profile", null, null));
        }

        [Test]
        public void DirectFlashHoldsFullWhite_PeripheralAndBackStayPartial()
        {
            float front = ThrowableScreenEffects.CalculateFlashStrength(2f, 9f, 1f, 1f);
            float side = ThrowableScreenEffects.CalculateFlashStrength(2f, 9f, 0f, 1f);
            float back = ThrowableScreenEffects.CalculateFlashStrength(2f, 9f, -1f, 1f);
            Assert.AreEqual(1f, front);
            Assert.That(side, Is.InRange(.01f, .7f));
            Assert.Less(back, side);
            Assert.AreEqual(1f, ThrowableScreenEffects.EvaluateAlpha(.5f, 1.75f, front));
            Assert.That(ThrowableScreenEffects.EvaluateAlpha(1.5f, 1.75f, front), Is.InRange(.01f, .5f));
            Assert.AreEqual(0f, ThrowableScreenEffects.EvaluateAlpha(1.75f, 1.75f, front));
            Assert.Less(ThrowableScreenEffects.CalculateFlashStrength(8f, 9f, 1f, 1f), side);
        }

        [Test]
        public void ThrowTimingAndPhysicsRemainCompleteAndServerAuthoritative()
        {
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            Assert.True(catalog.IsValid(out var reason), reason);
            Assert.That(catalog.ReleaseDelaySeconds, Is.InRange(.1f, .2f));
            Assert.Greater(catalog.ThrowActionSeconds, catalog.ReleaseDelaySeconds);
            foreach (var type in new[] { ThrowableType.Frag, ThrowableType.Flash, ThrowableType.Smoke })
            {
                Assert.That(catalog.Get(type).Bounciness, Is.InRange(.5f, .75f));
                Assert.That(catalog.Get(type).DynamicFriction, Is.InRange(.2f, .4f));
            }
        }
    }
}
