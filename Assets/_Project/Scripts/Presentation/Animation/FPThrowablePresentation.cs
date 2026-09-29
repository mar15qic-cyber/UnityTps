using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>Owned first-person model clips and grip; no gameplay timing.</summary>
    public sealed class FPThrowablePresentation : MonoBehaviour
    {
        [SerializeField] private AnimationClip draw;
        [SerializeField] private AnimationClip prepare;
        [SerializeField] private AnimationClip throwing;
        [SerializeField] private float gripYaw = 180f;
        public AnimationClip Draw => draw;
        public AnimationClip Prepare => prepare;
        public AnimationClip Throw => throwing;
        public Quaternion GripAdjustment => Quaternion.Euler(0, gripYaw, 0);
        public void Configure(AnimationClip drawClip, AnimationClip prepareClip, AnimationClip throwClip)
        { draw = drawClip; prepare = prepareClip; throwing = throwClip; gripYaw = 180f; }
    }
}
