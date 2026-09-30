using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>Invisible map limits and stair ramps block movement, while visible mesh colliders own shots.</summary>
    public sealed class MapMovementBarrier : MonoBehaviour
    {
        private void Awake() => HitVolumeTag.Assign(gameObject, HitVolumeRole.MovementBlocker);
    }
}
