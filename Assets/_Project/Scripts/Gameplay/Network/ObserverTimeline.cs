using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    public struct ObserverPose
    {
        public uint ServerTick;
        public uint LifeEpoch;
        public Vector3 Position;
        public Quaternion Rotation;
        public float LeanAmount;
    }

    /// <summary>One clock per client session; arrival time is never used as a snapshot timestamp.</summary>
    public static class ObserverTimeline
    {
        private static uint _latest;
        private static double _arrival, _render;
        private static double _interval = 1.0 / 30;
        private static int _rate = 30;
        public static double DelaySeconds => Math.Max(.05, Math.Min(.15, _interval * 2.1));
        private static int _renderFrame = -1;
        private static double _presented;
        public static double PresentedTick => _presented > 0 ? _presented : RenderTick;
        public static double RenderTick => Evaluate(Time.unscaledTimeAsDouble);
        public static double SampleForRendering()
        {
            if (_renderFrame != Time.frameCount)
            {
                _renderFrame = Time.frameCount;
                _presented = Evaluate(Time.unscaledTimeAsDouble);
            }
            return _presented;
        }
        public static bool Ready => _latest > 0;
        public static void Reset() { _latest = 0; _arrival = _render = _presented = 0; _renderFrame = -1; _interval = 1.0 / 30; }
        public static void Observe(uint tick, int rate, double arrival)
        {
            if (tick <= _latest) return;
            if (_latest > 0)
            {
                double interval = (arrival - _arrival) / Math.Max(1, tick - _latest);
                if (interval > .0005 && interval < 1) _interval += .15 * (interval - _interval);
            }
            _rate = Math.Max(1, rate); _latest = tick; _arrival = arrival;
        }
        public static double Evaluate(double now)
        {
            if (_latest == 0) return 0;
            double desired = _latest + Math.Max(0, now - _arrival) * _rate - DelaySeconds * _rate;
            // Never extrapolate beyond data, and never reverse the render clock after jitter.
            _render = Math.Max(_render, Math.Max(1, Math.Min(_latest, desired)));
            return _render;
        }
    }

    public sealed class TimestampedPoseBuffer
    {
        private readonly List<ObserverPose> _samples = new(64);
        public bool Starved { get; private set; }
        public double ActualTick { get; private set; }
        public void Reset() { _samples.Clear(); Starved = true; ActualTick = 0; }
        public bool Push(ObserverPose pose)
        {
            if (_samples.Count > 0)
            {
                var last = _samples[_samples.Count - 1];
                if (pose.LifeEpoch < last.LifeEpoch || pose.ServerTick <= last.ServerTick) return false;
                if (pose.LifeEpoch != last.LifeEpoch) Reset();
            }
            _samples.Add(pose);
            if (_samples.Count > 64) _samples.RemoveAt(0);
            return true;
        }
        public bool Evaluate(double tick, out ObserverPose pose)
        {
            pose = default;
            if (_samples.Count == 0) { Starved = true; return false; }
            var first = _samples[0]; var last = _samples[_samples.Count - 1];
            if (tick <= first.ServerTick) { pose = first; ActualTick = first.ServerTick; Starved = tick < first.ServerTick; return true; }
            if (tick >= last.ServerTick) { pose = last; ActualTick = last.ServerTick; Starved = tick > last.ServerTick; return true; }
            for (int i = 1; i < _samples.Count; i++)
            {
                var b = _samples[i]; if (b.ServerTick < tick) continue;
                var a = _samples[i - 1]; float t = (float)((tick - a.ServerTick) / (b.ServerTick - a.ServerTick));
                pose = a; pose.Position = Vector3.Lerp(a.Position, b.Position, t);
                pose.Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                pose.LeanAmount = Mathf.Lerp(a.LeanAmount, b.LeanAmount, t);
                ActualTick = tick; Starved = false; return true;
            }
            return false;
        }
    }

    public struct TimedFireRequest
    {
        public uint ShotId, InputTick, LifeEpoch;
        public double DisplayTick;
        // The shot's displayed camera ray, before this shot adds recoil. Never a client hit result.
        public Vector3 AimOrigin, AimDirection;
        public float Ads01;
    }

    public static class ShotAimPolicy
    {
        // Covers bounded render interpolation and recoil debt; history still limits origin/time/life.
        public const float MaxOriginError = 0.75f;
        public const float MaxDirectionError = 25f;
        public static bool Validate(TimedFireRequest shot, Vector3 serverOrigin, Vector3 serverDirection)
            => Finite(shot.AimOrigin) && Finite(shot.AimDirection)
               && shot.AimDirection.sqrMagnitude > 0.99f && shot.AimDirection.sqrMagnitude < 1.01f
               && float.IsFinite(shot.Ads01) && shot.Ads01 >= 0f && shot.Ads01 <= 1f
               && Vector3.Distance(shot.AimOrigin, serverOrigin) <= MaxOriginError
               && Vector3.Angle(shot.AimDirection, serverDirection) <= MaxDirectionError;

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        // One random stream per request. Rejected requests cannot shift every subsequent bullet.
        public static int SpreadSeed(uint shotId, uint lifeEpoch)
        {
            unchecked { return (int)((shotId * 747796405u) ^ (lifeEpoch * 2891336453u) ^ 277803737u); }
        }
    }

    public static class ShotTimingPolicy
    {
        public enum Decision { Ready, Wait, StaleLife, InvalidTime, InputTimeout }
        public const double MaxWaitSeconds = .2;
        public static bool ValidDisplayTick(double tick, double now, int rate) =>
            !double.IsNaN(tick) && !double.IsInfinity(tick) && tick > 0 && tick <= now
            && now - tick <= Math.Max(1, rate) * .2 + 1e-6;
        public static Decision Evaluate(TimedFireRequest request, uint life, double serverTick, int rate,
            bool hasInput, uint acknowledged, double waitedSeconds)
        {
            if (request.LifeEpoch != life) return Decision.StaleLife;
            if (!ValidDisplayTick(request.DisplayTick, serverTick, rate)) return Decision.InvalidTime;
            if (waitedSeconds > MaxWaitSeconds) return Decision.InputTimeout;
            if (hasInput) return Decision.Ready;
            return request.InputTick > acknowledged && waitedSeconds < MaxWaitSeconds
                ? Decision.Wait : Decision.InputTimeout;
        }
    }
}
