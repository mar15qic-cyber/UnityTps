using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 权威附件网络快照编解码（Gate A-2，2026-09-08 P0 追加复审 §1.2）：
    /// 服务器把票据配装快照（TicketLoadoutSnapshot.attachments）按武器槽位压缩成分号分隔的
    /// 附件 itemId 串，经 NetworkWeaponState 的 SyncVar 广播；客户端只把该快照用于对应玩家的
    /// FP/TP 表现装配（FPWeaponRig / TPWeaponMeshSwapper）——远端玩家不得再以本机
    /// WeaponAttachmentStore（PlayerPrefs）作为其他玩家的附件真相，Owner 表现也保证来自本次
    /// 服务器快照。服务器仍是数值权威（WeaponController.SetAttachments 服务器独立执行）。
    /// 编码格式：分号分隔的附件 itemId（目录键不含分号）；"-"（AuthoritativeEmpty）= 权威
    /// 快照在位且该槽无任何附件；"" = 快照不在位（调试 Host 十槽 / 离线）→ 客户端回退本机
    /// 存储（离线单人行为不变）。纯函数编解码 + 目录解析（缺失条目跳过=安全降级，不 fail
    /// closed），EditMode 可测。
    /// </summary>
    public static class AttachmentSnapshotCodec
    {
        /// <summary>权威空装配哨兵：与「快照不在位」（空串）严格区分。</summary>
        public const string AuthoritativeEmpty = "-";

        /// <summary>编码附件 itemId 列表（空白项忽略；全空 = 权威空装配哨兵）。</summary>
        public static string Encode(IReadOnlyList<string> itemIds)
        {
            if (itemIds == null || itemIds.Count == 0) return AuthoritativeEmpty;
            var ids = new List<string>(itemIds.Count);
            foreach (var id in itemIds)
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            return ids.Count == 0 ? AuthoritativeEmpty : string.Join(";", ids);
        }

        /// <summary>按后端武器槽位名（"Primary"/"Secondary"，忽略大小写）过滤票据快照并编码。</summary>
        public static string EncodeForSlot(TicketLoadoutSnapshot loadout, string weaponSlot)
        {
            if (loadout?.attachments == null || string.IsNullOrEmpty(weaponSlot)) return AuthoritativeEmpty;
            var ids = new List<string>();
            foreach (var attachment in loadout.attachments)
            {
                if (attachment == null || string.IsNullOrWhiteSpace(attachment.attachmentItemId)) continue;
                if (!string.Equals(attachment.weaponSlot, weaponSlot, StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(attachment.attachmentItemId.Trim());
            }
            return Encode(ids);
        }

        /// <summary>
        /// 解码：返回 false = 快照不在位（空串 → 调用方回退本机存储）；
        /// true + 空表 = 权威空装配（哨兵）；其余 = 分号拆分（空白 token 忽略）。
        /// </summary>
        public static bool TryDecode(string encoded, out List<string> itemIds)
        {
            itemIds = new List<string>();
            if (string.IsNullOrEmpty(encoded)) return false;
            if (encoded == AuthoritativeEmpty) return true;
            foreach (var token in encoded.Split(';'))
                if (!string.IsNullOrWhiteSpace(token)) itemIds.Add(token.Trim());
            return true;
        }

        /// <summary>
        /// 解码 + 附件目录解析（缺失目录条目跳过并告警 = 安全降级，绝不 fail closed）。
        /// 返回 false = 快照不在位（调用方回退本机存储）；true = 快照在位（entries 可为空 =
        /// 权威空装配）。catalog 为空时用 AttachmentAssetCatalog.LoadOrDefault()。
        /// </summary>
        public static bool TryResolveEntries(string encoded, List<AttachmentAssetEntry> entries,
            AttachmentAssetCatalog catalog = null, UnityEngine.Object logContext = null)
        {
            if (entries == null) return false;
            entries.Clear();
            if (!TryDecode(encoded, out var itemIds)) return false;
            catalog ??= AttachmentAssetCatalog.LoadOrDefault();
            foreach (var itemId in itemIds)
            {
                if (catalog == null || !catalog.TryGet(itemId, out var entry) || entry == null)
                {
                    Debug.LogWarning($"[AttachmentSnapshot] 附件目录缺失已跳过 item={itemId}", logContext);
                    continue;
                }
                entries.Add(entry);
            }
            return true;
        }
    }
}
