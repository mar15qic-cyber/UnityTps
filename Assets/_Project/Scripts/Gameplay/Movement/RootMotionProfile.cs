using System;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    public enum RootMotionGait
    {
        Walk,
        Sprint
    }

    public enum RootMotionDirection
    {
        Forward,
        ForwardRight,
        Right,
        BackRight,
        Backward,
        BackLeft,
        Left,
        ForwardLeft
    }

    [Serializable]
    public sealed class RootMotionTrack
    {
        [SerializeField] private string sourceClipName;
        [SerializeField] private float sourceDuration = 1f;
        [SerializeField] private Vector2[] cumulativePositions = Array.Empty<Vector2>();
        [SerializeField] private float[] cumulativeYaw = Array.Empty<float>();

        public string SourceClipName => sourceClipName;
        public float SourceDuration => sourceDuration;
        public bool IsValid => cumulativePositions != null && cumulativePositions.Length >= 2;
        public Vector2 TotalPosition => IsValid ? cumulativePositions[cumulativePositions.Length - 1] : Vector2.zero;
        public float TotalYaw => cumulativeYaw != null && cumulativeYaw.Length == cumulativePositions.Length
            ? cumulativeYaw[cumulativeYaw.Length - 1]
            : 0f;

        public Vector2 EvaluateCumulative(float unwrappedPhase)
        {
            if (!IsValid) return Vector2.zero;
            int cycles = Mathf.FloorToInt(unwrappedPhase);
            float phase = unwrappedPhase - cycles;
            return TotalPosition * cycles + SamplePosition(phase);
        }

        public float EvaluateCumulativeYaw(float unwrappedPhase)
        {
            if (!IsValid || cumulativeYaw == null || cumulativeYaw.Length != cumulativePositions.Length) return 0f;
            int cycles = Mathf.FloorToInt(unwrappedPhase);
            float phase = unwrappedPhase - cycles;
            return TotalYaw * cycles + SampleYaw(phase);
        }

        private Vector2 SamplePosition(float phase)
        {
            float sample = Mathf.Clamp01(phase) * (cumulativePositions.Length - 1);
            int lower = Mathf.Min(Mathf.FloorToInt(sample), cumulativePositions.Length - 1);
            int upper = Mathf.Min(lower + 1, cumulativePositions.Length - 1);
            return Vector2.LerpUnclamped(cumulativePositions[lower], cumulativePositions[upper], sample - lower);
        }

        private float SampleYaw(float phase)
        {
            float sample = Mathf.Clamp01(phase) * (cumulativeYaw.Length - 1);
            int lower = Mathf.Min(Mathf.FloorToInt(sample), cumulativeYaw.Length - 1);
            int upper = Mathf.Min(lower + 1, cumulativeYaw.Length - 1);
            return Mathf.LerpUnclamped(cumulativeYaw[lower], cumulativeYaw[upper], sample - lower);
        }

#if UNITY_EDITOR
        public void SetBakedData(string clipName, float duration, Vector2[] positions, float[] yaw)
        {
            sourceClipName = clipName;
            sourceDuration = Mathf.Max(0.0001f, duration);
            cumulativePositions = positions ?? Array.Empty<Vector2>();
            cumulativeYaw = yaw ?? Array.Empty<float>();
        }
#endif
    }

    [CreateAssetMenu(menuName = "UnityFps/Movement/Root Motion Profile", fileName = "RootMotionProfile")]
    public sealed class RootMotionProfile : ScriptableObject
    {
        public const int SamplesPerCycle = 60;
        public const int DirectionCount = 8;

        [SerializeField] private string versionHash;
        [SerializeField] private float walkCycleDuration = 0.9333334f;
        [SerializeField] private float sprintCycleDuration = 0.6666667f;
        [SerializeField] private float walkSpeed = 1.58f;
        [SerializeField] private float sprintSpeed = 3.44f;
        [SerializeField] private RootMotionTrack[] walkTracks = new RootMotionTrack[DirectionCount];
        [SerializeField] private RootMotionTrack[] sprintTracks = new RootMotionTrack[DirectionCount];

        public string VersionHash => versionHash;
        public float WalkSpeed => walkSpeed;
        public float SprintSpeed => sprintSpeed;
        public bool IsValid => HasValidTracks(walkTracks) && HasValidTracks(sprintTracks);

        public Vector2 EvaluateDelta(
            RootMotionGait gait,
            Vector2 move,
            float phase,
            float deltaTime,
            out float nextPhase,
            out float deltaYaw)
        {
            float duration = gait == RootMotionGait.Sprint ? sprintCycleDuration : walkCycleDuration;
            float phaseAdvance = Mathf.Max(0f, deltaTime) / Mathf.Max(0.0001f, duration);
            nextPhase = Mathf.Repeat(phase + phaseAdvance, 1f);
            deltaYaw = 0f;

            RootMotionTrack[] tracks = gait == RootMotionGait.Sprint ? sprintTracks : walkTracks;
            if (move.sqrMagnitude < 0.0001f || !HasValidTracks(tracks)) return Vector2.zero;
            float targetSpeed = gait == RootMotionGait.Sprint ? sprintSpeed : walkSpeed;

            float angle = Mathf.Atan2(move.x, move.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            float directionSample = angle / 45f;
            int lowerIndex = Mathf.FloorToInt(directionSample) % DirectionCount;
            int upperIndex = (lowerIndex + 1) % DirectionCount;
            float blend = directionSample - Mathf.Floor(directionSample);

            EvaluateTrack(tracks[lowerIndex], phase, phaseAdvance, duration, targetSpeed,
                out Vector2 lowerDelta, out float lowerYaw);
            EvaluateTrack(tracks[upperIndex], phase, phaseAdvance, duration, targetSpeed,
                out Vector2 upperDelta, out float upperYaw);

            Vector2 delta = Vector2.LerpUnclamped(lowerDelta, upperDelta, blend);
            deltaYaw = Mathf.LerpUnclamped(lowerYaw, upperYaw, blend);

            return delta;
        }

        private static void EvaluateTrack(
            RootMotionTrack track,
            float phase,
            float phaseAdvance,
            float canonicalDuration,
            float targetSpeed,
            out Vector2 delta,
            out float deltaYaw)
        {
            if (track == null || !track.IsValid)
            {
                delta = Vector2.zero;
                deltaYaw = 0f;
                return;
            }

            float endPhase = phase + phaseAdvance;
            delta = track.EvaluateCumulative(endPhase) - track.EvaluateCumulative(phase);
            deltaYaw = track.EvaluateCumulativeYaw(endPhase) - track.EvaluateCumulativeYaw(phase);
            float averageSpeed = track.TotalPosition.magnitude / Mathf.Max(0.0001f, canonicalDuration);
            if (averageSpeed > 0.0001f) delta *= targetSpeed / averageSpeed;
        }

        private static bool HasValidTracks(RootMotionTrack[] tracks)
        {
            if (tracks == null || tracks.Length != DirectionCount) return false;
            for (int i = 0; i < tracks.Length; i++)
                if (tracks[i] == null || !tracks[i].IsValid) return false;
            return true;
        }

#if UNITY_EDITOR
        public void SetBakedData(
            string hash,
            float walkDuration,
            float runDuration,
            RootMotionTrack[] bakedWalkTracks,
            RootMotionTrack[] bakedSprintTracks)
        {
            versionHash = hash;
            walkCycleDuration = Mathf.Max(0.0001f, walkDuration);
            sprintCycleDuration = Mathf.Max(0.0001f, runDuration);
            walkSpeed = 1.58f;
            sprintSpeed = 3.44f;
            walkTracks = bakedWalkTracks;
            sprintTracks = bakedSprintTracks;
        }
#endif
    }
}
