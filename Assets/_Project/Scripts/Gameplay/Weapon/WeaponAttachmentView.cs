using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 配件运行时表现（Docs/21 Phase E/G，socket 驱动；FP/TP 视图通用）：
    /// 按当前装配在武器视图的 Attach_* 挂点上实例化配件模型。
    /// 位姿 = 挂点局部位姿 × 配件 mountEuler（轴系校正）× AttachmentCalibration offset（逐组合微调）。
    /// 替代旧版 FPWeaponAttachmentView 的 3 条硬编码映射（scope_01/02+silencer 显隐切换）。
    /// 2026-09-05 增量（用户需求）：LPFP 枪模自带的机械瞄具子件（*_Iron_Sights）在装配
    /// 任意瞄具（红点/全息/低倍/高倍——2026-09-05 二次确认：不按放大档位区分）时隐藏——
    /// 机瞄柱挡在镜片视野内影响瞄具体验；卸下/换装恢复原状。
    /// 判定与名字匹配均为纯函数，EditMode 可测。
    /// </summary>
    public sealed class WeaponAttachmentView : MonoBehaviour
    {
        /// <summary>激光指示器目录 id（目录里唯一带光束特效的配件）：装配其克隆时按需挂 LaserSightBeam。
        /// 用稳定 id 而非序列化标记位，避免依赖目录资产重生成（catalog 资产由生成器工具维护）。</summary>
        public const string LaserItemId = "attach.lpw.tactical.laser";

        private AttachmentSocket[] _sockets;
        /// <summary>挂点静止帧缓存（挂点相对视图根的朝向，Awake/首次缓存时的 prefab 授权姿态）。
        /// 挂点位于动画骨骼（原生 FP=Armature/weapon）之下，实时测量随动画姿态漂移——
        /// 切换回来的视图实例以收枪冻结姿态被重新激活且 ApplyAttachments 先于 PlayDraw，
        /// 用实时帧做校准换算会把收枪姿态烤进 delta（瞄具/镭射严重偏位）。</summary>
        private readonly Dictionary<AttachmentSlotType, Quaternion> _socketRestFrames = new();
        private readonly List<GameObject> _spawned = new();
        private readonly List<(GameObject go, bool wasActive)> _suppressedStock = new();

        public IReadOnlyList<GameObject> Spawned => _spawned;

        /// <summary>子节点名是否为枪模自带的机械瞄具（LPFP 实测命名：Assault_Rifle_01_Iron_Sights /
        /// Grenade_Launcher_01_Front_Iron_Sights / SMG_01_Iron_Sights 等；大小写不敏感含
        /// iron_sight/ironsight 变体）。挂点上实例化的配件（Att_/Attach_ 前缀）一律排除。</summary>
        public static bool IsStockIronSightsName(string childName)
        {
            if (string.IsNullOrEmpty(childName)) return false;
            if (childName.StartsWith("Att_") || childName.StartsWith("Attach_")) return false;
            var lower = childName.ToLowerInvariant();
            return lower.Contains("iron_sight") || lower.Contains("ironsight");
        }

        private void Awake() => CacheSockets();

        /// <summary>按槽位取挂点 Transform（瞄具眼点反查等校准路径用；无挂点=null）。</summary>
        public Transform GetSocketTransform(AttachmentSlotType slot)
        {
            var socket = FindSocket(slot);
            return socket != null ? socket.transform : null;
        }

        /// <summary>按 itemId 查挂点上实例化的配件克隆（P4 实体镜取瞄具几何用；未挂=null）。</summary>
        public Transform FindSpawned(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            foreach (var go in _spawned)
                if (go != null && go.name == "Att_" + itemId) return go.transform;
            return null;
        }

        /// <summary>查枪模自带的出厂镜体（P4 实体镜内置瞄具路径；与 ApplyAttachments 的
        /// scope 抑制匹配器同源——名称含 scope 且不在配件子树内）。返回首个带渲染器的命中。</summary>
        public Transform FindStockScope()
        {
            var children = GetComponentsInChildren<Transform>(true);
            foreach (var child in children)
            {
                if (child == null || child == transform || child.parent == null) continue;
                if (IsUnderSpawned(child)) continue;
                var name = child.name;
                if (!name.ToLowerInvariant().Contains("scope")) continue;
                if (name.StartsWith("Att_") || name.StartsWith("Attach_")) continue;
                if (child.GetComponentInChildren<Renderer>(true) != null) return child;
            }
            return null;
        }

        /// <summary>应用装配（整体替换）。纯数值配件（无模型，如弹匣）自动跳过。
        /// 装瞄具（任意档位）时自动隐藏枪模自带的出厂瞄具（scope 子网格）与
        /// 机械瞄具（*_Iron_Sights），避免镜内遮挡。
        /// laserBeamEnabled：是否为激光配件挂光束组件——仅本地第一人称视图传 true
        /// （TP 视图/枪匠预览/校准窗传 false：射线只服务本地瞄准）。</summary>
        public void ApplyAttachments(AttachmentAssetCatalog catalog, string weaponItemId, IEnumerable<AttachmentAssetEntry> attachments,
            bool laserBeamEnabled = true)
        {
            Clear();
            if (attachments == null) return;
            if (_sockets == null || _sockets.Length == 0) CacheSockets();
            if (_sockets == null || _sockets.Length == 0) return;

            var calibration = catalog != null ? catalog.Calibration : null;
            var hasOptic = false;
            foreach (var entry in attachments)
            {
                if (entry == null) continue;
                if (entry.slot == AttachmentSlotType.Optic) hasOptic = true;
                if (!entry.HasModel) continue;
                var socket = FindSocket(entry.slot);
                if (socket == null) continue;   // 该武器视图无此槽位挂点：诚实降级为不显示
                var go = Instantiate(entry.prefab, socket.transform);
                go.name = "Att_" + entry.itemId;
                // mountOffset 在配件局部系（先旋转后平移：先摆正姿态，再按包围盒算出的贴合偏移落位）
                go.transform.localRotation = entry.MountRotation;
                go.transform.localPosition = entry.mountOffset;
                if (calibration != null && calibration.TryGet(weaponItemId, entry.itemId, out var posOffset, out var eulerOffset, out var authorFrame))
                {
                    // 跨帧换算（2026-09-05 激光错位修正）：delta 存于作者帧（枪匠预览挂点）局部系，
                    // 而各视图挂点局部朝向不同（实测 SMG_01 FP/TP 挂点差一俯仰分量）——
                    // 先把位移转回视图根空间（两视图根均为持枪朝向的枪体空间），再转到本挂点局部；
                    // 旋转 delta 做共轭换帧。旧行（无作者帧）维持直通。
                    // 帧取值用缓存的静止帧（2026-09-07 切枪配件错位修复）：换枪回来的视图实例
                    // 以收枪冻结姿态重新激活且本方法先于 PlayDraw 执行，实时测量会把收枪姿态
                    // 共轭进校准 delta——改用 Awake/首次缓存时的 prefab 授权姿态，换算与动画解耦。
                    if (authorFrame != default)
                    {
                        var currentFrame = _socketRestFrames.TryGetValue(entry.slot, out var restFrame)
                            ? restFrame
                            : Quaternion.Inverse(transform.rotation) * socket.transform.rotation;
                        if (Quaternion.Angle(currentFrame, authorFrame) > 0.05f)
                        {
                            posOffset = Quaternion.Inverse(currentFrame) * authorFrame * posOffset;
                            eulerOffset = (Quaternion.Inverse(currentFrame) * authorFrame
                                           * Quaternion.Euler(eulerOffset)
                                           * Quaternion.Inverse(authorFrame) * currentFrame).eulerAngles;
                        }
                    }
                    go.transform.localPosition += posOffset;
                    go.transform.localRotation *= Quaternion.Euler(eulerOffset);
                }
                _spawned.Add(go);
                if (laserBeamEnabled && entry.itemId == LaserItemId)
                {
                    var beam = go.AddComponent<LaserSightBeam>();
                    beam.Setup(); // 显式初始化（EditMode 不触发 OnEnable；运行时幂等）
                }
            }

            // 出厂子件抑制（装任意瞄具触发）：①原生狙击枪自带 scope 网格（既有行为）；
            // ②LPFP 机械瞄具（*_Iron_Sights，用户需求：机瞄柱遮挡瞄具视野）。
            // 卸下/换装即经 Clear 恢复原状。注意跳过挂点上实例化的配件子树——配件模型内部
            // 可能也有含 scope/iron 命名的部件（如 Scope_02 镜体），绝不能误隐藏配件本体。
            if (hasOptic)
            {
                foreach (var child in transform.GetComponentsInChildren<Transform>(true))
                {
                    if (child.parent == null || child == transform) continue;
                    if (IsUnderSpawned(child)) continue;
                    var name = child.name;
                    var suppressScope = name.ToLowerInvariant().Contains("scope")
                        && !name.StartsWith("Att_") && !name.StartsWith("Attach_");
                    var suppressIronsights = IsStockIronSightsName(name);
                    if ((!suppressScope && !suppressIronsights) || !child.gameObject.activeSelf) continue;
                    _suppressedStock.Add((child.gameObject, true));
                    child.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>子节点是否位于本视图挂点上实例化的配件子树内（配件部件绝不参与出厂件抑制）。</summary>
        private bool IsUnderSpawned(Transform child)
        {
            foreach (var spawned in _spawned)
                if (spawned != null && child.IsChildOf(spawned.transform)) return true;
            return false;
        }

        public void Clear()
        {
            foreach (var go in _spawned)
                if (go != null) Destroy(go);
            _spawned.Clear();
            foreach (var (go, wasActive) in _suppressedStock)
                if (go != null) go.SetActive(wasActive);
            _suppressedStock.Clear();
        }

        private AttachmentSocket FindSocket(AttachmentSlotType slot)
        {
            foreach (var s in _sockets)
                if (s != null && s.Slot == slot) return s;
            return null;
        }

        private void CacheSockets()
        {
            _sockets = GetComponentsInChildren<AttachmentSocket>(true);
            // 静止帧与挂点同刻捕获：组件 Awake 发生在实例化当帧（动画器尚未评估，prefab 授权姿态），
            // 复用实例后续换枪回来的姿态变化不影响缓存（仅 _sockets 为空时才会重入本方法）。
            _socketRestFrames.Clear();
            foreach (var s in _sockets)
                if (s != null && !_socketRestFrames.ContainsKey(s.Slot))
                    _socketRestFrames[s.Slot] = Quaternion.Inverse(transform.rotation) * s.transform.rotation;
        }
    }

    /// <summary>
    /// 配件装配的跨场景传输（大厅 → 战斗，PlayerPrefs 桥）：后端为持久化权威，
    /// 本类只在进入战斗前缓存"当前局装配"，键 = itemId，值 = 配件 itemId。
    /// </summary>
    public static class WeaponAttachmentStore
    {
        private const string PrefKeyPrefix = "Game.Loadout.Attachments.";
        public static readonly string[] AllSlots = { "Optic", "Muzzle", "Magazine", "Tactical", "Underbarrel" };

        public static void Save(string weaponItemId, IReadOnlyDictionary<string, string> slotToItemId)
        {
            if (string.IsNullOrWhiteSpace(weaponItemId)) return;
            foreach (var slot in AllSlots)
                PlayerPrefs.SetString(PrefKeyPrefix + weaponItemId + "." + slot, slotToItemId != null && slotToItemId.TryGetValue(slot, out var id) ? id : string.Empty);
            PlayerPrefs.Save();
        }

        public static void Load(string weaponItemId, Dictionary<string, string> slotToItemId)
        {
            slotToItemId.Clear();
            if (string.IsNullOrWhiteSpace(weaponItemId)) return;
            foreach (var slot in AllSlots)
            {
                var id = PlayerPrefs.GetString(PrefKeyPrefix + weaponItemId + "." + slot, string.Empty);
                if (!string.IsNullOrWhiteSpace(id)) slotToItemId[slot] = id;
            }
        }
    }

    /// <summary>
    /// 枪匠页草稿持久化（Docs/21 v2 收尾）：装备/购买/卸下的选择即时写入本地，
    /// 重进枪匠页恢复"退出时所见"。后端配装仍是长期权威（点保存配置后两者一致）；
    /// 草稿保证未保存配置时刷新/重进不丢屏幕状态。
    /// </summary>
    public static class GunsmithDraftStore
    {
        private const string KeyPrefix = "Game.GunsmithDraft.";

        public static void Save(string weaponItemId, IReadOnlyDictionary<string, string> slotToItemId)
        {
            if (string.IsNullOrWhiteSpace(weaponItemId)) return;
            var entries = slotToItemId.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => "\"" + kv.Key + "\":\"" + kv.Value + "\"");
            PlayerPrefs.SetString(KeyPrefix + weaponItemId, "{" + string.Join(",", entries) + "}");
            PlayerPrefs.Save();
        }

        public static bool TryLoad(string weaponItemId, Dictionary<string, string> slotToItemId)
        {
            slotToItemId.Clear();
            if (string.IsNullOrWhiteSpace(weaponItemId)) return false;
            var json = PlayerPrefs.GetString(KeyPrefix + weaponItemId, string.Empty);
            if (string.IsNullOrEmpty(json)) return false;
            // 轻量解析（固定结构 {"Slot":"itemId",...}）
            foreach (var pair in json.Trim('{', '}').Split(','))
            {
                var kv = pair.Split(':');
                if (kv.Length != 2) continue;
                var slot = kv[0].Trim('"');
                var id = kv[1].Trim('"');
                if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(id)) slotToItemId[slot] = id;
            }
            return slotToItemId.Count > 0;
        }

        public static void Clear(string weaponItemId)
        {
            if (string.IsNullOrWhiteSpace(weaponItemId)) return;
            PlayerPrefs.DeleteKey(KeyPrefix + weaponItemId);
            PlayerPrefs.Save();
        }
    }
}
