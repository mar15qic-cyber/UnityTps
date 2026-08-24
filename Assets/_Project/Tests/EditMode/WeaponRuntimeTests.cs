using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class WeaponRuntimeTests
    {
        [Test]
        public void Fire_ConsumesOneRound_AndCooldownGatesNextShot()
        {
            var runtime = new WeaponRuntime(30, 90);

            Assert.That(runtime.TryConsumeRound(), Is.True);
            runtime.StartCooldown(0.1f);
            Assert.That(runtime.CurrentAmmo, Is.EqualTo(29));
            Assert.That(runtime.TryConsumeRound(), Is.False);

            runtime.Tick(0.1f);
            Assert.That(runtime.TryConsumeRound(), Is.True);
            Assert.That(runtime.CurrentAmmo, Is.EqualTo(28));
        }

        [Test]
        public void Reload_MovesOnlyRequiredAmmo_FromReserve()
        {
            var runtime = new WeaponRuntime(5, 2);
            Assert.That(runtime.TryConsumeRound(), Is.True);
            Assert.That(runtime.TryConsumeRound(), Is.True);
            Assert.That(runtime.TryConsumeRound(), Is.True);

            Assert.That(runtime.BeginReload(1f), Is.True);
            Assert.That(runtime.CompleteReload(), Is.EqualTo(2));
            Assert.That(runtime.CurrentAmmo, Is.EqualTo(4));
            Assert.That(runtime.ReserveAmmo, Is.Zero);
        }

        [Test]
        public void InterruptedReload_DoesNotChangeAmmo()
        {
            var runtime = new WeaponRuntime(5, 10);
            Assert.That(runtime.TryConsumeRound(), Is.True);
            Assert.That(runtime.BeginReload(1f), Is.True);

            runtime.CancelReload();

            Assert.That(runtime.CurrentAmmo, Is.EqualTo(4));
            Assert.That(runtime.ReserveAmmo, Is.EqualTo(10));
            Assert.That(runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
        }

        [Test]
        public void ActionTimer_Completes_AndSwitchCanInterruptReload()
        {
            var gameObject = new GameObject("ActionSystem_Test");
            try
            {
                var actions = gameObject.AddComponent<ActionSystem>();
                PlayerActionType completed = PlayerActionType.None;
                PlayerActionType interrupted = PlayerActionType.None;
                actions.OnActionCompleted += value => completed = value;
                actions.OnActionInterrupted += (value, _) => interrupted = value;

                Assert.That(actions.TryStart(PlayerActionType.Reload, 1f), Is.True);
                Assert.That(actions.TryStart(PlayerActionType.GrenadeThrow, 1f), Is.False);
                Assert.That(actions.TryStart(PlayerActionType.SwitchWeapon, 0.5f), Is.True);
                Assert.That(interrupted, Is.EqualTo(PlayerActionType.Reload));

                actions.Tick(0.5f);
                Assert.That(completed, Is.EqualTo(PlayerActionType.SwitchWeapon));
                Assert.That(actions.CurrentAction, Is.EqualTo(PlayerActionType.None));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
