using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>Owner-only ring contact/carry; never changes grenade gameplay or world prefabs.</summary>
    public sealed class FPThrowablePinView : MonoBehaviour
    {
        public const float SeparationSeconds = .12f;
        public static readonly Vector3 FingerContact = new(0f, .012f, 0f);
        [SerializeField] private Transform ring;
        [SerializeField] private Transform grip;
        [SerializeField] private Vector3 carryPosition;
        [SerializeField] private Quaternion carryRotation = Quaternion.identity;
        private Transform _hand, _finger;
        private bool _detached;
        public Transform Ring => ring;
        public Transform Grip => grip;
        public bool Detached => _detached;

        public void Bind(Transform leftHand)
        {
            _hand = leftHand;
            _finger = _hand != null ? _hand.Find("finger_01_L/finger_02_L/finger_03_L") : null;
        }

        // Called after the animation graph, including while the grenade body is
        // hidden after release. Its detached ring remains visible with the left hand.
        public void Present(bool throwing, float seconds, float preparationSeconds)
        {
            if (_finger == null || ring == null) return;
            if (!_detached && throwing && seconds >= SeparationSeconds)
            {
                ring.SetParent(_finger, false);
                ring.SetLocalPositionAndRotation(carryPosition, carryRotation);
                _detached = true;
            }
            if (_detached) return;
            float contact = throwing ? Mathf.SmoothStep(0, 1, seconds / .07f)
                : Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.10f, .28f, preparationSeconds));
            AlignContact(contact);
        }

        public void AlignContact(float weight)
        {
            if (_finger == null || grip == null || weight <= 0) return;
            // Only the supporting arm receives the per-model contact correction.
            // Finger shape and palm rotation remain authored by the clip.
            Vector3 offset = grip.position - _finger.TransformPoint(FingerContact);
            TwoBoneIKSolver.Solve(_hand.parent.parent, _hand.parent, _hand,
                _hand.position + offset, _hand.rotation, weight, 1f);
        }

        public void Configure(Transform ringTransform, Transform gripTransform, Vector3 position, Quaternion rotation)
        { ring = ringTransform; grip = gripTransform; carryPosition = position; carryRotation = rotation; }

        public void Clear()
        {
            if (ring == null || !_detached) return;
            ring.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(ring.gameObject); else DestroyImmediate(ring.gameObject);
            ring = null;
        }

        private void OnDestroy() => Clear();
    }
}
