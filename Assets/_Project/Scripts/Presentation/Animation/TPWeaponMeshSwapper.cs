using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 武器挂点 mesh 切换：订阅 Arsenal 交换点事件，把当前武器的
    /// ThirdPersonViewPrefab 实例挂到右手骨骼下（远端可见的武器外形）。
    /// 挂点局部偏移烘焙在各武器预制体根 Transform 上（LPFP 原厂约定：
    /// 武器挂 hand_R + 每武器一个握把对齐偏移，本组件不覆盖该变换）。
    /// </summary>
    public sealed class TPWeaponMeshSwapper : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Transform weaponBone;

        private GameObject _current;

        /// <summary>当前 TP 武器实例（TPWeaponFX / TPLeftHandIK 只读消费）。</summary>
        public GameObject CurrentInstance => _current;
        /// <summary>当前武器枪口挂点（prefab 内 "Muzzle" 子节点）。</summary>
        public Transform CurrentMuzzle { get; private set; }
        /// <summary>当前武器左手持枪挂点（prefab 内 "LeftHandTarget" 子节点）。</summary>
        public Transform CurrentLeftHandTarget { get; private set; }

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponentInParent<Arsenal>();
            if (weaponBone == null)
            {
                var anim = GetComponentInParent<Animator>();
                if (anim != null)
                {
                    var bone = anim.GetBoneTransform(HumanBodyBones.RightHand);
                    if (bone != null) weaponBone = bone;
                }
            }
        }

        private void OnEnable()
        {
            if (arsenal == null) return;
            arsenal.OnActiveWeaponChanged += HandleWeaponChanged;
        }

        private void OnDisable()
        {
            if (arsenal == null) return;
            arsenal.OnActiveWeaponChanged -= HandleWeaponChanged;
        }

        private void Start()
        {
            var initial = arsenal != null ? arsenal.ActiveWeapon : null;
            if (initial != null) HandleWeaponChanged(initial);
        }

        private void HandleWeaponChanged(WeaponDefinition definition)
        {
            if (weaponBone == null)
            {
                Debug.LogWarning($"[TPWeaponMeshSwapper] 右手骨骼未解析，TP 武器模型未挂载（{definition?.name}）", this);
                return;
            }
            if (definition == null) return;
            if (definition.ThirdPersonViewPrefab == null)
            {
                // 静默失败会让"第三人称没枪"完全不可见（2026-09-05 用户实测事故：
                // 定义资产引用的 TP 预制体根 fileID 失配 → null → 直接 return）——必须留痕
                Debug.LogWarning($"[TPWeaponMeshSwapper] {definition.name} 的 ThirdPersonViewPrefab 为空/断链，TP 无枪。请在 Inspector 重新指定 TP 武器预制体。", this);
                return;
            }
            if (_current != null) Destroy(_current);
            // 握把对齐偏移保留在武器预制体根 Transform 上（Instantiate 挂到父骨骼时原样生效）
            _current = Instantiate(definition.ThirdPersonViewPrefab, weaponBone);
            // 武器与 TP 身体同层：本地主相机剔除 LocalPlayerBody（不渲染自己的 TP 武器），
            // 远端代理渲染身体时武器随之可见。Instantiate 保留 prefab 层（Default），子节点不继承父骨骼层，须显式同步。
            int bodyLayer = weaponBone.gameObject.layer;
            foreach (var t in _current.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = bodyLayer;
            _current.name = definition.ThirdPersonViewPrefab.name;

            // 枪口/左手挂点（Day4.2：TPWeaponFX 与 TPLeftHandIK 消费）
            CurrentMuzzle = _current.transform.Find("Muzzle");
            CurrentLeftHandTarget = _current.transform.Find("LeftHandTarget");

            // 配件表现（Docs/21 Phase G）：远端可见的改装外形。Gate A-2（2026-09-08 复审）：
            // 附件真相 = 服务器权威快照（远端玩家不得读本机 WeaponAttachmentStore 作为他人
            // 附件真相）；快照不在位（离线/调试 Host）才回退本机存储。
            var weaponItemId = definition.CatalogItemId;
            var entries = new System.Collections.Generic.List<AttachmentAssetEntry>();
            if (!TryResolveAuthoritativeAttachments(definition, entries) && !string.IsNullOrEmpty(weaponItemId))
            {
                var slotToItemId = new System.Collections.Generic.Dictionary<string, string>();
                WeaponAttachmentStore.Load(weaponItemId, slotToItemId);
                if (slotToItemId.Count > 0)
                {
                    var attachmentCatalog = AttachmentAssetCatalog.LoadOrDefault();
                    foreach (var kv in slotToItemId)
                        if (attachmentCatalog.TryGet(kv.Value, out var entry)) entries.Add(entry);
                }
            }
            if (entries.Count > 0)
            {
                var view = _current.AddComponent<WeaponAttachmentView>();
                view.ApplyAttachments(AttachmentAssetCatalog.LoadOrDefault(), weaponItemId, entries, laserBeamEnabled: false); // TP 视图非本地第一人称：不挂激光束
                // 后挂入的配件不在上方层同步循环内，须补齐到身体层
                foreach (var spawned in view.Spawned)
                    if (spawned != null)
                        foreach (var t in spawned.GetComponentsInChildren<Transform>(true))
                            t.gameObject.layer = bodyLayer;
            }
        }

        /// <summary>联网权威附件解析（Gate A-2）：快照在位返回 true（含权威空装配）；快照
        /// 不在位（离线/调试 Host）返回 false 回退本机存储。</summary>
        private bool TryResolveAuthoritativeAttachments(WeaponDefinition definition,
            System.Collections.Generic.List<AttachmentAssetEntry> entries)
        {
            var networkState = GetComponentInParent<NetworkWeaponState>();
            return networkState != null && networkState.TryGetAuthoritativeAttachments(definition, entries);
        }
    }
}
