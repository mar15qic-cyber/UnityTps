using System;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// Applies the small, explicitly verified first-person attachment matrix.
    /// The weapon prefabs own the meshes; this component only switches those
    /// authored variants and never invents a generic attachment transform.
    /// </summary>
    public sealed class FPWeaponAttachmentView : MonoBehaviour
    {
        private const string PrefKeyPrefix = "Game.Loadout.Attachments.";

        private Transform[] _children;
        private string _opticId;
        private string _muzzleId;
        private string _magazineId;

        public string OpticId => _opticId;
        public string MuzzleId => _muzzleId;
        public string MagazineId => _magazineId;

        private void Awake()
        {
            CacheChildren();
        }

        public void Apply(string opticId, string muzzleId, string magazineId)
        {
            if (_children == null) CacheChildren();

            _opticId = opticId ?? string.Empty;
            _muzzleId = muzzleId ?? string.Empty;
            _magazineId = magazineId ?? string.Empty;

            SetActive("scope_01", _opticId == "attachment.optic.rifle.01");
            SetActive("scope_02", _opticId == "attachment.optic.pistol.01");
            SetActive("scope_03", false);
            SetActive("scope_04", false);
            SetActive("silencer", _muzzleId == "attachment.muzzle.rifle.01" || _muzzleId == "attachment.muzzle.pistol.01");

            // The verified magazine slot is the authored `mag` mesh already
            // present in both FP prefabs. Keep it visible for the base weapon
            // and for the owned magazine item; no unverified replacement mesh
            // is created here.
            var magazine = FindChild("mag");
            if (magazine != null) magazine.gameObject.SetActive(true);
        }

        public static string ResolveStableWeaponId(string definitionId)
        {
            return definitionId switch
            {
                "rifle.day3" => "weapon.m4",
                "pistol.day2" => "weapon.service_pistol",
                "rifle.02" => "weapon.ak",
                _ => string.Empty
            };
        }

        public static void SavePersisted(string weaponId, string opticId, string muzzleId, string magazineId)
        {
            if (string.IsNullOrWhiteSpace(weaponId)) return;
            PlayerPrefs.SetString(PrefKeyPrefix + weaponId + ".Optic", opticId ?? string.Empty);
            PlayerPrefs.SetString(PrefKeyPrefix + weaponId + ".Muzzle", muzzleId ?? string.Empty);
            PlayerPrefs.SetString(PrefKeyPrefix + weaponId + ".Magazine", magazineId ?? string.Empty);
            PlayerPrefs.Save();
        }

        public static void LoadPersisted(string weaponId, out string opticId, out string muzzleId, out string magazineId)
        {
            opticId = string.IsNullOrWhiteSpace(weaponId) ? string.Empty : PlayerPrefs.GetString(PrefKeyPrefix + weaponId + ".Optic", string.Empty);
            muzzleId = string.IsNullOrWhiteSpace(weaponId) ? string.Empty : PlayerPrefs.GetString(PrefKeyPrefix + weaponId + ".Muzzle", string.Empty);
            magazineId = string.IsNullOrWhiteSpace(weaponId) ? string.Empty : PlayerPrefs.GetString(PrefKeyPrefix + weaponId + ".Magazine", string.Empty);
        }

        private void CacheChildren()
        {
            _children = GetComponentsInChildren<Transform>(true);
        }

        private Transform FindChild(string childName)
        {
            if (_children == null) CacheChildren();
            foreach (var child in _children)
                if (child != null && string.Equals(child.name, childName, StringComparison.Ordinal)) return child;
            return null;
        }

        private void SetActive(string childName, bool active)
        {
            var child = FindChild(childName);
            if (child != null) child.gameObject.SetActive(active);
        }
    }
}

