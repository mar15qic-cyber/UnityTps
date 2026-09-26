using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>Places a spawned controller so the authored shoe sole meets the floor.
    /// CharacterController.Move stops one skin width above contact, including after a teleport.</summary>
    public static class SpawnGrounding
    {
        public static float RootYForGround(float groundY, CharacterController controller)
        {
            if (controller == null) return groundY;
            float capsuleBottom = controller.center.y - controller.height * .5f;
            return groundY + controller.skinWidth - capsuleBottom;
        }

        public static Vector3 Align(Vector3 marker, CharacterController controller)
        {
            Vector3 origin = marker + Vector3.up * .5f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
            float highest = float.NegativeInfinity;
            foreach (var hit in hits)
            {
                if (hit.collider == null || hit.normal.y < .65f
                    || hit.point.y > marker.y + .15f
                    || hit.collider.GetComponentInParent<NetworkCombatAuthority>() != null)
                    continue;
                if (hit.point.y > highest) highest = hit.point.y;
            }
            if (float.IsNegativeInfinity(highest)) return marker;
            marker.y = RootYForGround(highest, controller);
            return marker;
        }
    }
}
