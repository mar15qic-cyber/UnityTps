using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 服务器权威配装解析（2026-09-08 追加 P0 §6 二.3，审计 §5.1 缺口 2）：
    /// Dedicated Server 在网络玩家首次装备/Arsenal 初始采样【之前】，按后端 ticket consume
    /// 权威快照把 Arsenal 严格配置为账号实际两槽 [primary, secondary]。映射失败 fail closed
    /// （拒绝生成并断开，给明确错误），绝不回退 prefab 十槽调试 Arsenal（十槽仅保留给
    /// 离线直开/F1 调试 Host 等无后端身份路径）。
    /// 程序集边界（Game.Gameplay 不引用 Game.UI/Game.Account）：itemId→WeaponDefinition 映射
    /// 所需的 WeaponAssetCatalog 属 Game.UI——经反射按名调用 Game.UI.NetworkPlayerLoadoutApplier
    ///（同 PlayerNetworkAdapter.WireRemotePresentation 的反向依赖规避惯例）；附件目录
    /// AttachmentAssetCatalog 属 Game.Gameplay.Weapon——直接解析。网络接线在
    /// PlayerNetworkAdapter.OnStartServer（生成回调早于 Arsenal.Start 首装）。
    /// </summary>
    public static class NetworkLoadoutPolicy
    {
        private const string ApplierTypeName = "Game.UI.NetworkPlayerLoadoutApplier, Game.UI";
        private static MethodInfo _applyServerSlots;
        private static MethodInfo _applyClientSlots;
        private static bool _applierResolved;

        /// <summary>
        /// 服务器侧权威两槽配置（DS 在玩家生成回调调用；早于 Arsenal.Start 首装）。
        /// 返回 null = 成功；非空 = fail closed 错误（拒绝生成并断开，日志不含密钥/票据）。
        /// </summary>
        public static string TryApplyServerSlots(Arsenal arsenal, TicketLoadoutSnapshot loadout)
        {
            if (arsenal == null) return "arsenal-missing（网络玩家缺 Arsenal 组件）";
            var method = ResolveApplierMethod("ApplyServerSlots", ref _applyServerSlots);
            if (method == null)
                return "catalog-bridge-missing（Game.UI.NetworkPlayerLoadoutApplier.ApplyServerSlots 不可用——武器目录解析链路断裂）";
            try
            {
                return method.Invoke(null, new object[] { arsenal, loadout }) as string;
            }
            catch (Exception exception)
            {
                return "catalog-bridge-fault：" + exception.InnerException?.Message ?? exception.Message;
            }
        }

        /// <summary>
        /// 客户端侧两槽复刻（Owner/Observer 在网络玩家生成回调调用）：把本地副本的 Arsenal
        /// 配置为服务器同步的权威两槽——Owner 只显示/切换这两槽（数字键/滚轮/Q 越界自然无效），
        /// 远端 ApplyWeapon 可按 weaponId 命中槽位。currentWeaponId=服务器当前武器（definitionId），
        /// 用于初始槽位对齐（重连瞬间服务器若持副武器则本地初始装副）。客户端解析失败只告警不踢
        ///（服务器仍是权威闸）。
        /// </summary>
        public static bool TryApplyClientSlots(Arsenal arsenal, string primaryItemId, string secondaryItemId, string currentWeaponId)
        {
            if (arsenal == null) return false;
            var method = ResolveApplierMethod("ApplyClientSlots", ref _applyClientSlots);
            if (method == null)
            {
                Debug.LogWarning("[NetworkLoadoutPolicy] 客户端槽位解析桥不可用——保留本地槽位（服务器仍权威验证）");
                return false;
            }
            try
            {
                var error = method.Invoke(null, new object[] { arsenal, primaryItemId, secondaryItemId, currentWeaponId }) as string;
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning($"[NetworkLoadoutPolicy] 客户端权威两槽解析失败（保留本地槽位，服务器仍权威验证）：{error}");
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[NetworkLoadoutPolicy] 客户端槽位解析异常：" + (exception.InnerException?.Message ?? exception.Message));
                return false;
            }
        }

        private static MethodInfo ResolveApplierMethod(string methodName, ref MethodInfo cached)
        {
            if (cached != null) return cached;
            if (!_applierResolved)
            {
                _applierResolved = true;
                var type = Type.GetType(ApplierTypeName);
                if (type == null)
                    Debug.LogWarning($"[NetworkLoadoutPolicy] 找不到 {ApplierTypeName}——武器目录桥未装配");
            }
            if (_applyServerSlots == null || _applyClientSlots == null)
            {
                var applierType = Type.GetType(ApplierTypeName);
                _applyServerSlots ??= applierType?.GetMethod("ApplyServerSlots", BindingFlags.Public | BindingFlags.Static);
                _applyClientSlots ??= applierType?.GetMethod("ApplyClientSlots", BindingFlags.Public | BindingFlags.Static);
            }
            cached ??= methodName == "ApplyServerSlots" ? _applyServerSlots : _applyClientSlots;
            return cached;
        }

        /// <summary>服务器侧权威附件解析（§6 二.4：六处一致——服务器 WeaponController 属性随配件重建）：
        /// 按武器槽位（"Primary"/"Secondary"，忽略大小写）过滤快照附件并解析目录条目。
        /// 查不到的附件条目跳过并告警（不 fail closed——配件缺失不应踢出合法玩家），其余正常应用。</summary>
        public static List<AttachmentAssetEntry> ResolveAttachmentEntries(TicketLoadoutSnapshot loadout, int slotIndex,
            UnityEngine.Object logContext = null)
        {
            var entries = new List<AttachmentAssetEntry>();
            if (loadout?.attachments == null) return entries;
            string weaponSlot = SlotName(slotIndex);
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            if (catalog == null) return entries;
            foreach (var attachment in loadout.attachments)
            {
                if (attachment == null || string.IsNullOrEmpty(attachment.attachmentItemId)) continue;
                if (!string.Equals(attachment.weaponSlot, weaponSlot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!catalog.TryGet(attachment.attachmentItemId, out var entry) || entry == null)
                {
                    Debug.LogWarning($"[NetworkLoadoutPolicy] 配件条目缺失已跳过 slot={weaponSlot} item={attachment.attachmentItemId}", logContext);
                    continue;
                }
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>本地槽位索引 ↔ 后端武器槽位名（"Primary"/"Secondary"；越界/空一律 Primary）。</summary>
        public static string SlotName(int slotIndex) => slotIndex == 1 ? "Secondary" : "Primary";
    }
}
