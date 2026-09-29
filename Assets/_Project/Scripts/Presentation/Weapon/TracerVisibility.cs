using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>Cosmetic visibility only. Never feeds back into shot acceptance or damage.</summary>
    internal sealed class TracerVisibility
    {
        internal const float EndpointTolerance = .02f;
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[32];

        internal bool CanShow(Vector3 start, Vector3 end, Transform shooterRoot, int mask)
        {
            if (!Finite(start) || !Finite(end)) return false;
            Vector3 delta = end - start;
            float distance = delta.magnitude;
            if (!float.IsFinite(distance) || distance < .0001f) return false;

            int count = Physics.OverlapSphereNonAlloc(start, .001f, _overlaps, mask,
                QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) return false;
            for (int i = 0; i < count; i++)
                if (IsEnvironment(_overlaps[i], shooterRoot)
                    && (_overlaps[i].ClosestPoint(start) - start).sqrMagnitude < .00000001f)
                    return false;

            float length = distance - EndpointTolerance;
            if (length <= 0f) return true;
            count = Physics.RaycastNonAlloc(start, delta / distance, _hits, length, mask,
                QueryTriggerInteraction.Ignore);
            if (count == _hits.Length) return false;
            for (int i = 0; i < count; i++)
                if (IsEnvironment(_hits[i].collider, shooterRoot)) return false;
            return true;
        }

        private static bool IsEnvironment(Collider collider, Transform shooterRoot)
        {
            if (collider == null || collider.isTrigger
                || shooterRoot != null && collider.transform.IsChildOf(shooterRoot)) return false;
            var tag = collider.GetComponent<HitVolumeTag>();
            if (tag != null && tag.Role == HitVolumeRole.MovementBlocker) return false;
            // Do not use DamageableTarget as the filter: destructible environment still occludes.
            return collider.GetComponentInParent<PlayerNetworkAdapter>() == null
                && collider.GetComponentInParent<NetworkCombatAuthority>() == null;
        }

        private static bool Finite(Vector3 v)
            => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
