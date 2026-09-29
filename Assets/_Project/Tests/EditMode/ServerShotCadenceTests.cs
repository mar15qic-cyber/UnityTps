using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class ServerShotCadenceTests
    {
        [Test]
        public void BatchedValidShots_ConsumeRoundsDespiteSameFrameCooldown()
        {
            var cadence = new ServerShotCadence();
            var runtime = new WeaponRuntime(30, 90);
            foreach (uint tick in new uint[] { 100, 103, 106 })
            {
                Assert.That(cadence.CanFire(tick), Is.True);
                Assert.That(runtime.TryConsumeRound(cadenceValidated: true), Is.True);
                runtime.StartCooldown(.1f);
                cadence.Record(tick, 30, 600);
            }
            Assert.That(runtime.CurrentAmmo, Is.EqualTo(27));
            Assert.That(runtime.TryConsumeRound(), Is.False, "local fire still uses cooldown");
        }

        [Test]
        public void Cadence_RejectsReplayAndSustainedExcessFireRate()
        {
            var cadence = new ServerShotCadence();
            cadence.Record(100, 30, 600);
            Assert.That(cadence.CanFire(100), Is.False);
            Assert.That(cadence.CanFire(101), Is.False);
            Assert.That(cadence.CanFire(102), Is.True, "one quantized tick of tolerance");
            cadence.Record(102, 30, 600);
            Assert.That(cadence.CanFire(104), Is.False, "tolerance cannot accumulate into higher RPM");
            Assert.That(cadence.CanFire(105), Is.True);
        }

        [Test]
        public void QuantizedM4Cadence_PreservesFractionalInterval()
        {
            var cadence = new ServerShotCadence();
            foreach (uint tick in new uint[] { 100, 102, 105, 108, 111, 114 })
            {
                Assert.That(cadence.CanFire(tick), Is.True);
                cadence.Record(tick, 30, 640);
            }
            Assert.That(cadence.CanFire(114), Is.False);
        }

        [Test]
        public void ValidatedCadence_CannotBypassReloadOrEmptyMagazine()
        {
            var runtime = new WeaponRuntime(1, 2);
            Assert.That(runtime.TryConsumeRound(true), Is.True);
            Assert.That(runtime.TryConsumeRound(true), Is.False);
            Assert.That(runtime.BeginReload(1), Is.True);
            Assert.That(runtime.TryConsumeRound(true), Is.False);
            runtime.CompleteReload();
            Assert.That(runtime.TryConsumeRound(true), Is.True);
        }

        [Test]
        public void ForgedInputTicks_CannotAccelerateServerTimeBudget()
        {
            var cadence = new ServerShotCadence();
            int accepted = 0;
            for (uint serverTick = 0; serverTick < 300; serverTick++)
            {
                uint inputTick = (serverTick + 1) * 30;
                double now = serverTick / 30.0;
                if (!cadence.CanFire(inputTick, now)) continue;
                cadence.Record(inputTick, 30, 600, now);
                accepted++;
            }
            Assert.That(accepted, Is.InRange(99, 103), "600 RPM plus at most 200ms bounded burst");
        }

        [Test]
        public void NewWeaponRuntime_DoesNotRestoreConsumedCadenceBudget()
        {
            var cadence = new ServerShotCadence();
            cadence.Record(1, 30, 60, 0);
            var replacement = new WeaponRuntime(30, 90);
            Assert.That(replacement.TryConsumeRound(true), Is.True);
            Assert.That(cadence.CanFire(31, .1), Is.False);
            Assert.That(cadence.CanFire(31, 1), Is.True);
        }
    }
}
