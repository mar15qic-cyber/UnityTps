using Game.Gameplay.Action;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// FP 武器视图唯一管理者：按 Arsenal 事件切换 WeaponDefinition.FirstPersonViewPrefab
    /// 实例（收旧枪 → 交换点换实例并播出枪 → 完成收尾）。视图实例自带
    /// FPWeaponAnimator/WeaponView，激活即接管表现。
    /// </summary>
    [DefaultExecutionOrder(25)]
    public sealed class FPWeaponRig : MonoBehaviour, Game.Gameplay.Weapon.IWeaponPresentationGate
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform viewRoot;

        private readonly System.Collections.Generic.Dictionary<WeaponDefinition, GameObject> _views = new();
        private GameObject _activeView;
        private WeaponDefinition _activeDefinition;
        // 审计 2026-09-16 §3：第一人称视图可见性唯一写者（原因合成 + 受控注册基线）。
        // 死亡/镜内遮罩/复活准备各持原因位，不再共享一个 bool + 一张 Renderer 快照
        //（旧双写链：相机关→Rig 把 false 存成原始状态→复活相机恢复→Rig 下一帧又写回 false = 永久隐形）。
        private readonly FPViewModelVisibility _visibility = new();
        /// <summary>复活恢复协程代际：死亡/切枪/禁用即作废，过期协程不得恢复错误视图。</summary>
        private int _respawnGeneration;
        /// <summary>测试接缝（EditMode 不泵协程，用代际断言"复活前再次死亡使旧恢复作废"）。</summary>
        public int RespawnGenerationForTests => _respawnGeneration;
        /// <summary>FPWeaponMotion 实例（挂在 viewRoot=FP_Weapon_Root，见 §3.3-6 核实；懒解析缓存）。</summary>
        private Game.Presentation.Camera.FPWeaponMotion _weaponMotion;

        public GameObject ActiveView => _activeView;
        private UnityEngine.Camera _aimCamera;

        private void LateUpdate()
        {
            if (controller == null) return;
            if (_aimCamera == null) _aimCamera = GetComponentInParent<UnityEngine.Camera>();
            if (_aimCamera != null && _aimCamera.isActiveAndEnabled)
                controller.SetPresentedAim(_aimCamera.transform.position, _aimCamera.transform.forward);
        }
        /// <summary>镜内遮罩原因是否生效（语义收窄：只代表 ScopeOverlay，不再代表"任意隐藏"）。</summary>
        public bool IsScopedViewmodelHidden => _visibility.HasReason(FPViewHideReason.ScopeOverlay);
        public event System.Action<GameObject> OnActiveViewChanged;

        // ---- IWeaponPresentationGate（Gameplay 接口，Presentation 实现；单向依赖不变）----
        // LaserSightBeam 等持续表现写者据此判断"当前是否允许出束/运行"——
        // 死亡/镜内/复活准备期间为 false，写者不得每帧自行 enabled=true（"无枪红线"根因）。
        public bool IsWeaponViewVisible => _activeView != null && !_visibility.IsHidden
            && (!_activeView.TryGetComponent<FPWeaponAnimator>(out var animator) || !animator.IsThrowablePresentationActive);
        GameObject Game.Gameplay.Weapon.IWeaponPresentationGate.ActiveView => _activeView;
        bool Game.Gameplay.Weapon.IWeaponPresentationGate.IsWeaponViewVisible => IsWeaponViewVisible;

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponentInParent<Arsenal>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (viewRoot == null) viewRoot = transform;
            // Scene-authored FP views are legacy debris: the rig owns every view
            // instance and will instantiate/cache it from WeaponDefinition. Keep
            // an authored WeaponView from rendering beside the owned view.
            PurgeUnmanagedSerializedViews();
            // R4 审计修复：实体镜唯一挂载入口（幂等；Dedicated 构建内不挂载）——此前 PhysicalScopeView
            // 只有类定义没有任何运行时挂载路径，正式玩家永远不会启用实体镜表现
            Game.Presentation.Camera.PhysicalScopeView.EnsureMounted(this);
        }

        private void OnEnable()
        {
            if (arsenal != null)
            {
                arsenal.OnSwitchStarted += HandleSwitchStarted;
                arsenal.OnActiveWeaponChanged += HandleActiveWeaponChanged;
            }
            if (controller != null) controller.OnWeaponEquipped += HandleControllerWeaponEquipped;
        }

        private void OnDisable()
        {
            // 审计 §3.3-4：禁用/离场边界作废在途复活协程，并清空全部原因位、按受控基线还原
            //（复用/重建后不得带着上一局的隐藏原因或过期协程回来）
            _respawnGeneration++;
            _visibility.ResetAll();
            if (arsenal != null)
            {
                arsenal.OnSwitchStarted -= HandleSwitchStarted;
                arsenal.OnActiveWeaponChanged -= HandleActiveWeaponChanged;
            }
            if (controller != null) controller.OnWeaponEquipped -= HandleControllerWeaponEquipped;
        }

        private void Start()
        {
            // 初始武器视图（Arsenal.Start 已广播过，这里直接取当前武器兜底）
            var initial = arsenal != null ? arsenal.ActiveWeapon : controller != null ? controller.Definition : null;
            if (initial != null) ShowView(initial, playDraw: true);
        }

        private void HandleSwitchStarted(WeaponDefinition oldWeapon, int _)
        {
            // 收旧枪：由旧视图的 FPWeaponAnimator 播 Holster
            if (_activeView != null && _activeView.TryGetComponent(out FPWeaponAnimator animator))
                animator.PlayHolster();
        }

        private void HandleActiveWeaponChanged(WeaponDefinition newWeapon)
        {
            if (newWeapon == null) return;
            ShowView(newWeapon, playDraw: true);
        }

        private void HandleControllerWeaponEquipped(WeaponDefinition equipped)
        {
            if (equipped == null) return;
            // Controller.Start can run before FPWeaponRig.Start. The initial view is still
            // owned by Start (draw timing preserved).
            if (_activeView == null) return;
            if (_activeDefinition != equipped)
            {
                // 审计 2026-09-15 §3.2：网络权威校正/服务器驱动装备与本地 Arsenal 交换链不同源——
                // controller 已换定义而 Arsenal 事件未必携带本次变化。这里作为兜底交换点切换 FP
                // 模型，保证弹道/HUD 与第一人称模型同源；本地切枪路径双事件幂等（ShowView 同定义早退）。
                ShowView(equipped, playDraw: true);
                return;
            }
            // Re-apply persisted optics once the runtime stats/definition are initialized.
            ApplyPersistedAttachments(_activeView, equipped);
        }

        private void ShowView(WeaponDefinition definition, bool playDraw)
        {
            PurgeUnmanagedSerializedViews();
            if (definition == null || definition == _activeDefinition) return;
            var next = GetOrCreateView(definition);
            if (next == null) return;

            if (_activeView != null && _activeView != next)
                _activeView.SetActive(false);

            _activeView = next;
            _activeDefinition = definition;
            DeactivateUnmanagedViewsExcept(next);
            _activeView.SetActive(true);
            ApplyPersistedAttachments(_activeView, definition);
            // 审计 §3.3-3：切枪/换视图都要把新视图的 Renderer 纳入受控基线（死亡原因持续继承，
            // 不会因切枪被旧快照意外释放）；新实例基线=prefab 作者状态。
            RegisterViewRenderers(_activeView);
            OnActiveViewChanged?.Invoke(_activeView);
            if (playDraw && _activeView.TryGetComponent(out FPWeaponAnimator animator))
                animator.PlayDraw();
        }

        /// <summary>把视图（含动态配件）的全部 Renderer 注册进受控基线（重复注册安全）。</summary>
        private void RegisterViewRenderers(GameObject view)
        {
            if (view == null) return;
            foreach (var renderer in view.GetComponentsInChildren<Renderer>(true))
                _visibility.Register(renderer);
        }

        private void ApplyPersistedAttachments(GameObject view, WeaponDefinition definition)
        {
            // Docs/21 Phase E/G：socket 驱动装配——读当前局装配 → 解析配件目录条目 →
            // ① 挂点实例化（视觉）② 数值修饰注入 WeaponController（弹匣容量/散布/后坐等）
            if (view == null || definition == null) return;
            // 35/36 FP prefab 根缺 WeaponAttachmentView（SocketBuilder 只写挂点）——与
            // TPWeaponMeshSwapper 对称地运行时补挂（幂等；_views 缓存实例切换时重复应用也安全）。
            if (!view.TryGetComponent(out WeaponAttachmentView attachments))
            {
                attachments = view.AddComponent<WeaponAttachmentView>();
                Debug.LogWarning($"[FPWeaponRig] {definition.name} FP 视图缺 WeaponAttachmentView，已运行时补挂", this);
            }

            var entries = new System.Collections.Generic.List<AttachmentAssetEntry>();
            var weaponItemId = definition.CatalogItemId;
            // Gate A-2（2026-09-08 复审）：联网玩家附件真相 = 服务器权威快照——含 Owner 本机
            //（本地存储只反映本机账号，不保证是本次服务器快照）；快照不在位（离线/调试 Host）
            // 才回退本机 WeaponAttachmentStore（离线单人行为不变）。
            if (!TryResolveAuthoritativeAttachments(definition, controller, entries) && !string.IsNullOrEmpty(weaponItemId))
            {
                var slotToItemId = new System.Collections.Generic.Dictionary<string, string>();
                WeaponAttachmentStore.Load(weaponItemId, slotToItemId);
                var attachmentCatalog = AttachmentAssetCatalog.LoadOrDefault();
                foreach (var kv in slotToItemId)
                    if (attachmentCatalog.TryGet(kv.Value, out var entry))
                        entries.Add(entry);
            }

            AttachmentCompatibilityPolicy.RemoveUnsupported(definition, entries);

            attachments.ApplyAttachments(
                entries.Count > 0 ? AttachmentAssetCatalog.LoadOrDefault() : null, weaponItemId, entries);
            // 双相机架构：世界相机剔除层 8/9，武器相机仅渲染 FirstPersonView(9)——配件 prefab 层为 0，
            // 不同步会被武器相机剔除（对齐 TP 侧同步循环与管线 SetLayerRecursive 约定）。
            var fpLayer = LayerMask.NameToLayer("FirstPersonView");
            if (fpLayer < 0) fpLayer = view.layer;
            foreach (var socket in view.GetComponentsInChildren<AttachmentSocket>(true))
                SetLayerRecursive(socket.transform, fpLayer);
            foreach (var spawned in attachments.Spawned)
                if (spawned != null) SetLayerRecursive(spawned.transform, fpLayer);
            // 审计 §3.3-3：动态挂上的配件 Renderer 也必须进受控基线（否则配件不参与原因合成，
            // 死亡期间挂新配件会闪现，或基线从未建立导致复活后配件永久不可见）
            foreach (var spawned in attachments.Spawned)
                if (spawned != null)
                    foreach (var renderer in spawned.GetComponentsInChildren<Renderer>(true))
                        _visibility.Register(renderer);
            if (controller != null && controller.IsInitialized)
                controller.SetAttachments(entries);   // 装备期重算：含加长弹匣的弹容量重建

        }

        /// <summary>联网权威附件解析（Gate A-2）：快照在位返回 true（含权威空装配），由
        /// NetworkWeaponState 按 definition 所属权威槽位解析；快照不在位返回 false 回退本机存储。</summary>
        private static bool TryResolveAuthoritativeAttachments(WeaponDefinition definition, WeaponController controller,
            System.Collections.Generic.List<AttachmentAssetEntry> entries)
        {
            var networkState = controller != null ? controller.GetComponent<NetworkWeaponState>() : null;
            return networkState != null && networkState.TryGetAuthoritativeAttachments(definition, entries);
        }

        private static void SetLayerRecursive(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            foreach (Transform child in root) SetLayerRecursive(child, layer);
        }

        // ---- 2026-09-16 审计 §4：Owner FP 死亡/复活显式入口（Gameplay 经本表现层入口驱动）----

        /// <summary>
        /// 死亡边界（Owner FP）：置 Death 原因位（当帧关掉全部已注册 Renderer）并让动画/武器运动
        /// 进入死亡态；同时作废任何在途的复活恢复协程。
        /// 只隐藏 Renderer、**不**停用视图 GameObject：停用会走 AnimancerComponent 的
        /// `_ActionOnDisable` 路径（FP 视图=Stop/Reset 语义），既丢姿态也不保证复活后重建 Idle。
        /// 幂等：与 TP 幂等闸无关，重复死亡广播只置一次位。
        /// </summary>
        public void ApplyDeathState()
        {
            _respawnGeneration++; // 期间到达的旧复活协程作废（含"复活下一帧前再次死亡"）
            ApplyDeathStateToActiveView();
            _visibility.SetHidden(FPViewHideReason.Death, true);
        }

        /// <summary>
        /// 复活边界（Owner FP）：释放 Death、置 RespawnPreparing，重建腰射 Idle 并评估一帧后
        /// 释放 RespawnPreparing 恢复可见——避免复活瞬间闪出"死亡前的 ADS/开火姿态"。
        /// 只释放自己的原因位，绝不动 ScopeOverlay（ADS 中死亡→按住 ADS 复活时镜内隐藏保持）。
        /// </summary>
        public void ApplyRespawnState()
        {
            _respawnGeneration++;
            ApplyRespawnStateToActiveView();
            _visibility.SetHidden(FPViewHideReason.Death, false);
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                // 组件已被禁用/对象离场：没有协程可依赖，直接按基线还原（不得残留隐藏）
                _visibility.SetHidden(FPViewHideReason.RespawnPreparing, false);
                return;
            }
            _visibility.SetHidden(FPViewHideReason.RespawnPreparing, true);
            StartCoroutine(ShowViewmodelWhenReady(_respawnGeneration));
        }

        private System.Collections.IEnumerator ShowViewmodelWhenReady(int generation)
        {
            yield return null; // 让 Animancer 在本帧评估出新姿态后再显示
            if (generation != _respawnGeneration) yield break; // 期间又死亡/切枪/禁用：过期恢复作废
            _visibility.SetHidden(FPViewHideReason.RespawnPreparing, false);
        }

        private void ApplyDeathStateToActiveView()
        {
            if (_activeView == null) return;
            if (_activeView.TryGetComponent(out FPWeaponAnimator animator)) animator.ApplyDeathState();
            // 审计 2026-09-16 §3.3-6（实施核实）：FPWeaponMotion 挂在 **viewRoot（FP_Weapon_Root）**，
            // 不在视图实例上——按 _activeView.TryGetComponent 永远落空，运行态缓存清理从未生效。
            // 视图实例是 viewRoot 的子物体，rig(Main Camera) 用 GetComponentInChildren 覆盖两条链。
            if (_weaponMotion == null) _weaponMotion = GetComponentInChildren<Game.Presentation.Camera.FPWeaponMotion>(true);
            if (_weaponMotion != null) _weaponMotion.ApplyDeathState();
        }

        private void ApplyRespawnStateToActiveView()
        {
            if (_activeView == null) return;
            if (_activeView.TryGetComponent(out FPWeaponAnimator animator)) animator.ApplyRespawnState();
            if (_weaponMotion == null) _weaponMotion = GetComponentInChildren<Game.Presentation.Camera.FPWeaponMotion>(true);
            if (_weaponMotion != null) _weaponMotion.ApplyRespawnState();
        }

        /// <summary>镜内遮罩原因位（OpticAdsView 专用；与死亡/复活准备互不影响）。</summary>
        public void SetScopeOverlayHidden(bool hidden)
            => _visibility.SetHidden(FPViewHideReason.ScopeOverlay, hidden);

        /// <summary>注册一个 Renderer 到受控基线（视图创建/动态配件挂载时调用；重复注册安全）。</summary>
        public void RegisterViewRenderer(Renderer renderer) => _visibility.Register(renderer);

        public void SetThrowableRendererHidden(Renderer renderer, bool hidden)
            => _visibility.SetThrowableHidden(renderer, hidden);


        private GameObject GetOrCreateView(WeaponDefinition definition)
        {
            if (_views.TryGetValue(definition, out var view) && view != null)
                return view;

            var prefab = definition.FirstPersonViewPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[FPWeaponRig] WeaponDefinition '{definition.name}' 未配置 FirstPersonViewPrefab。", this);
                return null;
            }

            view = Instantiate(prefab, viewRoot);
            view.transform.localPosition = Vector3.zero;
            view.transform.localRotation = Quaternion.identity;
            view.name = prefab.name;
            var fpLayer = LayerMask.NameToLayer("FirstPersonView");
            if (fpLayer >= 0) SetLayerRecursive(view.transform, fpLayer);
            _views[definition] = view;
            view.SetActive(false);
            return view;
        }

        /// <summary>
        /// Removes only direct-child WeaponViews that are not present in the rig's
        /// runtime cache. A cached inactive view is always retained, so repeated
        /// ShowView calls cannot delete a legitimate switch-cache entry.
        /// </summary>
        private void PurgeUnmanagedSerializedViews()
        {
            if (viewRoot == null) return;
            var candidates = viewRoot.GetComponentsInChildren<Game.Presentation.Weapon.WeaponView>(true);
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.transform.parent != viewRoot) continue;
                if (IsManagedView(candidate.gameObject)) continue;
                Debug.LogWarning($"[FPWeaponRig] Removing unmanaged serialized FP view '{candidate.name}' from {viewRoot.name}.", this);
                if (Application.isPlaying) Destroy(candidate.gameObject);
                else DestroyImmediate(candidate.gameObject);
            }
        }

        private void DeactivateUnmanagedViewsExcept(GameObject keep)
        {
            if (viewRoot == null) return;
            foreach (var candidate in viewRoot.GetComponentsInChildren<Game.Presentation.Weapon.WeaponView>(true))
            {
                if (candidate == null || candidate.gameObject == keep
                    || candidate.transform.parent != viewRoot) continue;
                candidate.gameObject.SetActive(false);
            }
        }

        private bool IsManagedView(GameObject candidate)
        {
            if (candidate == null) return false;
            foreach (var managed in _views.Values)
                if (managed == candidate) return true;
            return false;
        }
    }
}
