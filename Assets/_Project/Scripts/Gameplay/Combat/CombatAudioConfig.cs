using UnityEngine;

namespace Game.Gameplay.Combat
{
    [CreateAssetMenu(menuName = "UnityFps/Audio/Combat Audio Config")]
    public sealed class CombatAudioConfig : ScriptableObject
    {
        public AudioClip WalkingLoop;
        public AudioClip RunningLoop;
        [Min(0f)] public float WalkMinSpeed = 0.45f;
        [Min(0f)] public float SprintMinSpeed = 2.0f;
        [Range(0f, 1f)] public float LocalFootstepVolume = 0.35f;
        [Range(0f, 1f)] public float RemoteFootstepVolume = 0.5f;
        [Min(1f)] public float FootstepMaxDistance = 18f;
        [Range(0f, 1f)] public float RemoteGunVolume = 0.8f;
        [Min(1f)] public float RemoteGunMaxDistance = 65f;
        [Min(0f)] public float ImpactMinSpeed = 1.3f;
        [Min(0f)] public float ImpactCooldownSeconds = 0.12f;
        [Range(0f, 1f)] public float ImpactVolume = 0.5f;
        [Min(1f)] public float ImpactMaxDistance = 20f;
        [Range(0f, 1f)] public float DetonateVolume = 0.9f;
        [Min(1f)] public float DetonateMaxDistance = 50f;

        public bool IsValid => WalkingLoop != null && RunningLoop != null
            && WalkMinSpeed > 0f && SprintMinSpeed > WalkMinSpeed
            && FootstepMaxDistance > 0f && RemoteGunMaxDistance > 0f;
    }
}
