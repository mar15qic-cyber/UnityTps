using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Shared analytic two-bone solver for first- and third-person weapon grips.
    /// The current animation supplies the bend plane, so authored elbow motion is
    /// preserved while the wrist is corrected to a weapon-specific target.
    /// </summary>
    internal static class TwoBoneIKSolver
    {
        public static void Solve(
            Transform upperArm,
            Transform lowerArm,
            Transform hand,
            Vector3 targetPosition,
            Quaternion targetRotation,
            float positionWeight,
            float rotationWeight,
            Vector3? polePosition = null)
        {
            if (upperArm == null || lowerArm == null || hand == null) return;

            positionWeight = Mathf.Clamp01(positionWeight);
            rotationWeight = Mathf.Clamp01(rotationWeight);
            if (positionWeight <= 0.0001f && rotationWeight <= 0.0001f) return;

            Vector3 shoulder = upperArm.position;
            Vector3 elbow = lowerArm.position;
            Vector3 wrist = hand.position;
            Vector3 goal = Vector3.Lerp(wrist, targetPosition, positionWeight);

            float upperLength = Vector3.Distance(shoulder, elbow);
            float lowerLength = Vector3.Distance(elbow, wrist);
            if (upperLength <= 0.00001f || lowerLength <= 0.00001f) return;

            Vector3 shoulderToGoal = goal - shoulder;
            float rawDistance = shoulderToGoal.magnitude;
            if (rawDistance <= 0.00001f) return;

            Vector3 direction = shoulderToGoal / rawDistance;
            float distance = Mathf.Clamp(
                rawDistance,
                Mathf.Abs(upperLength - lowerLength) + 0.0001f,
                upperLength + lowerLength - 0.0001f);

            Vector3 currentUpper = (elbow - shoulder).normalized;
            Vector3 currentLower = (wrist - elbow).normalized;
            Vector3 planeNormal = polePosition.HasValue
                ? Vector3.Cross(direction, polePosition.Value - shoulder)
                : Vector3.Cross(currentUpper, currentLower);
            if (planeNormal.sqrMagnitude <= 0.000001f)
            {
                planeNormal = Vector3.Cross(currentUpper, direction);
                if (planeNormal.sqrMagnitude <= 0.000001f)
                    planeNormal = Vector3.Cross(direction, upperArm.up);
            }
            planeNormal.Normalize();

            Vector3 bendDirection = Vector3.Cross(planeNormal, direction).normalized;
            if (Vector3.Dot(bendDirection, elbow - shoulder) < 0f)
                bendDirection = -bendDirection;

            float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance)
                / (2f * distance);
            float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            Vector3 desiredElbow = shoulder + direction * along + bendDirection * height;

            RotateToward(upperArm, elbow - shoulder, desiredElbow - shoulder);
            RotateToward(lowerArm, hand.position - lowerArm.position, goal - lowerArm.position);

            if (rotationWeight > 0.0001f)
                hand.rotation = Quaternion.Slerp(hand.rotation, targetRotation, rotationWeight);
        }

        private static void RotateToward(Transform bone, Vector3 from, Vector3 to)
        {
            if (from.sqrMagnitude <= 0.000001f || to.sqrMagnitude <= 0.000001f) return;
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }
    }
}
