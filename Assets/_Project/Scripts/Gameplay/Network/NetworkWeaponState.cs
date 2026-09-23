using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Weapon;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 远端玩家的武器状态同步（Docs/19 N2）：
    /// - 服务器广播 weaponId（SyncVar）：远端 EquipDefinition 切换 TP 持枪（WeaponController
    ///   在远端只作表现数据源——processLocalInput 关闭，Runtime 不 Tick 开火）；
    /// - 开火/换弹动画事件（ObserversRpc）：远端 TPAnimDriver 的上层动作层播放；
    /// - Owner 侧：本地事件照常（本地 TP 动画走既有 PlayerStateView 链路）。
    /// N3 将把开火升级为服务器权威（FireRequest→验证→Raycast→广播）；当前 N2 只同步表现。
    /// </summary>
    public sealed class NetworkWeaponState : NetworkBehaviour
    {
        private readonly SyncVar<string> _weaponId = new();
        // A single SyncVar is intentional: current/reserve, weapon/life identity and ACK
        // sequence must arrive as one observation. Separate ints can transiently manufacture
        // ammunition (the observed 0/30 -> 30/15 failure).
        private readonly SyncVar<AuthoritativeAmmoSnapshot> _ammoSnapshot = new();
        private uint _nextAmmoSnapshotSequence;
        // 服务器权威两槽（2026-09-08 P0 §6 二.4）：服务器 OnStartServer 从已按账号配装配置好的
        // Arsenal 读出并广播——Owner/Observer 各端把本地副本复刻为同两槽（Owner 只显示/切换
        // 这两槽；远端 ApplyWeapon 按 weaponId 命中）。调试 Host（十槽）不写——空串=本地保留。
        private readonly SyncVar<string> _primaryWeaponId = new();
        private readonly SyncVar<string> _secondaryWeaponId = new();
        // Gate A-2（2026-09-08 P0 追加复审 §1.2）：权威附件快照——服务器把票据配装按槽位压缩
        // 广播（AttachmentSnapshotCodec 编码），客户端 FP/TP 表现只消费该快照；""=快照不在位
        //（调试 Host 十槽/离线）→ 客户端回退本机 WeaponAttachmentStore，离线单人行为不变。
        private readonly SyncVar<string> _primaryAttachments = new();
        private readonly SyncVar<string> _secondaryAttachments = new();

        private WeaponController _controller;
        private Arsenal _arsenal;

        private void Awake()
        {
            _controller = GetComponent<WeaponController>();
            _arsenal = GetComponentInParent<Arsenal>();
        }

        public override void OnStartServer()
        {
            // 服务器采集初始武器 + 订阅后续切换与开火/换弹事件（服务器上每个玩家的
            // WeaponController 都在跑——Host 玩家本地开火即服务器事件；远端玩家的
            // 开火在 N3 之前无本地结算，N3 经 FireRequest 服务器验证后才产生事件）
            if (_controller != null)
            {
                _weaponId.Value = _controller.Definition != null ? _controller.Definition.WeaponId : string.Empty;
                _controller.OnWeaponEquipped += HandleServerWeaponEquipped;
                _controller.OnShotFired += HandleServerShotFired;
                _controller.OnReloadStarted += HandleServerReloadStarted;
                // 弹药权威采集：初始值 + 后续变化（Docs/23 P0-3）
                PublishAuthoritativeAmmoSnapshot(0u);
                _controller.OnAmmoChanged += HandleServerAmmoChanged;
            }

            // 权威两槽广播（§6 二.4）：PlayerNetworkAdapter.OnStartServer 已先按账号权威配装
            // 配置 Arsenal（prefab 组件序：Adapter 先于本组件）——此处读其槽位定义广播。
            // 广播 CatalogItemId（后端目录键，如 weapon.rifle03）而非 WeaponId（definitionId）——
            // 客户端 NetworkPlayerLoadoutApplier 按 itemId 查 WeaponAssetCatalog（TryGet 语义）。
            // 调试 Host/旁路（十槽）不写——空串=客户端保留本地槽位（服务器仍权威验证切槽）。
            if (_arsenal != null && _arsenal.SlotCount == 2)
            {
                _primaryWeaponId.Value = _arsenal.Slots[0] != null ? _arsenal.Slots[0].CatalogItemId : string.Empty;
                _secondaryWeaponId.Value = _arsenal.Slots[1] != null ? _arsenal.Slots[1].CatalogItemId : string.Empty;
                Debug.Log($"[NetworkWeaponState] AUTHORITATIVE_SLOTS server weapon={NetworkObject?.Owner?.ClientId} primary={_primaryWeaponId.Value} secondary={_secondaryWeaponId.Value}");

                // Gate A-2：权威附件快照广播（同依赖：Adapter.OnStartServer 已先写入
                // AuthoritativeLoadout）。票据配装在位才写——调试 Host/无档案路径留空 =
                // 客户端回退本机存储（离线/调试行为不变）。
                var adapter = GetComponentInParent<PlayerNetworkAdapter>();
                var loadout = adapter != null ? adapter.AuthoritativeLoadout : null;
                if (loadout != null)
                {
                    _primaryAttachments.Value = AttachmentSnapshotCodec.EncodeForSlot(loadout, NetworkLoadoutPolicy.SlotName(0));
                    _secondaryAttachments.Value = AttachmentSnapshotCodec.EncodeForSlot(loadout, NetworkLoadoutPolicy.SlotName(1));
                }
            }
        }

        public override void OnStartClient()
        {
            // 权威两槽复刻（全端，2026-09-08 §6 二.4）：SyncVar 初值在生成时已到位（早于
            // Arsenal.Start 首装）——按服务器当前武器对齐初始槽位索引后配置本地副本两槽。
            TryApplyAuthoritativeSlots();

            if (!IsOwner)
            {
                // 远端：禁本地输入处理 + 应用当前值
                if (_controller != null)
                {
                    var flag = _controller.GetType().GetField("processLocalInput",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (flag != null) flag.SetValue(_controller, false);
                }
                ApplyWeapon(_weaponId.Value, "spawn");
            }
            // Owner 一并订阅权威变化（修正分支终于接通 §6 二.4）：本地切枪预测照旧，
            // 服务器广播的权威武器与本地不一致（初始错位/非法切枪被拒）时把 Owner 拉回权威武器
            _weaponId.OnChange += HandleWeaponChanged;
            _ammoSnapshot.OnChange += HandleAmmoSnapshotChanged;
            if (IsOwnerPlayerSafe)
            {
                ResolveCombatAuthority()?.ObserveOwnerAmmoLifeEpoch(_ammoSnapshot.Value.LifeEpoch);
                _controller?.ApplyAuthoritativeAmmoSnapshot(_ammoSnapshot.Value);
            }
            // 审计 2026-09-15 §3.4：两槽初值可能晚于 OnStartClient 到达（生成/池化时序）——
            // 变化时幂等重试权威两槽复刻（已配置同两槽时为空操作，不会打断对局中状态）。
            _primaryWeaponId.OnChange += HandleAuthoritativeSlotsChanged;
            _secondaryWeaponId.OnChange += HandleAuthoritativeSlotsChanged;
        }

        public override void OnStopClient()
        {
            // 生成/池化复用边界退订，防止重复订阅叠加处理器
            _weaponId.OnChange -= HandleWeaponChanged;
            _ammoSnapshot.OnChange -= HandleAmmoSnapshotChanged;
            _primaryWeaponId.OnChange -= HandleAuthoritativeSlotsChanged;
            _secondaryWeaponId.OnChange -= HandleAuthoritativeSlotsChanged;
        }

        private void HandleAuthoritativeSlotsChanged(string prev, string next, bool asServer)
        {
            if (asServer) return;
            TryApplyAuthoritativeSlots();
        }

        /// <summary>权威两槽初值（供 HUD/诊断只读）。</summary>
        public string PrimaryWeaponId => _primaryWeaponId.Value;
        public string SecondaryWeaponId => _secondaryWeaponId.Value;

        /// <summary>
        /// 当前武器定义 → 权威槽位索引（CatalogItemId 与服务器广播的两槽比对；不属于任一槽 = -1）。
        /// internal 纯函数供 EditMode 断言槽位映射语义。
        /// </summary>
        internal static int ResolveSlotIndex(string primaryItemId, string secondaryItemId, string catalogItemId)
        {
            if (string.IsNullOrEmpty(catalogItemId)) return -1;
            if (string.Equals(catalogItemId, primaryItemId, System.StringComparison.Ordinal)) return 0;
            if (string.Equals(catalogItemId, secondaryItemId, System.StringComparison.Ordinal)) return 1;
            return -1;
        }

        /// <summary>
        /// 权威附件解析（Gate A-2）：把 definition 所属槽位的附件快照解析为目录条目（FP/TP
        /// 表现装配入口）。返回 false = 快照不在位（离线/调试 Host/定义不属于权威两槽）→
        /// 调用方回退本机 WeaponAttachmentStore；true = 快照在位（entries 可为空 = 权威空装配）。
        /// 只驱动表现，服务器仍是数值权威；晚加入观察者经 SyncVar 生成期初值天然覆盖。
        /// </summary>
        public bool TryGetAuthoritativeAttachments(WeaponDefinition definition, List<AttachmentAssetEntry> entries)
        {
            if (definition == null || entries == null) return false;
            entries.Clear();
            int slot = ResolveSlotIndex(_primaryWeaponId.Value, _secondaryWeaponId.Value, definition.CatalogItemId);
            if (slot < 0) return false;
            bool resolved = AttachmentSnapshotCodec.TryResolveEntries(
                slot == 0 ? _primaryAttachments.Value : _secondaryAttachments.Value, entries, logContext: this);
            if (resolved)
                AttachmentCompatibilityPolicy.RemoveUnsupported(definition, entries);
            return resolved;
        }

        /// <summary>把本地副本 Arsenal 复刻为服务器权威两槽（全端，OnStartClient 时机——早于 Start 首装）。
        /// primary/secondary=服务器广播的 CatalogItemId；服务器当前武器（definitionId）用于初始槽位对齐
        ///（重连瞬间持副武器则初始装副）。空串（调试 Host 十槽）或客户端解析失败 → 保留本地槽位
        ///（服务器仍权威验证切槽）。</summary>
        private void TryApplyAuthoritativeSlots()
        {
            if (_arsenal == null) _arsenal = GetComponentInParent<Arsenal>();
            if (_arsenal == null) return;
            string primary = _primaryWeaponId.Value;
            string secondary = _secondaryWeaponId.Value;
            if (string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(secondary)) return;
            // 幂等闸（审计 2026-09-15 §3.4）：本地副本已是同两槽时不得重跑 ConfigureSlots——
            // 它会清 ActiveIndex 并重选初始槽，等同对局中强制切回主武器。
            if (AuthoritativeSlotsApplied(primary, secondary)) return;
            bool applied = NetworkLoadoutPolicy.TryApplyClientSlots(_arsenal, primary, secondary, _weaponId.Value);
            Debug.Log($"[NetworkWeaponState] AUTHORITATIVE_SLOTS_APPLY applied={applied} primary={primary} secondary={secondary} current={_weaponId.Value} owner={IsOwnerPlayerSafe}", this);
        }

        /// <summary>本地副本 Arsenal 是否已配置为权威两槽（幂等判定；CatalogItemId 逐一比对）。</summary>
        private bool AuthoritativeSlotsApplied(string primaryItemId, string secondaryItemId)
        {
            if (_arsenal.SlotCount != 2) return false;
            var slots = _arsenal.Slots;
            return slots[0] != null && slots[1] != null
                && string.Equals(slots[0].CatalogItemId, primaryItemId, System.StringComparison.Ordinal)
                && string.Equals(slots[1].CatalogItemId, secondaryItemId, System.StringComparison.Ordinal);
        }

        private void OnDestroy()
        {
            if (_controller == null) return;
            // 退订未注册的事件同样安全；不要在离线对象销毁阶段读取 FishNet 状态，
            // 此时 NetworkBehaviour 的 NetworkObject 缓存可能尚未绑定或已经释放。
            _controller.OnWeaponEquipped -= HandleServerWeaponEquipped;
            _controller.OnShotFired -= HandleServerShotFired;
            _controller.OnReloadStarted -= HandleServerReloadStarted;
            _controller.OnAmmoChanged -= HandleServerAmmoChanged;
        }

        private void HandleServerAmmoChanged(int current, int reserve)
        {
            if (IsServerInitialized)
                PublishAuthoritativeAmmoSnapshot(ResolveCombatAuthority()?.LastProcessedShotRequestId ?? 0u);
        }

        /// <summary>服务器权威弹药快照（Owner 会先回写 Runtime，HUD 只读 Runtime）。</summary>
        public AuthoritativeAmmoSnapshot AmmoSnapshot => _ammoSnapshot.Value;
        public int CurrentAmmo => _ammoSnapshot.Value.CurrentAmmo;
        /// <summary>服务器权威备弹。</summary>
        public int ReserveAmmo => _ammoSnapshot.Value.ReserveAmmo;

        private NetworkCombatAuthority ResolveCombatAuthority()
            => GetComponent<NetworkCombatAuthority>();

        /// <summary>Server-only producer. Every runtime mutation (fire, reload completion,
        /// respawn) calls this through OnAmmoChanged; callers with a fire ACK pass its id.
        /// The returned value is suitable for the immediate TargetRpc fast path.</summary>
        internal AuthoritativeAmmoSnapshot PublishAuthoritativeAmmoSnapshot(uint lastProcessedShotRequestId)
        {
            if (!IsServerInitialized || _controller == null || _controller.Runtime == null || _controller.Definition == null)
                return default;
            var authority = ResolveCombatAuthority();
            var snapshot = new AuthoritativeAmmoSnapshot
            {
                WeaponId = _controller.Definition.WeaponId,
                LifeEpoch = authority != null ? authority.CurrentLifeEpoch : 0u,
                Sequence = ++_nextAmmoSnapshotSequence,
                LastProcessedShotRequestId = lastProcessedShotRequestId,
                CurrentAmmo = _controller.Runtime.CurrentAmmo,
                ReserveAmmo = _controller.Runtime.ReserveAmmo,
                ReloadState = _controller.Runtime.State,
                ReloadRemaining = _controller.Runtime.ReloadRemaining
            };
            _ammoSnapshot.Value = snapshot;
            return snapshot;
        }

        private void HandleAmmoSnapshotChanged(AuthoritativeAmmoSnapshot previous,
            AuthoritativeAmmoSnapshot next, bool asServer)
        {
            if (asServer || !IsOwnerPlayerSafe) return;
            ResolveCombatAuthority()?.ObserveOwnerAmmoLifeEpoch(next.LifeEpoch);
            _controller?.ApplyAuthoritativeAmmoSnapshot(next);
        }

        /// <summary>离线安全的本地所有权判定（HUD 消费入口）：authored player 被
        /// SetIsNetworked(false) 后所有权缓存未建立，直接读 FishNet IsOwner 会 NRE
        /// （Docs/23 离线回归修复）——统一走 FishNetLifecycleGuard。</summary>
        public bool IsOwnerPlayerSafe => FishNetLifecycleGuard.IsLocalOwner(this);

        private void HandleServerWeaponEquipped(WeaponDefinition def)
        {
            if (IsServerInitialized)
                _weaponId.Value = def != null ? def.WeaponId : string.Empty;
        }

        private void HandleServerShotFired(WeaponShot _) => BroadcastFire();
        private void HandleServerReloadStarted() => BroadcastReload();

        private void HandleWeaponChanged(string prev, string next, bool asServer)
        {
            if (asServer) return;
            if (!IsOwner)
            {
                ApplyWeapon(next, "authoritativeChange");
                return;
            }
            // Owner 修正分支（Docs/23 P0-2）：服务器广播的权威武器与本地不一致（非法切枪
            // 被服务器拒绝后的下一次权威变化、或双端竞态）→ 把 Owner 拉回权威武器
            if (_controller != null && _controller.Definition != null && _controller.Definition.WeaponId != next)
                ApplyWeapon(next, "ownerCorrection");
        }

        /// <summary>按 weaponId 查 Arsenal 槽位并 EquipDefinition（表现数据源）。
        /// 审计 2026-09-15 §3：装备后必须对齐 Arsenal.ActiveIndex 并广播 OnActiveWeaponChanged——
        /// 否则弹道/弹药走新枪而 ActiveIndex/FP·TP·HUD·音频仍停在旧枪（装备状态分叉通道）。
        /// slots 为私有字段——反射读取（NetworkAdapter 同层的反向依赖规避惯例）。</summary>
        private void ApplyWeapon(string weaponId, string reason)
        {
            if (_controller == null || string.IsNullOrEmpty(weaponId)) return;
            if (_arsenal != null)
            {
                var slotsField = _arsenal.GetType().GetField("slots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var slots = slotsField?.GetValue(_arsenal) as WeaponDefinition[];
                if (slots != null)
                {
                    foreach (var def in slots)
                    {
                        if (def != null && def.WeaponId == weaponId)
                        {
                            _controller.EquipDefinition(def);
                            _arsenal.AlignToEquippedDefinition(def);
                            Debug.Log($"[NetworkWeaponState] AUTHORITATIVE_WEAPON_APPLY reason={reason} weaponId={weaponId} itemId={def.CatalogItemId} activeIndex={_arsenal.ActiveIndex} owner={IsOwnerPlayerSafe}", this);
                            return;
                        }
                    }
                }
            }
            Debug.LogWarning($"[NetworkWeaponState] 远端槽位未找到 weaponId={weaponId} reason={reason}", this);
        }

        // ---- 开火/换弹动画事件广播（N2：表现层；N3 升级为权威结算） ----

        /// <summary>服务器/Owner 调用：广播开火动画事件（远端 TP 上身动作）。</summary>
        public void BroadcastFire()
        {
            if (IsServerInitialized) ObserversFire();
        }

        /// <summary>服务器/Owner 调用：广播换弹动画事件。</summary>
        public void BroadcastReload()
        {
            if (IsServerInitialized) ObserversReload();
        }

        [ObserversRpc(ExcludeOwner = true, ExcludeServer = false, RunLocally = false)]
        private void ObserversFire()
        {
            // 远端 TP：TPAnimDriver 已订阅 _controller.OnShotFired——直接触发同链路
            _controller?.InvokeRemoteFireForPresentation();
        }

        [ObserversRpc(ExcludeOwner = true, ExcludeServer = false, RunLocally = false)]
        private void ObserversReload()
        {
            _controller?.InvokeRemoteReloadForPresentation();
        }
    }
}
