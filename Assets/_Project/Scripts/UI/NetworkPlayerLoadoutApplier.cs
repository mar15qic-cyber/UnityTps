using System.Collections.Generic;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 网络玩家权威配装装配桥（2026-09-08 追加 P0 §6 二.3）：
    /// itemId→WeaponDefinition 映射所需的 WeaponAssetCatalog 属 Game.UI（asmdef 边界：
    /// Game.Gameplay 不引用 Game.UI）——Gameplay 侧 NetworkLoadoutPolicy 经反射按名调用本类
    ///（同 PlayerNetworkAdapter.WireRemotePresentation 的反向依赖规避惯例）。
    /// LPFP 主线白名单纪律与 GameplayLoadoutBootstrap 完全一致（TryResolveLpfpDefinition 同语义）。
    /// </summary>
    public static class NetworkPlayerLoadoutApplier
    {
        /// <summary>
        /// 服务器侧权威两槽配置（DS 在网络玩家生成回调调用；早于 Arsenal.Start 首装）。
        /// 返回 null = 成功；非空 = fail closed 错误（拒绝生成并断开）。
        /// </summary>
        public static string ApplyServerSlots(Arsenal arsenal, TicketLoadoutSnapshot loadout)
        {
            if (loadout == null)
                return "authority-loadout-missing（consume 快照未携带配装——旧版后端或配装行缺失）";
            var catalog = WeaponAssetCatalog.LoadOrDefault();
            if (!TryResolveLpfp(catalog, loadout.primaryWeaponId, "主武器", out var primary, out var error)) return error;
            if (!TryResolveLpfp(catalog, loadout.secondaryWeaponId, "副武器", out var secondary, out error)) return error;
            arsenal.ConfigureSlots(new List<WeaponDefinition> { primary, secondary }, initialIndex: 0);
            return null;
        }

        /// <summary>
        /// 客户端侧两槽复刻（Owner/Observer 生成回调调用）：按服务器同步的权威 weaponId
        /// 把本地副本 Arsenal 配置为同两槽——Owner 只显示/切换这两槽，远端 ApplyWeapon 可命中。
        /// currentWeaponId=服务器当前武器（definitionId）用于初始槽位对齐（重连瞬间持副则初始装副）。
        /// 返回 null = 成功；非空 = 解析错误（调用方告警并保留本地槽位——服务器仍是权威闸）。
        /// </summary>
        public static string ApplyClientSlots(Arsenal arsenal, string primaryItemId, string secondaryItemId, string currentWeaponId)
        {
            if (arsenal == null) return "arsenal-missing";
            var catalog = WeaponAssetCatalog.LoadOrDefault();
            if (!TryResolveLpfp(catalog, primaryItemId, "主武器", out var primary, out var error)) return error;
            if (!TryResolveLpfp(catalog, secondaryItemId, "副武器", out var secondary, out error)) return error;
            int initialIndex = !string.IsNullOrEmpty(currentWeaponId) && secondary.WeaponId == currentWeaponId ? 1 : 0;
            arsenal.ConfigureSlots(new List<WeaponDefinition> { primary, secondary }, initialIndex);
            return null;
        }

        private static bool TryResolveLpfp(WeaponAssetCatalog catalog, string itemId, string slotLabel,
            out WeaponDefinition definition, out string error)
        {
            definition = null;
            error = null;
            if (string.IsNullOrWhiteSpace(itemId))
            {
                error = $"{slotLabel}槽位为空（配装非法）";
                return false;
            }
            if (catalog == null)
            {
                error = "weapon-catalog-missing（WeaponAssetCatalog 不可用）";
                return false;
            }
            if (!catalog.TryGet(itemId, out var entry) || entry == null || !entry.IsLpfp)
            {
                error = $"{slotLabel}仅支持 LPFP 资源，拒绝条目：{itemId}";
                return false;
            }
            if (!catalog.TryResolveDefinition(itemId, out definition) || definition == null)
            {
                error = $"{slotLabel} LPFP 资源映射缺失：{itemId}";
                return false;
            }
            return true;
        }
    }
}
