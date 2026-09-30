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
    [DefaultExecutionOrder(35)]
    public sealed class TPWeaponMeshSwapper : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Transform weaponBone;

        private GameObject _current;
        private WeaponAttachmentView _attachmentView;
        private Transform _baseLeftHandTarget;
        private TPLeftHandIK _leftHandIk;
        private bool _leftHandIkBound;
        private bool _throwableHidden;
        public void SetThrowableHidden(bool hidden)
        {
            _throwableHidden = hidden;
            if (_current != null) _current.SetActive(!hidden);
        }

        /// <summary>当前 TP 武器实例（TPWeaponFX / TPLeftHandIK 只读消费）。</summary>
        public GameObject CurrentInstance => _current;
        /// <summary>当前武器枪口挂点（prefab 内 "Muzzle" 子节点）。</summary>
        public Transform CurrentMuzzle { get; private set; }
        /// <summary>
        /// 当前武器左手持枪挂点：已装配带 LeftHandGrip 子标记的下挂握把时优先返回握把标记
        /// （左手改握握把），否则返回武器 prefab 烘焙的 LeftHandTarget（护木/护圈）。
        /// </summary>
        public Transform CurrentLeftHandTarget =>
            _throwableHidden ? null : ResolveLeftHandTarget(_attachmentView, _baseLeftHandTarget);

        /// <summary>
        /// 左手持枪目标决策（纯函数，EditMode 可测）：配件克隆（"Att_" 前缀命名）里带
        /// LeftHandGrip 子标记的视为垂直握把——左手改握握把；没有则回退武器自带 LeftHandTarget。
        /// </summary>
        public static Transform ResolveLeftHandTarget(WeaponAttachmentView attachments, Transform fallback)
        {
            if (attachments != null)
            {
                foreach (var spawned in attachments.Spawned)
                {
                    if (spawned == null) continue;
                    var grip = spawned.transform.Find("LeftHandGrip");
                    if (grip != null) return grip;
                }
            }
            return fallback;
        }

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
            EnsureLeftHandIk();
        }

        /// <summary>
        /// TP 枪械实例与左手 IK 必须共用同一帧驱动。枪已经由本组件成功生成时，
        /// 左手姿态也必须由同一生命周期提交；旧 prefab 若缺少组件则运行时自愈补齐。
        /// </summary>
        private void EnsureLeftHandIk()
        {
            if (_leftHandIk == null)
            {
                _leftHandIkBound = false;
                _leftHandIk = GetComponent<TPLeftHandIK>();
                if (_leftHandIk == null) _leftHandIk = gameObject.AddComponent<TPLeftHandIK>();
            }
            if (_leftHandIkBound) return;
            _leftHandIk.Bind(this);
            _leftHandIkBound = true;
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

        private void LateUpdate()
        {
            EnsureLeftHandIk();
            // Do not use isActiveAndEnabled here: EditMode/offline preview objects report false
            // even when the serialized component and hierarchy are enabled, recreating the exact
            // "one presentation path solves, another stays on the magazine" split. Death still
            // disables the component explicitly, so enabled + activeInHierarchy is the real gate.
            if (_leftHandIk != null && _leftHandIk.enabled && _leftHandIk.gameObject.activeInHierarchy)
                _leftHandIk.ApplyPoseFrame();
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
            _current.SetActive(!_throwableHidden);
            // 武器与 TP 身体同层：本地主相机剔除 LocalPlayerBody（不渲染自己的 TP 武器），
            // 远端代理渲染身体时武器随之可见。Instantiate 保留 prefab 层（Default），子节点不继承父骨骼层，须显式同步。
            int bodyLayer = weaponBone.gameObject.layer;
            foreach (var t in _current.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = bodyLayer;
            _current.name = definition.ThirdPersonViewPrefab.name;

            // 枪口/左手挂点（Day4.2：TPWeaponFX 与 TPLeftHandIK 消费）
            CurrentMuzzle = _current.transform.Find("Muzzle");
            if (CurrentMuzzle != null && CurrentMuzzle.Find("MuzzleExit") != null)
                CurrentMuzzle = CurrentMuzzle.Find("MuzzleExit");
            _baseLeftHandTarget = _current.transform.Find("LeftHandTarget");

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
            AttachmentCompatibilityPolicy.RemoveUnsupported(definition, entries);
            _attachmentView = null;
            if (entries.Count > 0)
            {
                var view = _current.AddComponent<WeaponAttachmentView>();
                view.ApplyAttachments(AttachmentAssetCatalog.LoadOrDefault(), weaponItemId, entries, laserBeamEnabled: false); // TP 视图非本地第一人称：不挂激光束
                _attachmentView = view;
                _current.GetComponent<TPGripPose>()?.CalibrateAttachmentTargets(view);
                ResolveAttachmentMuzzle(view);
                // 后挂入的配件不在上方层同步循环内，须补齐到身体层
                foreach (var spawned in view.Spawned)
                    if (spawned != null)
                        foreach (var t in spawned.GetComponentsInChildren<Transform>(true))
                            t.gameObject.layer = bodyLayer;
                foreach (var device in _current.GetComponentsInChildren<TacticalFlashlight>(true))
                    Game.Presentation.Weapon.TacticalFlashlightShadowFilter.Bind(device, _current);
            }
        }

        private void ResolveAttachmentMuzzle(WeaponAttachmentView attachments)
        {
            if (CurrentMuzzle == null || attachments == null) return;
            var socket = attachments.GetSocketTransform(AttachmentSlotType.Muzzle);
            if (socket == null) return;
            foreach (Transform child in socket)
            {
                if (!child.name.StartsWith("Att_", System.StringComparison.Ordinal) || !child.gameObject.activeInHierarchy) continue;
                var authored = child.Find("MuzzleExit");
                if (authored != null) { CurrentMuzzle = authored; break; }
                if (!Game.Presentation.Weapon.WeaponView.TryGetAttachmentTip(child, CurrentMuzzle, out var tip)) continue;
                var worldTip = child.TransformPoint(tip);
                if (Vector3.Dot(worldTip - CurrentMuzzle.position, CurrentMuzzle.forward) <= .005f) continue;
                var marker = new GameObject("Runtime_TP_Muzzle").transform;
                marker.SetParent(child, false);
                marker.SetPositionAndRotation(worldTip, CurrentMuzzle.rotation);
                marker.gameObject.layer = CurrentMuzzle.gameObject.layer;
                CurrentMuzzle = marker;
                break;
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
