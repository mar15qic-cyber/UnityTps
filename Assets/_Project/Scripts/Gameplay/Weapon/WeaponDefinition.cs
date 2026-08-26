using System;
using Game.Gameplay.Movement;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    public enum WeaponFireMode
    {
        SemiAutomatic,
        Automatic
    }

    /// <summary>FP 手臂动画集（clip 挂在各自 rig 的 view prefab 上，由 FPWeaponAnimator 直接播放）。</summary>
    [Serializable]
    public struct WeaponAnimationSet
    {
        public AnimationClip Idle;
        public AnimationClip Fire;
        public AnimationClip ReloadAmmoLeft;
        public AnimationClip ReloadOutOfAmmo;
        public AnimationClip Draw;
        public AnimationClip Holster;
        public AnimationClip DryFire;
    }

    /// <summary>TP 角色移动动画集（全身，Layer0 locomotion mixer 用；按武器族区分）。</summary>
    [Serializable]
    public struct TpLocomotionSet
    {
        public AnimationClip Idle;
        public AnimationClip WalkForward;
        public AnimationClip WalkForwardRight;
        public AnimationClip WalkRight;
        public AnimationClip WalkBackRight;
        public AnimationClip WalkBackward;
        public AnimationClip WalkBackLeft;
        public AnimationClip WalkLeft;
        public AnimationClip WalkForwardLeft;
        public AnimationClip RunForward;
        public AnimationClip RunForwardRight;
        public AnimationClip RunRight;
        public AnimationClip RunBackRight;
        public AnimationClip RunBackward;
        public AnimationClip RunBackLeft;
        public AnimationClip RunLeft;
        public AnimationClip RunForwardLeft;
        public AnimationClip JumpStart;
        public AnimationClip JumpLoop;
        public AnimationClip JumpLand;
    }

    /// <summary>TP 角色上半身动作动画集（Layer1 action 层用；由 WeaponController 事件触发）。</summary>
    [Serializable]
    public struct TpActionSet
    {
        public AnimationClip Fire;
        public AnimationClip ReloadAmmoLeft;
        public AnimationClip ReloadOutOfAmmo;
    }

    /// <summary>武器身份与表现资源；运行时弹药及 damage/rpm 等数值禁止写入本资产。</summary>
    [CreateAssetMenu(menuName = "UnityFps/Weapons/Weapon Definition", fileName = "WeaponDefinition")]
    public sealed class WeaponDefinition : ScriptableObject
    {
        [SerializeField] private string weaponId = "weapon.demo";
        [SerializeField] private string displayName = "Demo Weapon";
        [SerializeField] private WeaponFireMode fireMode = WeaponFireMode.SemiAutomatic;
        [SerializeField] private GameObject firstPersonViewPrefab;
        [SerializeField] private GameObject thirdPersonViewPrefab;
        [SerializeField] private WeaponAnimationSet firstPersonAnimations;
        [SerializeField] private TpLocomotionSet thirdPersonLocomotion;
        [SerializeField] private RootMotionProfile thirdPersonRootMotionProfile;
        [SerializeField] private TpActionSet thirdPersonActions;
        [SerializeField, Min(0f)] private float drawTime = 0.6f;
        [SerializeField, Min(0f)] private float holsterTime = 0.5f;
        [Tooltip("音频配置（CP3）：族共享 Profile；空 = 无音频（CP6 前 WeaponAudioView 静默）")]
        [SerializeField] private WeaponAudioProfile audioProfile;

        public string WeaponId => weaponId;
        public string DisplayName => displayName;
        public WeaponFireMode FireMode => fireMode;
        public GameObject FirstPersonViewPrefab => firstPersonViewPrefab;
        public GameObject ThirdPersonViewPrefab => thirdPersonViewPrefab;
        public WeaponAnimationSet FirstPersonAnimations => firstPersonAnimations;
        public TpLocomotionSet ThirdPersonLocomotion => thirdPersonLocomotion;
        public RootMotionProfile ThirdPersonRootMotionProfile => thirdPersonRootMotionProfile;
        public TpActionSet ThirdPersonActions => thirdPersonActions;
        public float DrawTime => drawTime;
        public float HolsterTime => holsterTime;
        public WeaponAudioProfile AudioProfile => audioProfile;
    }
}
