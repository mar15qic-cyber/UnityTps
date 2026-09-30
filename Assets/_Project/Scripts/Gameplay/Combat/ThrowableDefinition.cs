using UnityEngine;

namespace Game.Gameplay.Combat
{
    public enum ThrowableType : byte { Frag, Flash, Smoke }

    [CreateAssetMenu(menuName = "UnityFps/Combat/Throwable Definition")]
    public sealed class ThrowableDefinition : ScriptableObject
    {
        public ThrowableType Type;
        [Min(0)] public int InitialCount = 1;
        public GameObject ModelPrefab;
        public GameObject EffectPrefab;
        public AudioClip ThrowClip;
        public AudioClip ImpactClip;
        public AudioClip DetonateClip;
        [Min(0.01f)] public float FuseSeconds;
        [Min(0.01f)] public float Radius;
        [Min(0f)] public float EffectSeconds;
        [Min(0f)] public float ForwardSpeed;
        [Min(0f)] public float UpwardSpeed;
        [Range(0f, 1f)] public float InheritedHorizontalVelocity;
        [Min(0.001f)] public float Mass;
        [Min(0f)] public float AirDrag;
        [Range(0f, 1f)] public float Bounciness;
        [Range(0f, 1f)] public float DynamicFriction;
        [Min(0f)] public float RollStopSpeed;
        [Min(0f)] public float FragMaxDamage;
        [Range(0f, 1f)] public float FlashStrength;
        [Range(0f, 1f)] public float SmokeDensity;

        public bool IsValid(out string reason)
        {
            if (ModelPrefab == null || ThrowClip == null || ImpactClip == null
                || Type != ThrowableType.Smoke && DetonateClip == null)
            { reason = "model or required audio is missing"; return false; }
            if (FuseSeconds <= 0f || Radius <= 0f || Mass <= 0f || ForwardSpeed <= 0f)
            { reason = "physical or effect value is invalid"; return false; }
            if (Type == ThrowableType.Frag && (FragMaxDamage <= 0f || EffectPrefab == null))
            { reason = "frag damage or explosion effect is missing"; return false; }
            if (Type == ThrowableType.Flash && FlashStrength <= 0f)
            { reason = "flash strength is invalid"; return false; }
            if (Type == ThrowableType.Smoke && (SmokeDensity <= 0f || EffectPrefab == null))
            { reason = "smoke density or effect is missing"; return false; }
            reason = null;
            return true;
        }
    }

}
