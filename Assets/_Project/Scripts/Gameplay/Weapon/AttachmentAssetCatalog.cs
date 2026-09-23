using System;
using System.Collections.Generic;
using System.Text;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>配件槽位（与后端 CatalogItem.SlotType 字符串对应）.</summary>
    public enum AttachmentSlotType
    {
        Optic,       // 瞄具（顶部导轨）
        Muzzle,      // 枪口（螺纹）
        Magazine,    // 弹匣（纯数值，无模型——用户规则：无美术素材仅改弹容量）
        Tactical,    // 战术（激光/手电，护木下挂或枪管下导轨）
        Underbarrel, // 下挂（前握把，护木下方）
        Stock        // 枪托（暂无素材）
    }

    /// <summary>瞄具开镜分档（Docs/19 F4 三层镜内视野；Docs/21 瞄具审计定档）.</summary>
    public enum OpticAimTier
    {
        None,     // 非瞄具
        RedDot,   // 红点/开放式反射（L1：准星 overlay，无放大）
        Holo,     // 全息（L1：准星 overlay，无放大）
        LowZoom,  // 低倍 2-4x（L2：AdsFov 分档放大）
        HighZoom  // 高倍狙击 6x+（L3：全屏 overlay）
    }

    /// <summary>
    /// 当前瞄具的开镜情境（PlayerAimState/FPCameraRig 只读）：分档 + FOV 覆盖。
    /// 无瞄具或瞄具无分档 = None。FOV 覆盖仅 LowZoom/HighZoom 有意义，红点/全息沿用武器默认。
    /// </summary>
    public readonly struct OpticAimContext
    {
        public static readonly OpticAimContext None = new OpticAimContext(OpticAimTier.None, 0f);

        public readonly OpticAimTier Tier;
        /// <summary>瞄具 FOV 覆盖（度；&lt;=1 = 不覆盖，回退武器 Stat.AdsFov）。</summary>
        public readonly float AdsFovOverride;
        /// <summary>瞄具 itemId（眼光轴校准反查键；None 时为 null）。</summary>
        public readonly string ItemId;
        /// <summary>固定放大率（P4 实体镜 I4b）：&gt;1 = 镜内 RT 承担放大（独立倍率），1x/未配置 = 0。</summary>
        public readonly float Magnification;

        public OpticAimContext(OpticAimTier tier, float adsFovOverride, string itemId = null, float magnification = 0f)
        {
            Tier = tier;
            AdsFovOverride = adsFovOverride;
            ItemId = itemId;
            Magnification = magnification;
        }

        /// <summary>有效 ADS FOV：瞄具覆盖优先，否则武器默认。</summary>
        public float EffectiveAdsFov(float weaponAdsFov)
            => AdsFovOverride > 1f ? AdsFovOverride : weaponAdsFov;

        /// <summary>是否为实体镜（P4 I4b）：有固定放大率 &gt;1 的变焦瞄具，镜内视野由 RT 相机渲染。</summary>
        public bool IsPhysicalScope => Magnification > 1.01f;

        /// <summary>从装配集合解析瞄具情境（第一个带分档的 Optic 槽条目；无则 None）。纯函数，EditMode 可测。</summary>
        public static OpticAimContext FromEquipped(IReadOnlyList<AttachmentAssetEntry> equipped)
        {
            if (equipped == null) return None;
            for (int i = 0; i < equipped.Count; i++)
            {
                var e = equipped[i];
                if (e != null && e.slot == AttachmentSlotType.Optic && e.aimTier != OpticAimTier.None)
                    return new OpticAimContext(e.aimTier, e.adsFovOverride, e.itemId, e.magnification);
            }
            return None;
        }

        /// <summary>先取已装瞄具；没有时回退武器自带瞄具。纯函数，供运行时和测试共用。</summary>
        public static OpticAimContext Resolve(IReadOnlyList<AttachmentAssetEntry> equipped,
            BuiltInOpticDefinition builtIn)
        {
            var equippedContext = FromEquipped(equipped);
            if (equippedContext.Tier != OpticAimTier.None) return equippedContext;
            return builtIn.IsValid
                ? new OpticAimContext(builtIn.aimTier, builtIn.adsFovOverride, builtIn.opticId, builtIn.magnification)
                : None;
        }
    }

    /// <summary>单条数值修饰（可序列化，编辑器内配置）.</summary>
    [Serializable]
    public sealed class AttachmentModifierEntry
    {
        public WeaponStatId stat;
        public ModifierOperation op = ModifierOperation.Multiply;
        public float value = 1f;
    }

    /// <summary>配件资产条目：itemId（后端目录键）→ 模型 prefab + 数值修饰 + 瞄具分档.</summary>
    [Serializable]
    public sealed class AttachmentAssetEntry
    {
        public string itemId;
        public string displayName;
        [TextArea] public string description;
        public AttachmentSlotType slot = AttachmentSlotType.Optic;
        public OpticAimTier aimTier = OpticAimTier.None;
        [Tooltip("消音器：装配后开火音切换为 AudioProfile 的消音变体池（WeaponAudioView 消费）")]
        public bool isSuppressor;
        [Tooltip("开镜 FOV 覆盖（度，>1 生效）：低倍/高倍镜放大档位；0=沿用武器 Stat.AdsFov")]
        public float adsFovOverride;
        [Tooltip("固定放大率（P4 实体镜 I4b，>1 生效）：镜内 RT 相机独立倍率；0/1 = 走既有 overlay 路径")]
        public float magnification;
        [Tooltip("实体镜有效孔径中心，瞄具 prefab 局部坐标；光轴沿 prefab +Z。与安装位姿无关。")]
        public Vector3 scopeApertureCenter;
        [Tooltip("实体镜有效圆孔半径（prefab 局部米）；0=未标定。须内接于镜圈，不能取整个模型包围盒。")]
        public float scopeApertureRadius;
        public GameObject prefab;          // null = 纯数值配件（弹匣），无模型不改枪械美术
        public string prefabPath;
        [Tooltip("挂入挂点时的旋转校正（欧拉度）：LPW 配件恒等；LPFP 配件 (0,-90,0) 把 +Z 长轴对齐挂点 -X 前向")]
        public Vector3 mountEuler = Vector3.zero;
        [Tooltip("挂入挂点时的位置校正（配件局部系，由批量工具按包围盒自动计算：消音器尾端贴枪口/瞄具底面贴导轨/握把顶端贴护木）")]
        public Vector3 mountOffset = Vector3.zero;
        public List<AttachmentModifierEntry> modifiers = new();

        public bool HasModel => prefab != null;

        /// <summary>挂入挂点时的旋转校正（LPW 配件恒等；LPFP 配件 (0,-90,0) 把 +Z 长轴对齐挂点 -X 前向）.</summary>
        public Quaternion MountRotation => Quaternion.Euler(mountEuler);

        /// <summary>导出为不可变修饰符（SourceId=itemId，可按源精确移除）.</summary>
        public void CollectModifiers(List<WeaponStatModifier> output)
        {
            if (modifiers == null || output == null) return;
            foreach (var m in modifiers)
                output.Add(new WeaponStatModifier(m.stat, m.op, m.value, itemId));
        }
    }

    /// <summary>
    /// Runtime safety net for attachment compatibility rules that are also enforced by the backend.
    /// Old accounts can retain rows that were legal before the compatibility matrix changed; those
    /// rows must never create duplicate geometry or alter weapon stats while the backend cleans them.
    /// </summary>
    public static class AttachmentCompatibilityPolicy
    {
        public static bool IsAllowed(WeaponDefinition definition, AttachmentAssetEntry entry)
        {
            if (entry == null || WeaponAttachmentStore.IsRetiredAttachmentId(entry.itemId)) return false;
            return IsAllowed(definition != null ? definition.CatalogItemId : null, entry)
                && (definition == null || entry.slot != AttachmentSlotType.Underbarrel || !definition.RifleHasVerticalGrip);
        }

        public static bool IsAllowed(string weaponItemId, AttachmentAssetEntry entry)
        {
            if (entry == null || WeaponAttachmentStore.IsRetiredAttachmentId(entry.itemId)) return false;
            if (entry.slot == AttachmentSlotType.Underbarrel
                && (weaponItemId == "weapon.smg01" || weaponItemId == "weapon.smg04"
                    || weaponItemId == "weapon.smg05" || weaponItemId == "weapon.ak" || weaponItemId == "weapon.rifle03")) return false;
            bool pistol = weaponItemId == "weapon.service_pistol" || weaponItemId == "weapon.handgun02"
                || weaponItemId == "weapon.handgun03" || weaponItemId == "weapon.handgun04";
            return !pistol || entry.slot != AttachmentSlotType.Optic
                || entry.itemId == "attach.lpfp.optic.01" || entry.itemId == "attach.lpfp.optic.03";
        }

        public static int RemoveUnsupported(WeaponDefinition definition, List<AttachmentAssetEntry> entries)
        {
            if (entries == null || definition == null) return 0;
            return entries.RemoveAll(entry => !IsAllowed(definition, entry));
        }
    }

    /// <summary>
    /// 配件资产目录（对齐 WeaponAssetCatalog 的单 SO + 条目列表模式）。
    /// 后端为权威目录（CatalogItem/AttachmentCompat 矩阵）；本表只做 itemId → Unity 资产映射，
    /// 校准位姿由 AttachmentCalibration（Phase E）按 (weaponId, itemId) 提供。
    /// </summary>
    [CreateAssetMenu(fileName = "AttachmentAssetCatalog", menuName = "Game/Attachment Asset Catalog")]
    public sealed class AttachmentAssetCatalog : ScriptableObject
    {
        [SerializeField] private List<AttachmentAssetEntry> entries = new();
        [SerializeField] private AttachmentCalibration calibration;
        private Dictionary<string, AttachmentAssetEntry> _byId;
        private static AttachmentAssetCatalog _runtimeInstance;

        public IReadOnlyList<AttachmentAssetEntry> Entries => entries;

        /// <summary>贴合校准表（可为空 = 全部组合使用挂点初始位姿）.</summary>
        public AttachmentCalibration Calibration => calibration;

        /// <summary>
        /// 跨场景访问入口（大厅/战斗两场景共用）：Resources 加载，未找到时返回空目录
        /// （诚实降级——无配件资产时装配表现为空，数值修饰同样为空恒等）。
        /// </summary>
        public static AttachmentAssetCatalog LoadOrDefault()
        {
            if (_runtimeInstance != null) return _runtimeInstance;
            _runtimeInstance = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            if (_runtimeInstance == null) _runtimeInstance = CreateInstance<AttachmentAssetCatalog>();
            return _runtimeInstance;
        }

        public bool TryGet(string itemId, out AttachmentAssetEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(itemId) || WeaponAttachmentStore.IsRetiredAttachmentId(itemId)) return false;
            EnsureIndex();
            return _byId.TryGetValue(itemId, out entry);
        }

        public AttachmentAssetEntry Find(string itemId)
            => TryGet(itemId, out var entry) ? entry : null;

        /// <summary>按槽位过滤（UI 枪匠页选择器用）.</summary>
        public IReadOnlyList<AttachmentAssetEntry> FindBySlot(AttachmentSlotType slot)
        {
            EnsureIndex();
            var result = new List<AttachmentAssetEntry>();
            foreach (var e in entries)
                if (e != null && e.slot == slot && !WeaponAttachmentStore.IsRetiredAttachmentId(e.itemId)) result.Add(e);
            return result;
        }

