using Game.Core;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.UI
{
    /// <summary>Build-safe roots for diagnostics/tests; the catalog rows themselves retain direct references.</summary>
    public sealed class LPWProductionRuntimeRegistry : ScriptableObject
    {
        [SerializeField] private WeaponAssetCatalog weaponAssets;
        [SerializeField] private DemoBalanceConfig balance;
        [SerializeField] private LPWWeaponManifest manifest;

        public WeaponAssetCatalog WeaponAssets => weaponAssets;
        public DemoBalanceConfig Balance => balance;
        public LPWWeaponManifest Manifest => manifest;
    }
}
