using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class TpThrowClipWiringTests
    {
        [Test]
        public void RifleAndHandgunThrowPhasesResolveFromResources()
        {
            var catalog = Resources.Load<TpThrowAnimationCatalog>("TpThrowAnimationCatalog");
            Assert.That(catalog, Is.Not.Null);
            AssertClips(catalog.Rifle);
            AssertClips(catalog.Handgun);
        }

        private static void AssertClips(TpThrowClips clips)
        {
            Assert.That(clips.Start, Is.Not.Null);
            Assert.That(clips.Pose, Is.Not.Null);
            Assert.That(clips.Release, Is.Not.Null);
            Assert.That(clips.Cancel, Is.Not.Null);
        }
    }
}