#if UNITY_EDITOR
        /// <summary>编辑器装配管道专用（运行时只读）.</summary>
        public List<AttachmentAssetEntry> EditorEntries => entries;
#endif

        private void EnsureIndex()
        {
            if (_byId != null) return;
            _byId = new Dictionary<string, AttachmentAssetEntry>(StringComparer.Ordinal);
            if (entries == null) return;
            foreach (var e in entries)
                if (e != null && !string.IsNullOrWhiteSpace(e.itemId)) _byId[e.itemId] = e;
        }
    }

    /// <summary>
    /// 配件层数值修饰源（Priority=0，配件 &lt; 技能 &lt; Buff）：聚合当前装配的全部配件修饰符，
    /// 注入 WeaponStatResolver 既有管线。装配集合变更时 Reset 后整体重算（Docs/13 §6.2）。
    /// </summary>
    public sealed class AttachmentStatModifierSource : IWeaponStatModifierSource
    {
        private readonly List<WeaponStatModifier> _modifiers = new();
        private readonly List<AttachmentAssetEntry> _equipped = new();

        public int Priority => 0;
        public string SourceId => "attachments";
        public IReadOnlyList<AttachmentAssetEntry> Equipped => _equipped;

        /// <summary>整体替换装配集（枪匠保存 / 装备武器时调用），并重建修饰符快照.</summary>
        public void Reset(IEnumerable<AttachmentAssetEntry> equipped)
        {
            _equipped.Clear();
            _modifiers.Clear();
            if (equipped == null) return;
            foreach (var entry in equipped)
            {
                if (entry == null || WeaponAttachmentStore.IsRetiredAttachmentId(entry.itemId)) continue;
                _equipped.Add(entry);
                entry.CollectModifiers(_modifiers);
            }
        }

        public IReadOnlyList<WeaponStatModifier> GetModifiers() => _modifiers;

        public override string ToString()
        {
            var sb = new StringBuilder("attachments[");
            foreach (var e in _equipped) sb.Append(e.itemId).Append(' ');
            return sb.Append(']').ToString();
        }
    }
}
