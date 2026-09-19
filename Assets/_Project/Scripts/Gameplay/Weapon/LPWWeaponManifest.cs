using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    public enum WeaponCatalogCategory
    {
        Rifle,
        Pistol,
        Shotgun,
        Smg,
        Sniper
    }

    public enum WeaponSlotType
    {
        Primary,
        Secondary
    }

    public enum LPWPoseCalibrationMode
    {
        LegacyUnverified = 0,
        DualLayerVerified = 1,
        AnchoredDualPoseV2 = 2
    }

    public enum LPWSupportGripStyle
    {
        ForeEnd,
        VerticalGrip,
        MagazineWell,
        TwoHandPistol
    }

    public enum LPWAdsFirePresentationMode
    {
        LegacyAimFire = 0,
        ProceduralOnly = 1
    }

    public enum LPWSightCoordinateSpace
    {
        WeaponRootLocal = 0,
        SourceModelLocal = 1
    }

    /// <summary>
    /// LPFP reference set used to interpret contact-to-wrist offsets.  It lives
    /// in the gameplay data assembly so the production manifest can persist
    /// the same family data written into the presentation prefab.
    /// </summary>
    public enum FPAnimationReferenceFamily
    {
        Auto,
        Rifle01,
        Rifle02,
        Rifle03,
        Smg,
        Pistol,
        Shotgun,
        Sniper,
        Native
    }

    [Serializable]
    public struct LPWAnimationFamilyContactOffset
    {
        public FPAnimationReferenceFamily family;
        public bool configured;
        public Vector3 supportContactToWristLocalPosition;
        public Vector3 supportContactToWristLocalEulerAngles;
    }

    /// <summary>
    /// One authoritative row for every production LPW weapon. The editor pipeline consumes
    /// this asset to generate views/definitions and the runtime uses it for audit/debug data.
    /// </summary>
    [Serializable]
    public sealed class LPWWeaponSpec
    {
        public int schemaVersion = 7;
        public string itemId;
        public string definitionId;
        public string displayName;
        public string sourcePrefabPath;
        public string assetKey;
        public WeaponCatalogCategory category;
        public WeaponSlotType slotType;
        public WeaponFireMode fireMode;
        public FirstPersonAnimationFamily animationFamily;
        [Tooltip("True only when the real weapon action requires cycling a bolt after firing.")]
        public bool usesBoltAction;
        [Tooltip("Legacy LPFP definition that owns the matching authored arm/Aim animation set.")]
        public string animationDefinitionId;
        public string firstPersonTemplatePath;
        public string thirdPersonTemplatePath;
        public int tier;
        public long priceCoins;
        public int unlockLevel;
        public WeaponStat stat;
        [Tooltip("AnchoredDualPoseV2 uses an absolute idle gun pose plus an absolute ADS gun pose. Legacy values remain migration-only.")]
        public LPWPoseCalibrationMode poseCalibrationMode;
        [Tooltip("ADS fire presentation. V2 LPW weapons must use ProceduralOnly.")]
        public LPWAdsFirePresentationMode adsFirePresentationMode = LPWAdsFirePresentationMode.ProceduralOnly;
        public bool hasGripCalibration;
        public Vector3 fpRootPosition;
        public Vector3 fpRootEuler = new(0f, 90f, 326.73f);
        [Tooltip("True when the visible LPW rifle model has an explicit local transform calibration.")]
        public bool hasModelCalibration;
        public Vector3 fpModelPosition;
        public Vector3 fpModelEuler;
        public Vector3 fpModelScale = Vector3.one;
        [Tooltip("Schema-6 migration data only. V2 weapons never add this offset to LPW_Gun.")]
        public Vector3 fpAdsCenterOffset;
        public Vector3 fpRightHandGripPosition;
        public Vector3 fpRightHandGripEuler;
        public Vector3 fpLeftSupportGripPosition;
        public Vector3 fpLeftSupportGripEuler;
        public Vector3 fpTriggerPosition;
        public Vector3 fpTriggerEuler;
        [Tooltip("True when the replacement mesh exposes an authored magazine well/grab interface.")]
        public bool hasMagazineCalibration;
        public string magazinePartName;
        public Vector3 fpMagazineWellPosition;
        public Vector3 fpMagazineWellEuler;
        public Vector3 fpMagazineGripPosition;
        public Vector3 fpMagazineGripEuler;
        [Tooltip("True when this weapon uses a magazine-pivot guide before the final straight insertion.")]
        public bool hasMagazineInsertGuide;
        public Vector3 fpMagazineInsertGuidePosition;
        public Vector3 fpMagazineInsertGuideEuler;
        [Tooltip("AUG-style detached magazine motion: the magazine stays aligned to its well and the left hand follows it on a straight insertion axis.")]
        public bool lockMagazineToWellAxis;
        public Vector3 fpMagazineExtractedPosition;
        public Vector3 fpMagazineExtractedEuler;
        public Vector3 fpMagazineInsertionAxisLocal = Vector3.up;
        public float magazineExtractedDistance = .22f;
        public float magazineInsertStartDistance = .10f;
        public float magazineOutNormalized = 0.18f;
        public float magazineInNormalized = 0.65f;
        public float emptyMagazineOutNormalized = 0.12f;
        public float emptyMagazineInNormalized = 0.45f;
        public Vector3 magazineHeldLocalPosition;
        public Vector3 magazineHeldLocalEuler;
        [Tooltip("LPFP family reference offsets. Multiple entries allow one weapon to switch animation family when attachments change.")]
        public List<LPWAnimationFamilyContactOffset> fpAnimationFamilyContactOffsets = new();
        [Range(0f, .25f)] public float magazineReachWindow = .10f;
        [Range(0f, .25f)] public float magazineInsertWindow = .08f;
        [Range(0f, .25f)] public float magazineAlignWindow;
        [Tooltip("Camera-local elbow pole positions. These are authored per weapon and are not shared camera constants.")]
        public bool hasElbowPoleHints;
        public Vector3 fpLeftElbowPoleHintCameraLocal = new(-0.35f, -0.80f, 0.10f);
        public Vector3 fpRightElbowPoleHintCameraLocal = new(0.35f, -0.80f, 0.10f);
        public LPWSupportGripStyle supportGripStyle = LPWSupportGripStyle.ForeEnd;
        public bool hasSightCalibration;
        [Tooltip("Coordinate space of authored rear/front sight samples. Interface grips remain WeaponRoot local regardless of this flag.")]
        public LPWSightCoordinateSpace sightCoordinateSpace = LPWSightCoordinateSpace.WeaponRootLocal;
        public Vector3 fpRearSightPosition;
        public Vector3 fpRearSightEuler = new(0f, -90f, 0f);
        public Vector3 fpFrontSightPosition;
        public Vector3 fpFrontSightEuler = new(0f, -90f, 0f);
        public bool hasAdsCalibration;
        public Vector3 fpAdsViewmodelPosition;
        public Vector3 fpAdsViewmodelEuler;
        [Tooltip("Schema-7 absolute ADS gun pose, relative to the animated weapon bone. This is not an Idle increment.")]
        public Vector3 fpAdsGunPosition;
        public Vector3 fpAdsGunEuler;
        [Tooltip("Schema-5 compatibility alias. New calibration uses fpRearSightPosition.")]
        public Vector3 fpSightReferencePosition;
        public Vector3 fpSightReferenceEuler = new(0f, -90f, 0f);
        public Vector3 tpRootPosition;
        public Vector3 tpRootEuler = new(0f, 90f, 326.73f);
        public bool supportsVerifiedAttachments;
    }

    [CreateAssetMenu(menuName = "UnityFps/Weapons/LPW Weapon Manifest", fileName = "LPWWeaponManifest")]
    public sealed class LPWWeaponManifest : ScriptableObject
    {
        [SerializeField] private int schemaVersion = 7;
        [SerializeField] private List<LPWWeaponSpec> weapons = new();

        public int SchemaVersion => schemaVersion;
        public IReadOnlyList<LPWWeaponSpec> Weapons => weapons;
    }
}
