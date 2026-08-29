using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.UI
{
    [Serializable]
    public sealed class WeaponUiStats
    {
        public float damage;
        public float roundsPerMinute;
        public float magazineSize;
        public float recoil;

        public WeaponUiStats(float damage, float roundsPerMinute, float magazineSize, float recoil)
        {
            this.damage = damage;
            this.roundsPerMinute = roundsPerMinute;
            this.magazineSize = magazineSize;
            this.recoil = recoil;
        }
    }

    [Serializable]
    public sealed class WeaponAssetEntry
    {
        public string itemId;
        public string assetKey;
        public string definitionId;
        public string previewPrefabPath;
        public bool supportsVerifiedAttachments;
        public WeaponCatalogCategory category;
        public WeaponSlotType slotType;
        public WeaponDefinition definition;
        public GameObject previewPrefab;
        public WeaponUiStats stats;
    }

    /// <summary>Stable server ItemId to Unity definition/prefab mapping. Display names are never used as keys.</summary>
    public sealed class WeaponAssetCatalog : ScriptableObject
    {
        [SerializeField] private List<WeaponAssetEntry> entries = new();
        private Dictionary<string, WeaponAssetEntry> byId;

        public IReadOnlyList<WeaponAssetEntry> Entries => entries;

        public bool TryGet(string itemId, out WeaponAssetEntry entry)
        {
            EnsureDefaults();
            return byId.TryGetValue(itemId, out entry);
        }

        public WeaponDefinition FindDefinition(string itemId)
        {
            if (!TryGet(itemId, out var entry)) return null;
            if (entry.definition != null) return entry.definition;
#if UNITY_EDITOR
            foreach (var definition in Resources.FindObjectsOfTypeAll<WeaponDefinition>())
                if (definition != null && string.Equals(definition.WeaponId, entry.definitionId, StringComparison.Ordinal)) return definition;
#endif
            return null;
        }

        public bool TryResolveDefinition(string itemId, out WeaponDefinition definition)
        {
            definition = FindDefinition(itemId);
            return definition != null;
        }

        public GameObject FindPreviewPrefab(string itemId)
        {
            if (!TryGet(itemId, out var entry)) return null;
            if (entry.previewPrefab != null) return entry.previewPrefab;
#if UNITY_EDITOR
            if (!string.IsNullOrWhiteSpace(entry.previewPrefabPath))
                return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(entry.previewPrefabPath);
#else
            if (!string.IsNullOrWhiteSpace(entry.previewPrefabPath))
                return Resources.Load<GameObject>(entry.previewPrefabPath);
#endif
            return null;
        }

        public WeaponUiStats FindStats(string itemId)
        {
            return TryGet(itemId, out var entry) && entry.stats != null
                ? entry.stats
                : new WeaponUiStats(0f, 0f, 0f, 0f);
        }

        public static WeaponAssetCatalog CreateRuntime()
        {
            var catalog = CreateInstance<WeaponAssetCatalog>();
            catalog.EnsureDefaults();
            return catalog;
        }

        private void EnsureDefaults()
        {
            if (byId != null) return;
            if (entries == null) entries = new List<WeaponAssetEntry>();
            if (entries.Count == 0)
            {
                Add("weapon.m4", "rifle.day3/Assault_Rifle_01", "rifle.day3", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab", true, 26f, 600f, 30f, 1.35f);
                Add("weapon.ak", "rifle.02/Assault_Rifle_02", "rifle.02", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_02.prefab", false, 24f, 640f, 30f, 0.85f);
                Add("weapon.service_pistol", "pistol.day2/Pistol_01", "pistol.day2", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab", true, 34f, 360f, 12f, 0.8f);
                Add("weapon.rifle03", "rifle.03/Assault_Rifle_03", "rifle.03", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab", false, 28f, 520f, 25f, 1.2f);
                Add("weapon.smg01", "smg.01/SMG_01", "smg.01", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_01.prefab", false, 18f, 850f, 32f, 0.7f);
                Add("weapon.smg02", "smg.02/SMG_02", "smg.02", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_02.prefab", false, 20f, 780f, 30f, 0.65f);
                Add("weapon.shotgun01", "shotgun.01/Shotgun_01", "shotgun.01", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Shotgun_01.prefab", false, 60f, 90f, 6f, 2.6f);
                Add("weapon.sniper01", "sniper.01/Sniper_Rifle_01", "sniper.01", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab", false, 95f, 45f, 5f, 2.8f);
                Add("weapon.sniper02", "sniper.02/Sniper_Rifle_02", "sniper.02", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_02.prefab", false, 110f, 35f, 5f, 3f);
                Add("weapon.handgun02", "handgun.02/Pistol_02", "handgun.02", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_02.prefab", false, 30f, 300f, 15f, 0.9f);
            }
            byId = new Dictionary<string, WeaponAssetEntry>(StringComparer.Ordinal);
            foreach (var entry in entries) if (entry != null && !string.IsNullOrWhiteSpace(entry.itemId)) byId[entry.itemId] = entry;
        }

        private void Add(string itemId, string key, string definition, string prefab, bool verified, float damage, float rpm, float magazine, float recoil) => entries.Add(new WeaponAssetEntry
        {
            itemId = itemId, assetKey = key, definitionId = definition, previewPrefabPath = prefab, supportsVerifiedAttachments = verified,
            stats = new WeaponUiStats(damage, rpm, magazine, recoil)
        });
    }
}
