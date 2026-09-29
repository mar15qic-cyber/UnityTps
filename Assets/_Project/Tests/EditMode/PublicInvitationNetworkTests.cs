using Game.Core;
using Game.Gameplay.Network;
using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class PublicInvitationNetworkTests
    {
        [Test]
        public void ShotWaitIsBoundedAndRequiresMatchingLifeAndConsumedInput()
        {
            var request = new TimedFireRequest { ShotId=1, InputTick=40, LifeEpoch=2, DisplayTick=99 };
            Assert.That(ShotTimingPolicy.Evaluate(request,2,100,30,false,39,.19), Is.EqualTo(ShotTimingPolicy.Decision.Wait));
            Assert.That(ShotTimingPolicy.Evaluate(request,2,100,30,false,39,.2), Is.EqualTo(ShotTimingPolicy.Decision.InputTimeout));
            Assert.That(ShotTimingPolicy.Evaluate(request,2,100,30,true,40,.1), Is.EqualTo(ShotTimingPolicy.Decision.Ready));
            Assert.That(ShotTimingPolicy.Evaluate(request,3,100,30,true,40,.1), Is.EqualTo(ShotTimingPolicy.Decision.StaleLife));
            Assert.That(ShotTimingPolicy.Evaluate(request,2,100,30,false,41,.1), Is.EqualTo(ShotTimingPolicy.Decision.InputTimeout));
            Assert.That(ShotTimingPolicy.Evaluate(request,2,100,30,true,40,.21), Is.EqualTo(ShotTimingPolicy.Decision.InputTimeout));
        }

        [TestCase("http://example.com")]
        [TestCase("https://127.0.0.1")]
        [TestCase("https://localhost")]
        [TestCase("https://192.168.1.2")]
        [TestCase("https://[::1]")]
        [TestCase("https://user:secret@example.com")]
        public void PublicEnvironmentRejectsUnsafeEndpoint(string url)
        {
            var config = new ClientReleaseEnvironment { environmentId="test", releaseId="r1", inviteOnly=true, apiBaseUrl=url, hotUpdateBaseUrl="https://example.com/hotupdate" };
            Assert.That(config.TryValidate(out _), Is.False);
        }

        [TestCase(20)] [TestCase(30)] [TestCase(50)] [TestCase(60)] [TestCase(128)]
        public void CompensationWindowDoesNotIncludeStorageMargin(int rate)
        {
            Assert.That(ShotTimingPolicy.ValidDisplayTick(100 - rate * ShotTimingPolicy.MaxHistorySeconds, 100, rate), Is.True);
            Assert.That(ShotTimingPolicy.ValidDisplayTick(100 - rate * ShotTimingPolicy.MaxHistorySeconds - .01, 100, rate), Is.False);
            Assert.That(ShotTimingPolicy.ValidDisplayTick(101, 100, rate), Is.False);
            Assert.That(ShotTimingPolicy.ValidDisplayTick(double.NaN, 100, rate), Is.False);
        }

        [Test]
        public void PoseBufferIgnoresReorderAndDoesNotInterpolateAcrossRespawn()
        {
            var b = new TimestampedPoseBuffer();
            Assert.That(b.Push(new ObserverPose { ServerTick=10, LifeEpoch=1, Position=Vector3.zero, Rotation=Quaternion.identity }), Is.True);
            Assert.That(b.Push(new ObserverPose { ServerTick=12, LifeEpoch=1, Position=Vector3.right*2, Rotation=Quaternion.identity }), Is.True);
            Assert.That(b.Push(new ObserverPose { ServerTick=11, LifeEpoch=1 }), Is.False);
            Assert.That(b.Evaluate(11, out var p), Is.True); Assert.That(p.Position.x, Is.EqualTo(1).Within(.001));
            b.Push(new ObserverPose { ServerTick=13, LifeEpoch=2, Position=Vector3.right*50, Rotation=Quaternion.identity });
            b.Evaluate(12, out p); Assert.That(p.Position.x, Is.EqualTo(50));
            Assert.That(b.Push(new ObserverPose { ServerTick=14, LifeEpoch=1 }), Is.False);
        }

        [Test]
        public void PresentedPoseInterpolatesMovementPitchAndLeanOnOneTimeline()
        {
            var buffer = new TimestampedPoseBuffer();
            buffer.Push(new ObserverPose { ServerTick = 10, LifeEpoch = 1,
                Position = Vector3.zero, Rotation = Quaternion.identity, LeanAmount = 0f,
                PitchDegrees = -40f, LocomotionState = LocomotionState.Walk,
                MoveInput = Vector2.zero, HorizontalSpeed = 0f, GaitPhase = .9f });
            buffer.Push(new ObserverPose { ServerTick = 12, LifeEpoch = 1,
                Position = Vector3.right * 2f, Rotation = Quaternion.identity, LeanAmount = 1f,
                PitchDegrees = 20f, LocomotionState = LocomotionState.Jump,
                MoveInput = Vector2.right, HorizontalSpeed = 3f, GaitPhase = .1f });
            Assert.That(buffer.Evaluate(11, out var pose), Is.True);
            Assert.That(pose.Position.x, Is.EqualTo(1f).Within(.001f));
            Assert.That(pose.LeanAmount, Is.EqualTo(.5f).Within(.001f));
            Assert.That(pose.PitchDegrees, Is.EqualTo(-10f).Within(.001f));
            Assert.That(pose.MoveInput.x, Is.EqualTo(.5f).Within(.001f));
            Assert.That(pose.HorizontalSpeed, Is.EqualTo(1.5f).Within(.001f));
            Assert.That(pose.GaitPhase, Is.EqualTo(0f).Within(.001f));
            Assert.That(pose.LocomotionState, Is.EqualTo(LocomotionState.Jump));
        }

        [Test]
        public void StationarySamplesAdvanceClockAndClockNeverReverses()
        {
            ObserverTimeline.Reset(); var b=new TimestampedPoseBuffer();
            b.Push(new ObserverPose { ServerTick=90, Rotation=Quaternion.identity });
            b.Push(new ObserverPose { ServerTick=100, Rotation=Quaternion.identity });
            Assert.That(b.Evaluate(95, out _), Is.True); Assert.That(b.ActualTick, Is.EqualTo(95));
            ObserverTimeline.Observe(100,30,10); var t=ObserverTimeline.Evaluate(10.05);
            ObserverTimeline.Observe(99,30,11); Assert.That(ObserverTimeline.Evaluate(10.04), Is.GreaterThanOrEqualTo(t));
            ObserverTimeline.Reset();
        }

        [Test]
        public void RemotePoseClockIsPerPlayerAndCatchesUpAcrossFrames()
        {
            var observed = new ObserverPoseClock();
            var unrelated = new ObserverPoseClock();
            observed.Observe(100, 30, 10);
            double first = observed.Sample(10.02);
            unrelated.Observe(200, 30, 10.03);
            unrelated.Sample(10.04);
            Assert.That(observed.RenderTick, Is.EqualTo(first),
                "another player's newer tick must not advance this model");

            observed.Observe(101, 30, 10.033333);
            double moving = observed.Sample(10.05);
            Assert.That(moving, Is.GreaterThan(first));
            for (double frameTime = 10.058; frameTime < 10.15; frameTime += .008)
                observed.Sample(frameTime);
            double beforeBurst = observed.RenderTick;
            observed.Observe(108, 30, 10.15); // a late burst of samples
            double caughtUp = observed.Sample(10.158);
            Assert.That(caughtUp, Is.GreaterThan(beforeBurst));
            Assert.That(caughtUp - beforeBurst, Is.LessThan(.5),
                "a delayed packet must not display several ticks in one frame");
            observed.Reset();
            Assert.That(observed.Sample(20), Is.Zero);
        }

        [Test]
        public void RealHistoryInterpolatesAndRejectsOldStorageMargin()
        {
            var managerGo=new GameObject("rewind"); var target=new GameObject("target");
            try
            {
                var lag=managerGo.AddComponent<ServerLagCompensation>(); var box=target.AddComponent<BoxCollider>();
                lag.RegisterPlayer(target.transform,new Collider[]{box});
                for (uint t=90;t<=100;t++) { target.transform.position=Vector3.right*t; lag.Capture(t); }
                Assert.That(lag.TryBeginRewind(84.99,null,out double _), Is.False);
                Assert.That(lag.TryBeginRewind(94.5,null,out double used), Is.True);
                Assert.That(used,Is.EqualTo(94.5)); Assert.That(target.transform.position.x,Is.EqualTo(94.5).Within(.001));
                lag.EndRewind(); Assert.That(target.transform.position.x,Is.EqualTo(100));
            }
            finally { Object.DestroyImmediate(target); Object.DestroyImmediate(managerGo); }
        }
    }
}
