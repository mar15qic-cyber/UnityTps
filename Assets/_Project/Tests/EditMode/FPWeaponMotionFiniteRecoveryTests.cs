using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// Regression locks for the shared viewmodel spring. These deliberately call
    /// the same closed-form integrator used by FPWeaponMotion and LPWGunPoseDriver
    /// so a malformed recoil packet or an editor dt spike cannot poison Transform.
    /// </summary>
    public sealed class FPWeaponMotionFiniteRecoveryTests
    {
        [Test]
        public void NonFiniteSpringStateFallsBackToDeterministicZero()
        {
            Vector3 result = FPWeaponMotion.StepDampedSpringAxis3(
                new Vector3(float.NaN, 1f, 0f), Vector3.one,
                50f, .7f, .016f, out Vector3 velocity);

            Assert.That(result, Is.EqualTo(Vector3.zero));
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void InvalidSpringParametersRecoverWithoutNonFiniteOutput()
        {
            Vector3 result = FPWeaponMotion.StepDampedSpringAxis3(
                new Vector3(.3f, -.2f, 0f), new Vector3(2f, -1f, .5f),
                float.NaN, float.NaN, .25f, out Vector3 velocity);

            Assert.That(result, Is.EqualTo(Vector3.zero));
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void LargeFrameStepRemainsFiniteAndBounded()
        {
            Vector3 result = FPWeaponMotion.StepDampedSpringAxis3(
                new Vector3(-10f, 4f, -2f), new Vector3(100f, -50f, 20f),
                8f * Mathf.PI * 2f, .7f, .25f, out Vector3 velocity);

            Assert.That(float.IsFinite(result.x) && float.IsFinite(result.y)
                && float.IsFinite(result.z), Is.True);
            Assert.That(float.IsFinite(velocity.x) && float.IsFinite(velocity.y)
                && float.IsFinite(velocity.z), Is.True);
            Assert.That(result.magnitude, Is.LessThanOrEqualTo(10f));
        }
    }
}
