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
        LegacyUnverified,
        DualLayerVerified
    }

    /// <summary>
    /// One authoritative row for every production LPW weapon. The editor pipeline consumes
    /// this asset to generate views/definitions and the runtime uses it for audit/debug data.
    /// </summary>
    [Serializable]
    public sealed class LPWWeaponSpec
    {
        public int schemaVersion = 6;
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
        [Tooltip("LegacyUnverified keeps the schema-5 compatibility path. DualLayerVerified uses explicit grip/sight anchors and an FP_Weapon_Root ADS pose.")]
        public LPWPoseCalibrationMode poseCalibrationMode;
        public bool hasGripCalibration;
        public Vector3 fpRootPosition;
        public Vector3 fpRootEuler = new(0f, 90f, 326.73f);
        [Tooltip("Schema-5 migration data only. DualLayerVerified weapons must not add this offset to LPW_Gun.")]
        public Vector3 fpAdsCenterOffset;
        public Vector3 fpRightHandGripPosition;
        public Vector3 fpRightHandGripEuler;
        public Vector3 fpTriggerPosition;
        public Vector3 fpTriggerEuler;
        public bool hasSightCalibration;
        public Vector3 fpRearSightPosition;
        public Vector3 fpRearSightEuler = new(0f, -90f, 0f);
        public Vector3 fpFrontSightPosition;
        public Vector3 fpFrontSightEuler = new(0f, -90f, 0f);
        public bool hasAdsCalibration;
        public Vector3 fpAdsViewmodelPosition;
        public Vector3 fpAdsViewmodelEuler;
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
        [SerializeField] private int schemaVersion = 6;
        [SerializeField] private List<LPWWeaponSpec> weapons = new();

        public int SchemaVersion => schemaVersion;
        public IReadOnlyList<LPWWeaponSpec> Weapons => weapons;
    }
}
