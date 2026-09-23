using Game.Gameplay.Player;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class PlayerLeanStateTests
    {
        private GameObject _player;
        private GameObject _wall;

        [TearDown]
        public void TearDown()
        {
            if (_player != null) Object.DestroyImmediate(_player);
            if (_wall != null) Object.DestroyImmediate(_wall);
        }

        [Test]
        public void Lean_UsesConfiguredTiming_AndAirborneClearsImmediately()
        {
            var state = new PlayerLeanState();
            state.Step(1, true, false, LeanProfile.LeanInSeconds * .5f,
                Vector3.zero, Quaternion.identity, null, false);
            Assert.That(state.Amount, Is.EqualTo(.5f).Within(.001f));
            state.Step(0, true, false, LeanProfile.LeanOutSeconds * .5f,
                Vector3.zero, Quaternion.identity, null, false);
            Assert.That(state.Amount, Is.EqualTo(0f).Within(.001f));
            state.Step(-1, true, false, LeanProfile.LeanInSeconds,
                Vector3.zero, Quaternion.identity, null, false);
            state.Step(-1, false, false, .02f, Vector3.zero, Quaternion.identity, null, false);
            Assert.That(state.Amount, Is.Zero);
            Assert.That(state.Intent, Is.Zero);
        }

        [Test]
        public void Lean_WallLimitsHeadPath_AndIgnoresOwnColliders()
        {
            _player = new GameObject("lean player");
            var own = _player.AddComponent<BoxCollider>();
            own.center = new Vector3(0f, 1.5f, .333f);
            own.size = new Vector3(.1f, .1f, .1f);
            _wall = new GameObject("wall");
            var wall = _wall.AddComponent<BoxCollider>();
            wall.center = new Vector3(.34f, 1.6f, .333f);
            wall.size = new Vector3(.04f, .5f, .4f);
            Physics.SyncTransforms();

            float allowed = PlayerLeanState.AllowedFraction(Vector3.zero,
                Quaternion.identity, 1f, _player.transform);
            Assert.That(allowed, Is.GreaterThan(.3f).And.LessThan(.7f));
            var state = new PlayerLeanState();
            state.Step(1, true, false, 1f, Vector3.zero, Quaternion.identity,
                _player.transform, true);
            Assert.That(state.Amount, Is.EqualTo(allowed).Within(.01f));
        }
    }
}
