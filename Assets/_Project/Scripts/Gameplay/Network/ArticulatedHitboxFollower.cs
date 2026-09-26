using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// Keeps damage volumes on the same animated bones as the visible third-person mesh.
    /// The collider shapes are constant; lag compensation can therefore rewind their
    /// world transforms without needing a separate history of collider dimensions.
    /// </summary>
    [DefaultExecutionOrder(90)]
    public sealed class ArticulatedHitboxFollower : MonoBehaviour
    {
        private struct Binding
        {
            public Transform Volume;
            public Transform Bone;
            public Vector3 BoneLocalPosition;
            public Quaternion BoneLocalRotation;
            public float LeanFraction;
        }

        private readonly List<Binding> _bindings = new(9);
        private float _lean;
        private float _leanSideMeters;

        public bool IsBound => _bindings.Count > 0;

        public void Bind(Animator animator, float leanSideMeters,
            Transform upper, Transform head, Transform leftArm, Transform rightArm,
            Transform leftLeg, Transform rightLeg, Transform leftCalf, Transform rightCalf,
            Transform leftFoot, Transform rightFoot)
        {
            if (IsBound || animator == null || !animator.isHuman) return;
            _leanSideMeters = leanSideMeters;
            Add(animator, upper, HumanBodyBones.Chest, .45f);
            Add(animator, head, HumanBodyBones.Head, 1f);
            Add(animator, leftArm, HumanBodyBones.LeftUpperArm, .55f);
            Add(animator, rightArm, HumanBodyBones.RightUpperArm, .55f);
            Add(animator, leftLeg, HumanBodyBones.LeftUpperLeg, .15f);
            Add(animator, rightLeg, HumanBodyBones.RightUpperLeg, .15f);
            Add(animator, leftCalf, HumanBodyBones.LeftLowerLeg, .10f);
            Add(animator, rightCalf, HumanBodyBones.RightLowerLeg, .10f);
            Add(animator, leftFoot, HumanBodyBones.LeftFoot, .08f);
            Add(animator, rightFoot, HumanBodyBones.RightFoot, .08f);
            SyncNow();
        }

        private void Add(Animator animator, Transform volume, HumanBodyBones bodyBone, float leanFraction)
        {
            if (volume == null) return;
            Transform bone = animator.GetBoneTransform(bodyBone);
            if (bone == null) return;
            _bindings.Add(new Binding
            {
                Volume = volume,
                Bone = bone,
                BoneLocalPosition = bone.InverseTransformPoint(volume.position),
                BoneLocalRotation = Quaternion.Inverse(bone.rotation) * volume.rotation,
                LeanFraction = leanFraction
            });
        }

        public void SetLean(float amount)
        {
            _lean = Mathf.Clamp(amount, -1f, 1f);
            SyncNow();
        }

        public void SyncNow()
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                Binding binding = _bindings[i];
                if (binding.Volume == null || binding.Bone == null) continue;
                Vector3 side = transform.right * (_lean * _leanSideMeters * binding.LeanFraction);
                binding.Volume.SetPositionAndRotation(
                    binding.Bone.TransformPoint(binding.BoneLocalPosition) + side,
                    binding.Bone.rotation * binding.BoneLocalRotation);
            }
        }

        private void LateUpdate() => SyncNow();
    }
}
