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

    /// <summary>
    /// First-person rifle animation family. This is deliberately separate from
    /// the weapon's gameplay type: SMG/pistol definitions continue to use their
    /// authored native animation set and never enter the rifle-family rule.
    /// </summary>
    public enum FirstPersonAnimationFamily
    {
        Native = 0,
        Rifle01 = 1,
        Rifle02 = 2,
        Rifle03 = 3
    }

    /// <summary>FP 手臂动画集（clip 挂在各自 rig 的 view prefab 上，由 FPWeaponAnimator 直接播放）。
    /// ADS 四件套（Docs/18 §4.1）：未配置的武器由 FPWeaponMotion 程序化 ADS 兜底（零回归约束）。</summary>
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

        [Tooltip("开镜过渡（一次性）；与 AimOut 双全才启用动画 ADS 轨道")]
        public AnimationClip AimIn;
        [Tooltip("收镜过渡（一次性）")]
        public AnimationClip AimOut;
        [Tooltip("ADS 开火（一次性）；空则回退 AimIn 末帧姿态")]
        public AnimationClip AimFire;
        [Tooltip("ADS 保持姿势（定格）；空则回退 AimIn 末帧定格")]
        public AnimationClip AimIdle;

        /// <summary>是否具备动画 ADS 轨道：AimIn/AimOut 双全才启用（缺一会让收镜卡在贴腮态）。</summary>
        public bool HasAimClips => AimIn != null && AimOut != null;
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
        [Header("First-person animation family")]
        [Tooltip("Explicit animation family. Native preserves the authored firstPersonAnimations set.")]
        [SerializeField] private FirstPersonAnimationFamily firstPersonAnimationFamily = FirstPersonAnimationFamily.Native;
        [SerializeField, HideInInspector] private bool rifleHasVerticalGrip;
        [SerializeField, HideInInspector] private FirstPersonAnimationFamily rifleAnimationFamily = FirstPersonAnimationFamily.Rifle01;
        [Tooltip("Optional Low Poly FPS Pack rifle01 set. Empty entries fall back to firstPersonAnimations.")]
        [SerializeField] private WeaponAnimationSet rifle01Animations;
        [Tooltip("Optional Low Poly FPS Pack rifle02/M4 set. Empty entries fall back to firstPersonAnimations.")]
        [SerializeField] private WeaponAnimationSet rifle02Animations;
        [Tooltip("Optional Low Poly FPS Pack rifle03 set. Empty entries fall back to firstPersonAnimations.")]
        [SerializeField] private WeaponAnimationSet rifle03Animations;
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
        public bool RifleHasVerticalGrip => firstPersonAnimationFamily == FirstPersonAnimationFamily.Rifle02
            || firstPersonAnimationFamily == FirstPersonAnimationFamily.Rifle03;
        public FirstPersonAnimationFamily FirstPersonAnimationFamily => ResolveFirstPersonAnimationFamily();
        public WeaponAnimationSet FirstPersonAnimations => ResolveFirstPersonAnimations();
        public TpLocomotionSet ThirdPersonLocomotion => thirdPersonLocomotion;
        public RootMotionProfile ThirdPersonRootMotionProfile => thirdPersonRootMotionProfile;
        public TpActionSet ThirdPersonActions => thirdPersonActions;
        public float DrawTime => drawTime;
        public float HolsterTime => holsterTime;
        public WeaponAudioProfile AudioProfile => audioProfile;

        /// <summary>
        /// Resolves only the rifle family. Non-rifles retain their existing
        /// native FP set even if old assets have no family metadata yet.
        /// </summary>
        public FirstPersonAnimationFamily ResolveFirstPersonAnimationFamily()
        {
            if (firstPersonAnimationFamily != FirstPersonAnimationFamily.Native)
                return firstPersonAnimationFamily;

            // Migration fallback for existing rifle assets authored before the explicit field.
            if (!IsLegacyRifleDefinition()) return FirstPersonAnimationFamily.Native;
            if (!rifleHasVerticalGrip) return FirstPersonAnimationFamily.Rifle01;
            return rifleAnimationFamily == FirstPersonAnimationFamily.Rifle03
                ? FirstPersonAnimationFamily.Rifle03
                : FirstPersonAnimationFamily.Rifle02;
        }

        private WeaponAnimationSet ResolveFirstPersonAnimations()
        {
            var family = ResolveFirstPersonAnimationFamily();
            if (family == FirstPersonAnimationFamily.Native) return firstPersonAnimations;

            switch (family)
            {
                case FirstPersonAnimationFamily.Rifle02:
                    if (HasAnyClip(rifle02Animations)) return rifle02Animations;
                    break;
                case FirstPersonAnimationFamily.Rifle03:
                    if (HasAnyClip(rifle03Animations)) return rifle03Animations;
                    break;
                case FirstPersonAnimationFamily.Rifle01:
                    if (HasAnyClip(rifle01Animations)) return rifle01Animations;
                    break;
            }

            return firstPersonAnimations;
        }

        private static bool HasAnyClip(WeaponAnimationSet set)
            => set.Idle != null || set.Fire != null || set.ReloadAmmoLeft != null
                || set.ReloadOutOfAmmo != null || set.Draw != null || set.Holster != null
                || set.DryFire != null;

        private bool IsLegacyRifleDefinition()
            => !string.IsNullOrEmpty(weaponId)
                && weaponId.IndexOf("rifle", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
