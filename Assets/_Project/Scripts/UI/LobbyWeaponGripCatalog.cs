using System;
using UnityEngine;

namespace Game.UI
{
    /// <summary>Native FP hand contact landmarks transferred into each rigid TP weapon's mesh space.</summary>
    public sealed class LobbyWeaponGripCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Grip
        {
            public string weaponId;
            public Vector3 wrist, index, middle, indexDistal;
        }
        public Grip[] grips = Array.Empty<Grip>();
        public Grip Find(string id) => Array.Find(grips, g => g.weaponId == id);
        public static Quaternion HandFrame(Vector3 wrist, Vector3 index, Vector3 middle)
            => Quaternion.LookRotation((middle - wrist).normalized, Vector3.Cross(index - wrist, middle - wrist).normalized);

        public static void Apply(Grip grip, Animator animator, Transform weapon)
        {
            if (grip == null || animator == null || weapon == null) return;
            var wrist = animator.GetBoneTransform(HumanBodyBones.RightHand);
            var index = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            var middle = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            if (wrist == null || index == null || middle == null) return;
            var rotation = HandFrame(wrist.position, index.position, middle.position)
                * Quaternion.Inverse(HandFrame(grip.wrist, grip.index, grip.middle));
            weapon.SetPositionAndRotation(wrist.position - rotation * grip.wrist, rotation);
            var intermediate = animator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate);
            var distal = animator.GetBoneTransform(HumanBodyBones.RightIndexDistal);
            if (intermediate != null && distal != null)
                Game.Presentation.Animation.TwoBoneIKSolver.Solve(index, intermediate, distal,
                    weapon.TransformPoint(grip.indexDistal), distal.rotation, 1f, 0f);
        }
    }
}
