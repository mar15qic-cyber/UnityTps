using UnityEngine;

namespace Game.Gameplay.Combat
{
    [CreateAssetMenu(menuName = "UnityFps/Combat/Throwable Catalog")]
    public sealed class ThrowableCatalog : ScriptableObject
    {
        public ThrowableDefinition Frag;
        public ThrowableDefinition Frag2;
        public ThrowableDefinition Frag3;
        public ThrowableDefinition Flash;
        public ThrowableDefinition Smoke;
        public GameObject NetworkProjectilePrefab;
        [Min(0f)] public float ReleaseDelaySeconds = 0.35f;
        [Min(0f)] public float ThrowActionSeconds = 1.15f;

        public ThrowableDefinition Get(ThrowableType type) => type switch
        {
            ThrowableType.Frag => Frag,
            ThrowableType.Flash => Flash,
            ThrowableType.Smoke => Smoke,
            _ => null
        };

        public ThrowableDefinition Get(string id) => id switch
        {
            "throwable.frag" => Frag, "throwable.frag_02" => Frag2, "throwable.frag_03" => Frag3,
            "throwable.flash" => Flash, "throwable.smoke" => Smoke, _ => null
        };

        public bool IsValid(out string reason)
        {
            if (NetworkProjectilePrefab == null) { reason = "network projectile prefab missing"; return false; }
            if (ReleaseDelaySeconds <= 0f || ThrowActionSeconds <= ReleaseDelaySeconds)
            { reason = "throw timing invalid"; return false; }
            foreach (ThrowableType type in System.Enum.GetValues(typeof(ThrowableType)))
            {
                var definition = Get(type);
                if (definition == null || definition.Type != type)
                { reason = $"{type}: definition missing or type mismatched"; return false; }
                if (!definition.IsValid(out reason))
                { reason = $"{type}: {reason}"; return false; }
            }
            reason = null;
            return true;
        }
    }
}
