using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Small, allocation-free pose operations used by the first-person weapon
    /// adapters.  The important distinction is that a weapon contact marker
    /// is not a wrist target: it is composed with a reference contact-to-wrist
    /// offset, or with the inverse of the hand-to-magazine held pose.
    /// </summary>
    public readonly struct FPContactPose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public FPContactPose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    public static class FPWeaponPoseMath
    {
        /// <summary>
        /// Aligns a sight axis to a target forward while using the remaining
        /// roll degree of freedom to keep a trigger-hand grip oriented to its
        /// animated palm.  The search is deterministic and bounded to one
        /// degree, so calibration and runtime tests use identical math.
        /// </summary>
        public static Quaternion SolveSightAxisWithGripRoll(
            Vector3 sightAxisWorld,
            Vector3 targetForwardWorld,
            Quaternion hipRootWorldRotation,
            Quaternion gripLocalRotation,
            Quaternion desiredGripWorldRotation,
            bool solveGripRoll,
            out float gripRotationError)
        {
            gripRotationError = float.PositiveInfinity;
            if (sightAxisWorld.sqrMagnitude < 0.000001f
                || targetForwardWorld.sqrMagnitude < 0.000001f)
                return hipRootWorldRotation;

            Vector3 targetForward = targetForwardWorld.normalized;
            Quaternion axisAlignment = Quaternion.FromToRotation(
                sightAxisWorld.normalized, targetForward);
            Quaternion alignedRoot = axisAlignment * hipRootWorldRotation;
            if (!solveGripRoll)
            {
                gripRotationError = Quaternion.Angle(
                    alignedRoot * gripLocalRotation, desiredGripWorldRotation);
                return alignedRoot;
            }

            Quaternion best = alignedRoot;
            float bestError = float.PositiveInfinity;
            // One-degree sampling bounds the residual roll quantization to 0.5°.
            for (int step = 0; step <= 360; step++)
            {
                float roll = -180f + step;
                Quaternion candidate = Quaternion.AngleAxis(roll, targetForward)
                    * alignedRoot;
                float error = Quaternion.Angle(
                    candidate * gripLocalRotation, desiredGripWorldRotation);
                if (error < bestError)
                {
                    bestError = error;
                    best = candidate;
                }
            }

            gripRotationError = bestError;
            return best;
        }

        /// <summary>
        /// Converts a contact pose into the wrist pose using a reference
        /// contact-to-wrist transform authored in contact local space.
        /// </summary>
        public static FPContactPose ComposeContactToWrist(
            Vector3 contactPosition,
            Quaternion contactRotation,
            Vector3 contactToWristPosition,
            Quaternion contactToWristRotation)
        {
            return new FPContactPose(
                contactPosition + contactRotation * contactToWristPosition,
                contactRotation * contactToWristRotation);
        }

        /// <summary>
        /// Resolves the hand pose from a desired magazine pose and a
        /// hand-local held-magazine pose.  If M = H * Held, then H = M * Held^-1.
        /// </summary>
        public static FPContactPose ResolveHandFromHeldMagazine(
            Vector3 magazinePosition,
            Quaternion magazineRotation,
            Vector3 heldLocalPosition,
            Quaternion heldLocalRotation)
        {
            Quaternion inverseHeldRotation = Quaternion.Inverse(heldLocalRotation);
            Vector3 inverseHeldPosition = inverseHeldRotation * -heldLocalPosition;
            return new FPContactPose(
                magazinePosition + magazineRotation * inverseHeldPosition,
                magazineRotation * inverseHeldRotation);
        }

        /// <summary>
        /// Resolves the magazine pose carried by a hand. If M = H * Held,
        /// this is the forward operation paired with ResolveHandFromHeldMagazine.
        /// </summary>
        public static FPContactPose ComposeHeldMagazine(
            Vector3 handPosition,
            Quaternion handRotation,
            Vector3 heldLocalPosition,
            Quaternion heldLocalRotation)
        {
            return new FPContactPose(
                handPosition + handRotation * heldLocalPosition,
                handRotation * heldLocalRotation);
        }

        public static FPContactPose ComposeContactToWrist(Transform contact,
            Vector3 contactToWristPosition, Quaternion contactToWristRotation)
        {
            return contact == null
                ? default
                : ComposeContactToWrist(contact.position, contact.rotation,
                    contactToWristPosition, contactToWristRotation);
        }

        public static FPContactPose ResolveHandFromHeldMagazine(Transform magazineContact,
            Vector3 heldLocalPosition, Quaternion heldLocalRotation)
        {
            return magazineContact == null
                ? default
                : ResolveHandFromHeldMagazine(magazineContact.position,
                    magazineContact.rotation, heldLocalPosition, heldLocalRotation);
        }
    }
}
