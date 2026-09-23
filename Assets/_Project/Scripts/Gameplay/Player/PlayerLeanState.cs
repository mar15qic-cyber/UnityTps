using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>Deterministic, snapshot-restorable lean simulation. Physics is sampled on the authority.</summary>
    public sealed class PlayerLeanState
    {
        public float Amount { get; private set; }
        public sbyte Intent { get; private set; }

        public void Restore(float amount, sbyte intent)
        {
            Amount = Mathf.Clamp(amount, -1f, 1f);
            Intent = (sbyte)Mathf.Clamp(intent, -1, 1);
        }

        public void Clear() => Restore(0f, 0);

        public void Step(sbyte intent, bool grounded, bool jump, float dt, Vector3 root, Quaternion yaw,
            Transform self, bool checkObstruction)
        {
            Intent = grounded && !jump ? (sbyte)Mathf.Clamp(intent, -1, 1) : (sbyte)0;
            float target = Intent;
            if (checkObstruction && target != 0)
                target *= AllowedFraction(root, yaw, target, self);
            Amount = Mathf.MoveTowards(Amount, target,
                Mathf.Max(0f, dt) / (Mathf.Abs(target) > Mathf.Abs(Amount)
                    ? LeanProfile.LeanInSeconds : LeanProfile.LeanOutSeconds));
            if (!grounded || jump) Amount = 0f;
        }

        public static float AllowedFraction(Vector3 root, Quaternion yaw, float sign, Transform self)
        {
            float allowed = 1f;
            // Sweep both head and eye, not just the camera point. Ignore the owner's colliders.
            Vector3[] starts =
            {
                LeanProfile.HeadCenter(root, yaw, 0f),
                LeanProfile.Eye(root, yaw, 0f),
                LeanProfile.ChestCenter(root, yaw, 0f),
            };
            for (int i = 0; i < starts.Length; i++)
            {
                Vector3 end = i == 0 ? LeanProfile.HeadCenter(root, yaw, sign)
                    : i == 1 ? LeanProfile.Eye(root, yaw, sign)
                    : LeanProfile.ChestCenter(root, yaw, sign);
                Vector3 delta = end - starts[i];
                float distance = delta.magnitude;
                if (distance < .0001f) continue;
                float radius = i == 0 ? .15f : i == 1 ? .06f : .22f;
                foreach (var hit in Physics.SphereCastAll(starts[i], radius, delta / distance,
                             distance, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || self != null && hit.collider.transform.IsChildOf(self)) continue;
                    allowed = Mathf.Min(allowed, Mathf.Clamp01((hit.distance - .02f) / distance));
                }
            }
            // A sweep can miss colliders already overlapping its start or end (for
            // example a very thin corner). Constrain the final pose as well.
            if (!PoseClear(root, yaw, sign * allowed, self))
            {
                float low = 0f;
                float high = allowed;
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    float mid = (low + high) * .5f;
                    if (PoseClear(root, yaw, sign * mid, self)) low = mid;
                    else high = mid;
                }
                allowed = low;
            }
            return allowed;
        }

        private static bool PoseClear(Vector3 root, Quaternion yaw, float lean, Transform self)
        {
            Vector3[] centers =
            {
                LeanProfile.HeadCenter(root, yaw, lean),
                LeanProfile.Eye(root, yaw, lean),
                LeanProfile.ChestCenter(root, yaw, lean),
            };
            for (int i = 0; i < centers.Length; i++)
            {
                float radius = i == 0 ? .15f : i == 1 ? .06f : .22f;
                foreach (var collider in Physics.OverlapSphere(centers[i], radius, ~0,
                             QueryTriggerInteraction.Ignore))
                    if (collider != null && (self == null || !collider.transform.IsChildOf(self)))
                        return false;
            }
            return true;
        }
    }
}
