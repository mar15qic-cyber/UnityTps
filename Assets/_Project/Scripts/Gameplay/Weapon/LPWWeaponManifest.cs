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

    /// <summary>
    /// One authoritative row for every production LPW weapon. The editor pipeline consumes
    /// this asset to generate views/definitions and the runtime uses it for audit/debug data.
    /// </summary>
    [Serializable]
    public sealed class LPWWeaponSpec
    {
        public int schemaVersion = 5;
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
        public Vector3 fpRootPosition;
        public Vector3 fpRootEuler = new(0f, 90f, 326.73f);
        [Tooltip("Arena_LPWTest measured static offset that centers this weapon's sight axis at full ADS.")]
        public Vector3 fpAdsCenterOffset;
        public Vector3 fpRightHandGripPosition;
        public Vector3 fpSightReferencePosition;
        public Vector3 fpSightReferenceEuler = new(0f, -90f, 0f);
        public Vector3 tpRootPosition;
        public Vector3 tpRootEuler = new(0f, 90f, 326.73f);
        public bool supportsVerifiedAttachments;
    }

    [CreateAssetMenu(menuName = "UnityFps/Weapons/LPW Weapon Manifest", fileName = "LPWWeaponManifest")]
    public sealed class LPWWeaponManifest : ScriptableObject
    {
        [SerializeField] private int schemaVersion = 5;
        [SerializeField] private List<LPWWeaponSpec> weapons = new();

        public int SchemaVersion => schemaVersion;
        public IReadOnlyList<LPWWeaponSpec> Weapons => weapons;
    }
}
