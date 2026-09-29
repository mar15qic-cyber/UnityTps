using System;
using Game.Core;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class CombatTimingAuditTests
    {
        [Test]
        public void Cadence_NewEquipmentDoesNotInheritOldSniperDelay_SameEquipmentCannotResetIt()
        {
            var cadence = new ServerShotCadence();
            var sniper = new WeaponRuntime(5, 20);
            cadence.BindEquipment(sniper);
            cadence.RecordAt(10, 30, 100);
            cadence.BindEquipment(sniper);
            Assert.That(cadence.CanFireAt(10.5, 100.5), Is.False);
            cadence.BindEquipment(new WeaponRuntime(12, 48));
            Assert.That(cadence.CanFireAt(10.5, 100.5), Is.True);
            cadence.RecordAt(10.5, 360, 100.5);
            Assert.That(cadence.CanFireAt(10.51, 100.51), Is.False);
        }

        [TestCase(15, 640)] [TestCase(30, 640)] [TestCase(60, 640)] [TestCase(240, 640)]
        [TestCase(15, 850)] [TestCase(30, 850)] [TestCase(60, 850)] [TestCase(240, 850)]
        [TestCase(15, 900)] [TestCase(30, 900)] [TestCase(60, 900)] [TestCase(240, 900)]
        public void ContinuousFire_PreservesRateAcrossRenderFrames(int fps, int rpm)
        {
            var runtime = new WeaponRuntime(1000, 0);
            var server = new ServerShotCadence();
            int shots = 0;
            const double duration = 8;
            for (int frame = 0; frame <= fps * duration; frame++)
            {
                double now = frame / (double)fps;
                if (frame > 0) runtime.Tick(1f / fps);
                for (int burst = 0; burst < 3 && runtime.CooldownRemaining <= 0; burst++)
                {
                    double fired = now + Math.Min(0, runtime.CooldownOffset);
                    Assert.That(server.CanFireAt(fired, now), Is.True, $"frame={frame} shot={shots} time={fired}");
                    Assert.That(runtime.TryConsumeRound(), Is.True);
                    runtime.StartCooldown(60f / rpm);
                    server.RecordAt(fired, rpm, now);
                    shots++;
                }
            }
            Assert.That(shots, Is.InRange((int)(duration * rpm / 60), (int)(duration * rpm / 60) + 1));
        }

        [Test]
        public void FrozenRenderFrame_CannotAccumulateUnboundedShots()
        {
            var runtime = new WeaponRuntime(1000, 0);
            runtime.StartCooldown(.06666667f);
            runtime.Tick(20);
            int shots = 0;
            while (runtime.CooldownRemaining <= 0 && shots < 100)
            { runtime.TryConsumeRound(); runtime.StartCooldown(.06666667f); shots++; }
            Assert.That(shots, Is.EqualTo(2));
        }

        [Test]
        public void ShotClock_RejectsReplayAndFutureRateBurst()
        {
            var cadence = new ServerShotCadence();
            cadence.RecordAt(10, 600, 100);
            Assert.That(cadence.CanFireAt(10, 101), Is.False);
            Assert.That(cadence.CanFireAt(10.05, 101), Is.False);
            Assert.That(cadence.CanFireAt(10.1, 100.1), Is.True);
            Assert.That(cadence.CanFireAt(double.NaN, 101), Is.False);
            for (int i = 1; i <= 2; i++) cadence.RecordAt(10 + i * .1, 600, 100);
            Assert.That(cadence.CanFireAt(11, 100), Is.False, "client clock cannot bypass the server rate budget");
        }

        [TestCase(15)] [TestCase(30)] [TestCase(60)] [TestCase(240)]
        public void BloomRecovery_IsIndependentOfFramePartition(int fps)
        {
            var stats = WeaponStatResolver.Resolve(new WeaponStat {
                Accuracy = new AccuracyProfileData { ShotBloomPerShot = 2, MaxBloom = 5,
                    BloomRecoveryDelay = .15f, BloomRecoverySpeed = 5 } }, null);
            var frame = new WeaponAccuracyState(); var shot = new WeaponAccuracyState();
            frame.OnShot(stats); shot.OnShot(stats);
            float remaining = .3f;
            while (remaining > .000001f)
            { float step = Math.Min(remaining, 1f / fps); frame.Tick(step, stats); remaining -= step; }
            shot.Tick(.3f, stats);
            Assert.That(frame.CurrentBloom, Is.EqualTo(1.25f).Within(.00002f));
            Assert.That(frame.CurrentBloom, Is.EqualTo(shot.CurrentBloom).Within(.00002f));
        }

        [Test]
        public void AimHistory_ReconstructsAdsBeforeAndAfterLaterIntent()
        {
            var timeline = new CombatAimTimeline();
            timeline.SetTarget(true, 10, .16f);
            timeline.SetTarget(false, 10.12, .16f);
            Assert.That(timeline.Evaluate(10.08, .16f), Is.EqualTo(.5f).Within(.00001f));
            Assert.That(timeline.Evaluate(10.12, .16f), Is.EqualTo(.75f).Within(.00001f));
            Assert.That(timeline.Evaluate(10.16, .16f), Is.EqualTo(.5f).Within(.00001f));
            Assert.That(timeline.SetTarget(true, 9, .16f), Is.False);
        }

        [TestCase(MatchPhase.Idle, false)] [TestCase(MatchPhase.Countdown, false)]
        [TestCase(MatchPhase.InProgress, true)] [TestCase(MatchPhase.Ended, false)]
        public void NetworkCombatGate_FollowsMatchPhase_WithoutAffectingOffline(MatchPhase phase, bool allowed)
        {
            var property = typeof(MatchLifecycle).GetProperty("Phase"); var original = MatchLifecycle.Phase;
            try
            {
                property.SetValue(null, phase);
                Assert.That(MatchLifecycle.AllowsCombat(true), Is.EqualTo(allowed));
                Assert.That(MatchLifecycle.AllowsCombat(false), Is.True);
            }
            finally { property.SetValue(null, original); }
        }
    }
}
