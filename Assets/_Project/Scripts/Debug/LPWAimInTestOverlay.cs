using System.Collections.Generic;
using Game.Gameplay.Weapon;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Debugging
{
    /// <summary>
    /// Arena_LPWTest 专用的 29 枪切换辅助。ADS 校准文字与对照十字已移除，
    /// 避免测试画面被调试显示遮挡。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LPWAimInTestOverlay : MonoBehaviour
    {
        [Header("仅用于 Arena_LPWTest 切换 29 把武器")]
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private LPWWeaponManifest productionManifest;
        [SerializeField] private WeaponAssetCatalog weaponAssets;

        private readonly List<WeaponDefinition> productionWeapons = new();

        private void Awake()
        {
            arsenal ??= GetComponent<Arsenal>();
            ConfigureProductionWeapons();
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || arsenal == null || productionWeapons.Count == 0) return;
            if (keyboard.pageDownKey.wasPressedThisFrame || keyboard.rightBracketKey.wasPressedThisFrame)
                SelectRelative(1);
            else if (keyboard.pageUpKey.wasPressedThisFrame || keyboard.leftBracketKey.wasPressedThisFrame)
                SelectRelative(-1);
        }

        private void ConfigureProductionWeapons()
        {
            productionWeapons.Clear();
            if (arsenal == null || productionManifest == null || weaponAssets == null) return;
            foreach (LPWWeaponSpec spec in productionManifest.Weapons)
                if (spec != null && weaponAssets.TryResolveDefinition(spec.itemId, out WeaponDefinition definition))
                    productionWeapons.Add(definition);
            if (productionWeapons.Count == 29)
                arsenal.ConfigureSlots(productionWeapons, 0);
            else
                Debug.LogError($"[LPWAimInTestOverlay] 期望 29 把 LPW，实际解析 {productionWeapons.Count} 把。", this);
        }

        private void SelectRelative(int direction)
        {
            int current = Mathf.Clamp(arsenal.ActiveIndex, 0, productionWeapons.Count - 1);
            int next = (current + direction + productionWeapons.Count) % productionWeapons.Count;
            arsenal.TrySelectSlot(next);
        }

    }
}
