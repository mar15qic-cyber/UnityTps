using System;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    [Serializable]
    public struct TpThrowClips
    {
        public AnimationClip Start;
        public AnimationClip Pose;
        public AnimationClip Release;
        public AnimationClip Cancel;
    }

    [CreateAssetMenu(menuName = "UnityFps/Animation/TP Throw Catalog")]
    public sealed class TpThrowAnimationCatalog : ScriptableObject
    {
        public TpThrowClips Rifle;
        public TpThrowClips Handgun;

        public TpThrowClips For(WeaponDefinition definition)
        {
            string id = definition != null ? definition.WeaponId : string.Empty;
            return id.Contains("pistol") || id.Contains("handgun") ? Handgun : Rifle;
        }
    }
}
