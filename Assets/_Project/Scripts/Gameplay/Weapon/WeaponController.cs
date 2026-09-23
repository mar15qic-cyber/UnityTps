using System;
using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 单发开火载荷：全部表现（Tracer/火光/TP/动画/准心 Bloom/音频）消费同一份。
    /// CP4 扩展（Docs/13 §5.2）：FinalSpreadDegrees/Recoil/ShotIndex/Seed/Pellets。
    /// Pellets：单发武器为 null；Shotgun 每次开火独立分配（上限 16，不池化——readonly 载荷内
    /// 复用数组会让订阅者读到被改写数据，§5.3-10）。主 Result=首个 Damaged，否则首个 Hit，
    /// 否则主 pellet 未命中终点。
    /// </summary>
    public readonly struct WeaponShot
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        /// <summary>本发的实际弹道方向（散布后的 mainDirection；霰弹=弹丸锥主方向）。
        /// Day4 实机审计 §1：拖尾必须从枪口沿本方向延伸——Direction 是散布前瞄准方向，
        /// 两者不可混用（沿 Direction 画拖尾会与真实弹道平行偏移）。</summary>
        public readonly Vector3 FiredDirection;
        public readonly HitscanResult Result;
        public readonly float FinalSpreadDegrees;      // 本发合成散布锥角（不含 PelletSpread）
        public readonly ShotRecoilResult Recoil;       // 本发后坐（相机回声/Viewmodel/Shake 同源）
        public readonly int ShotIndex;                 // burst 内 0 基序号
        public readonly int Seed;                      // 随机种子快照（网络回放预留）
        public readonly HitscanResult[] Pellets;       // null=单发；Shotgun=全弹丸结果
        public readonly float Ads01;

        public WeaponShot(Vector3 origin, Vector3 direction, HitscanResult result)
            : this(origin, direction, direction, result, 0f, default, 0, 0, null) { }

        public WeaponShot(Vector3 origin, Vector3 direction, Vector3 firedDirection, HitscanResult result,
            float finalSpreadDegrees, ShotRecoilResult recoil, int shotIndex, int seed,
            HitscanResult[] pellets, float ads01 = 0f)
        {
            Origin = origin;
            Direction = direction;
            FiredDirection = firedDirection;
            Result = result;
            FinalSpreadDegrees = finalSpreadDegrees;
            Recoil = recoil;
            ShotIndex = shotIndex;
            Seed = seed;
            Pellets = pellets;
            Ads01 = ads01;
        }
    }

    /// <summary>武器运行时的唯一写者。只发 gameplay 事件，不操作 Animator、特效或 HUD。</summary>
    /// <remarks>
    /// 瞄准权威（Docs/13 §5.3-1）：FireRay 与相机中心射线同源同线——AimOrigin=CameraPivot；
    /// AimDirection=pivot×WeaponRecoilState.CurrentOffset（弹簧唯一存在处，相机仅回声）。
    /// CP4：五步顺序消费 ResolvedWeaponStats + WeaponFireContext；散布走 WeaponAccuracyState
    /// 动态合成（腰射/ADS/移动/冲刺/Bloom）；Shotgun 多弹丸聚合后单次广播；随机源可播种。
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(ActionSystem), typeof(CombatResolver))]
    public sealed class WeaponController : MonoBehaviour
    {
        [SerializeField] private WeaponDefinition definition;
        [SerializeField] private ScriptableObject balanceConfigAsset;
        [SerializeField] private InputReader input;
        [SerializeField] private ActionSystem actionSystem;
        [SerializeField] private CombatResolver combatResolver;
        [Tooltip("瞄准权威挂点（CameraPivot，头部 y=1.62）。射线原点与前向基准取自它，而非最终相机。")]
        [SerializeField] private Transform aimPivot;
        [Tooltip("开火情境快照提供者（PlayerAimState+Locomotor 聚合）；空=Default（静止腰射）")]
        [SerializeField] private WeaponFireContextProvider fireContextProvider;
        [SerializeField] private LayerMask hitMask = ~0;
        [SerializeField] private bool processLocalInput = true;
        [Header("调试")]
        [Tooltip("开发期后坐诊断日志（每发 Pitch/Yaw/ShotIndex/ADS 倍率/当前 Offset）。正式构建必须关闭")]
        [SerializeField] private bool debugRecoil;

        public WeaponDefinition Definition => definition;
        public WeaponRuntime Runtime { get; private set; }
        public WeaponStat Stat { get; private set; }
        public ActionSystem Actions => actionSystem;
        public bool IsInitialized => Runtime != null;

        /// <summary>当前装配是否含消音器（isSuppressor 配件）——WeaponAudioView 据此切换消音 Fire 池。</summary>
        public bool IsSuppressed
        {
            get
            {
                var equipped = _attachmentSource.Equipped;
                for (int i = 0; i < equipped.Count; i++)
                    if (equipped[i] != null && equipped[i].isSuppressor) return true;
                return false;
            }
        }

        /// <summary>当前瞄具开镜情境：已装瞄具优先，否则使用武器自带瞄具，再否则为机瞄。</summary>
        public OpticAimContext CurrentOpticAim => definition != null
            ? OpticAimContext.Resolve(_attachmentSource.Equipped, definition.BuiltInOptic)
            : OpticAimContext.None;

        /// <summary>解析后数值（唯一持有者；Initialize/EquipDefinition 重算，CP4 起为消费源）。</summary>
        public ResolvedWeaponStats Resolved { get; private set; }
        /// <summary>当前瞄准偏移（度；Pitch 向上为正、Yaw 向右为正）。CmFPCameraRecoil 回声与 FireRay 共用。</summary>
        public Vector2 CurrentRecoilOffset => _recoil.CurrentOffset;
        public Quaternion CurrentRecoilRotation => _recoil.OffsetRotation;
        /// <summary>权威射线原点：CameraPivot 头位。</summary>
        public Vector3 AimOrigin
        {
            get
            {
                Vector3 neutral = aimPivot != null ? aimPivot.position : transform.position;
                var locomotor = GetComponentInParent<Game.Gameplay.Movement.Locomotor>();
                return locomotor != null
                    ? neutral + transform.root.right * (locomotor.Lean.Amount * Game.Gameplay.Player.LeanProfile.EyeSideMeters)
                    : neutral;
            }
        }
        /// <summary>权威瞄准方向：pivot 旋转 × 后坐偏移。</summary>
        public Vector3 AimDirection => (aimPivot != null ? aimPivot.rotation : transform.rotation)
            * _recoil.OffsetRotation * Vector3.forward;

        /// <summary>
        /// 让玩家输入优先抵消后坐债务；返回仍应写入基础视角的剩余“向上/向右”角度。
        /// </summary>
        public Vector2 ConsumeRecoilCompensation(Vector2 requestedAimDeltaDeg)
            => _recoil.ConsumeCompensation(requestedAimDeltaDeg);

        /// <summary>后坐补偿债务（度）——移动快照携带，保证预测重放从同一起点消费（审计 2026-09-16 M3）。</summary>
        public Vector2 RecoilCompensationDebt => _recoil.CompensationDebt;

        /// <summary>恢复后坐补偿债务（权威快照对位/重放前调用）。</summary>
        public void RestoreRecoilCompensationDebt(Vector2 debt) => _recoil.RestoreCompensationDebt(debt);

        /// <summary>当前合成散布锥角（度）——弹道与准心 HUD 的同一数据源。</summary>
        public float CurrentSpreadDegrees => _accuracy.CurrentSpread(FireContext, Resolved);

        /// <summary>
        /// 廉价准入预检（审计 2026-09-16 §6.4）：回答"现在开火会不会被冷却/弹药/动作槽拒绝"，
        /// **不消费任何状态、不做任何射线**（镜像 WeaponRuntime.TryConsumeRound 的三条前置）。
        /// 用途=服务器在处理开火请求前先排除必被拒绝的请求，避免为它们付出 hitbox 回溯代价
        /// （回溯要临时移动全部玩家 hitbox 并 SyncTransforms，逐渲染帧 FireHeld 时开销可观）。
        /// </summary>
        public bool CanAttemptFire
            => Runtime != null && !actionSystem.IsBusy
               && Runtime.State == WeaponRuntimeState.Ready
               && Runtime.CooldownRemaining <= 0f
               && Runtime.HasAmmo;

        /// <summary>最近一次开火的权威几何证据（审计 §6.1；服务器侧有意义，离线/客户端为 default）。</summary>
        public Combat.FireEvidence LastFireEvidence
            => combatResolver != null ? combatResolver.LastTwoStageEvidence : default;

        public event System.Action<WeaponShot> OnShotFired;
        public event System.Action OnDryFire;
        public event System.Action<int, int> OnAmmoChanged;
        public event System.Action OnReloadStarted;
        public event System.Action OnReloadCompleted;
        public event System.Action<ActionInterruptReason> OnReloadInterrupted;
        public event System.Action<WeaponDefinition> OnWeaponEquipped;
        public event System.Action<OpticAimContext> OnAttachmentsChanged;

        /// <summary>远端表现触发（Docs/19 N2，NetworkWeaponState RPC 调用）：
        /// 只广播动画事件链，不结算弹道/弹药（服务器权威结算在 N3）。</summary>
        internal void InvokeRemoteFireForPresentation() => OnShotFired?.Invoke(default);
        internal void InvokeRemoteReloadForPresentation() => OnReloadStarted?.Invoke();

        private IBalanceConfig _balance;
        private WeaponRecoilState _recoil = new();        private readonly WeaponAccuracyState _accuracy = new();
        private readonly AttachmentStatModifierSource _attachmentSource = new();   // 配件层（Priority=0，Docs/21 Phase D）
        private System.Random _random = new();     // 可播种（seed=0 随机）；弹道散布唯一随机源
        private int _seed;

        // Day4 残余审计 P0-2：按 WeaponId 索引的弹药持久化缓存——切槽只切换当前运行时引用，
        // 每把武器保留自己服务器权威的弹匣/备弹状态（跨切槽往返/配件容量重算），切回即恢复
        //（按新容量钳制，绝不隐式补满）。_runtimeWeaponId 记录当前 Runtime 归属，切出时写缓存。
        private readonly Dictionary<string, (int currentAmmo, int reserveAmmo)> _ammoCacheByWeaponId = new();
        private readonly PendingShotAmmoLedger _pendingShotAmmo = new();
        private uint _ammoSnapshotEpoch;
        private uint _lastAmmoSnapshotSequence;
        private readonly Dictionary<string, AttachmentAssetEntry[]> _attachmentsByWeaponId = new();
        private string _runtimeWeaponId;

        private WeaponFireContext FireContext
            => fireContextProvider != null ? fireContextProvider.Context : WeaponFireContext.Default;

        private void Awake()
        {
            if (input == null) input = GetComponentInParent<InputReader>();
            if (actionSystem == null) actionSystem = GetComponent<ActionSystem>();
            if (combatResolver == null) combatResolver = GetComponent<CombatResolver>();
            if (fireContextProvider == null) fireContextProvider = GetComponentInParent<WeaponFireContextProvider>();
            if (aimPivot == null)
            {
                // 兜底：CameraPivot 是 Main Camera 的父级（Player prefab 结构）；无相机时退回自身。
                var mainCam = UnityEngine.Camera.main;
                aimPivot = mainCam != null && mainCam.transform.parent != null
                    ? mainCam.transform.parent
                    : transform;
            }
            _balance = balanceConfigAsset as IBalanceConfig;
        }

        private void OnEnable()
        {
            if (actionSystem == null) actionSystem = GetComponent<ActionSystem>();
            actionSystem.OnActionCompleted += HandleActionCompleted;
            actionSystem.OnActionInterrupted += HandleActionInterrupted;
        }

        private void Start()
        {
            if (definition == null)
            {
                Debug.LogError("[WeaponController] Start blocked: WeaponDefinition is not assigned.", this);
                enabled = false;
                return;
            }
            if (!TryResolveBalance(null, out IBalanceConfig resolvedBalance))
            {
                Debug.LogError("[WeaponController] Start blocked: no IBalanceConfig is assigned or resolvable.", this);
                enabled = false;
                return;
            }
            Initialize(definition, resolvedBalance);
        }

        private void OnDisable()
        {
            if (actionSystem == null) return;
            actionSystem.OnActionCompleted -= HandleActionCompleted;
            actionSystem.OnActionInterrupted -= HandleActionInterrupted;
        }

        private void Update()
        {
            if (Runtime == null) return;
            float dt = Time.deltaTime;
            Runtime.Tick(dt);
            _recoil.Tick(dt, Resolved);
            _accuracy.Tick(dt, Resolved);
            if (Runtime.State == WeaponRuntimeState.Reloading)
                Runtime.SyncReloadRemaining(actionSystem.Remaining);

            if (!processLocalInput || input == null || input.WeaponInputBlocked) return;
            bool wantsFire = definition.FireMode == WeaponFireMode.Automatic ? input.FireHeld : input.FirePressed;
            if (wantsFire) TryFire();
            if (input.ReloadPressed) TryReload();
        }

        public void Initialize(WeaponDefinition weaponDefinition, IBalanceConfig balance)
        {
            if (weaponDefinition == null) throw new ArgumentNullException(nameof(weaponDefinition));
            if (!TryResolveBalance(balance, out IBalanceConfig resolvedBalance))
            {
                Debug.LogError($"[WeaponController] Initialize blocked for '{weaponDefinition.name}': no IBalanceConfig is assigned or resolvable.", this);
                return;
            }

            // Resolve all prerequisites before replacing the current definition/runtime;
            // a failed equip must leave the previous weapon state intact.
            definition = weaponDefinition;
            _balance = resolvedBalance;
            Stat = _balance.GetWeaponStat(definition.WeaponId);
            RebuildResolvedStats();
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        private bool TryResolveBalance(IBalanceConfig requested, out IBalanceConfig resolved)
        {
            resolved = requested ?? _balance;
            if (resolved == null && balanceConfigAsset is IBalanceConfig serializedBalance)
                resolved = serializedBalance;
            if (resolved != null) _balance = resolved;
            return resolved != null;
        }

        /// <summary>
        /// 整体替换装配配件集（枪匠保存后 / 换武器重套配装时调用）。
        /// 重算解析数值并以新弹匣容量重建运行时（调用时机=装备期，非战斗中途——满弹重建语义正确）。
        /// </summary>
        public void SetAttachments(IEnumerable<AttachmentAssetEntry> attachments)
        {
            var compatible = attachments != null
                ? new List<AttachmentAssetEntry>(attachments)
                : new List<AttachmentAssetEntry>();
            AttachmentCompatibilityPolicy.RemoveUnsupported(definition, compatible);
            _attachmentSource.Reset(compatible);
            if (definition != null)
                _attachmentsByWeaponId[definition.WeaponId] = new List<AttachmentAssetEntry>(_attachmentSource.Equipped).ToArray();
            RebuildResolvedStats();
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
            OnAttachmentsChanged?.Invoke(CurrentOpticAim);
        }

        /// <summary>当前装配的配件（只读；FP/TP 表现层挂模型用）.</summary>
        public IReadOnlyList<AttachmentAssetEntry> EquippedAttachments => _attachmentSource.Equipped;

        private void RebuildResolvedStats()
        {
            // Day4 残余审计 P0-2：重建前把当前运行时弹药记入按 WeaponId 索引的缓存；
            // 重建后同武器恢复（按新弹匣容量钳制——配件增减容量不得隐式补满/清零），
            // 不同武器/首次装备 = 满弹新运行时（出生语义不变）。换弹态随切枪取消，不赠弹。
            if (Runtime != null && !string.IsNullOrEmpty(_runtimeWeaponId))
                _ammoCacheByWeaponId[_runtimeWeaponId] = (Runtime.CurrentAmmo, Runtime.ReserveAmmo);

            Resolved = WeaponStatResolver.Resolve(Stat,
                _attachmentSource.Equipped.Count > 0 ? new List<IWeaponStatModifierSource> { _attachmentSource } : null);
            // 弹匣容量可被加长弹匣修饰（数量型），运行时按解析值重建
            Runtime = new WeaponRuntime(Mathf.Max(1, Mathf.RoundToInt(Resolved.MagazineSize)), Stat.ReserveAmmo);

            if (definition != null && _ammoCacheByWeaponId.TryGetValue(definition.WeaponId, out var persisted))
                Runtime.RestoreAmmo(persisted.currentAmmo, persisted.reserveAmmo);
            _runtimeWeaponId = definition != null ? definition.WeaponId : null;
        }

        /// <summary>
        /// 服务器重生弹药重置（2026-09-18 实机问题8）：弹匣补满+备弹回到配装初始值，
        /// 切枪弹药缓存清空（否则切回该枪会读回死亡前的残弹）；换弹/冷却态随 RestoreAmmo 复位。
        /// 仅服务器权威调用（NetworkCombatAuthority.ServerRespawn）；客户端经 SyncVar 同步。
        /// </summary>
        internal void ServerResetAmmoToLoadoutDefault()
        {
            if (Runtime == null) return;
            _ammoCacheByWeaponId.Clear();
            Runtime.RestoreAmmo(Runtime.MagazineSize, Stat.ReserveAmmo);
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        /// <summary>
        /// Owner 本地重生补弹镜像（F12，2026-09-19 审计）：服务器重生已权威重置并经 SyncVar
        /// 下发 HUD，但 Owner 本地预测 Runtime 与两槽缓存仍停在死亡前残弹——HUD 满弹而本地
        /// TryFire 被 TryConsumeRound 拒绝（本地动画/音效缺失，与服务器发次分叉）。两端
        /// definition/balance 同源 → MagazineSize/ReserveAmmo 确定性一致；仅 Owner 客户端调用
        /// （NetworkCombatAuthority.ObserversRespawned；服务器/Host 走 ServerResetAmmoToLoadoutDefault）。
        /// </summary>
        public void OwnerResetAmmoToRespawnBaseline()
        {
            if (Runtime == null) return;
            _ammoCacheByWeaponId.Clear();
            Runtime.RestoreAmmo(Runtime.MagazineSize, Stat.ReserveAmmo);
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        /// <summary>客户端接收服务器弹药快照的唯一回写入口。快照始终按 weaponId 写入
        /// 槽位缓存；当前持枪再同步 Runtime 并广播 HUD。这样服务器拒绝的一发或换弹请求
        /// 不会只改右下角文字而把下一次 TryFire/切枪继续留在旧预测弹药上。</summary>
        public void RegisterPredictedShotForAmmo(uint shotRequestId, uint lifeEpoch)
        {
            if (definition == null) return;
            _pendingShotAmmo.Register(shotRequestId, definition.WeaponId, lifeEpoch);
        }

        /// <summary>Applies one atomic owner snapshot, then replays only locally predicted
        /// shots that the server has not processed yet. Stale, cross-life and cross-weapon
        /// snapshots cannot refill the current Runtime.</summary>
        public void ApplyAuthoritativeAmmoSnapshot(AuthoritativeAmmoSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.WeaponId)) return;
            if (snapshot.LifeEpoch < _ammoSnapshotEpoch) return;
            if (snapshot.LifeEpoch == _ammoSnapshotEpoch && snapshot.Sequence <= _lastAmmoSnapshotSequence) return;

            if (snapshot.LifeEpoch > _ammoSnapshotEpoch)
            {
                _ammoSnapshotEpoch = snapshot.LifeEpoch;
                _lastAmmoSnapshotSequence = 0;
                _pendingShotAmmo.ClearForLife(snapshot.LifeEpoch);
            }
            _lastAmmoSnapshotSequence = snapshot.Sequence;
            int current = Mathf.Max(0, snapshot.CurrentAmmo);
            int reserve = Mathf.Max(0, snapshot.ReserveAmmo);
            // Consume before checking the equipped weapon: an A acknowledgement can arrive
            // while B is equipped, and A's cache must retain A's later local shot debt.
            int pending = _pendingShotAmmo.ConsumeAcknowledgedAndCountRemaining(snapshot.WeaponId,
                snapshot.LifeEpoch, snapshot.LastProcessedShotRequestId);
            int reconciledCurrent = Mathf.Max(0, current - pending);
            _ammoCacheByWeaponId[snapshot.WeaponId] = (reconciledCurrent, reserve);
            if (Runtime == null || definition == null
                || !string.Equals(definition.WeaponId, snapshot.WeaponId, StringComparison.Ordinal)) return;

            Runtime.ReconcileAuthoritativeAmmo(current, reserve, snapshot.ReloadState, snapshot.ReloadRemaining);
            Runtime.ApplyPredictedAmmoDebt(pending);
            _ammoCacheByWeaponId[snapshot.WeaponId] = (Runtime.CurrentAmmo, Runtime.ReserveAmmo);
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        // Kept for existing offline callers/tests. Network code must use the atomic overload.
        public void ApplyAuthoritativeAmmoSnapshot(string weaponId, int currentAmmo, int reserveAmmo)
            => ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = weaponId, CurrentAmmo = currentAmmo, ReserveAmmo = reserveAmmo,
                LifeEpoch = _ammoSnapshotEpoch, Sequence = _lastAmmoSnapshotSequence + 1,
                ReloadState = Runtime != null ? Runtime.State : WeaponRuntimeState.Ready,
                ReloadRemaining = Runtime != null ? Runtime.ReloadRemaining : 0f
            });

        /// <summary>切枪中途换装（Arsenal 在交换点调用）：硬重置运行时并广播，供 FP/TP 表现切换。</summary>
        public void EquipDefinition(WeaponDefinition next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (!TryResolveBalance(null, out IBalanceConfig resolvedBalance))
            {
                Debug.LogError($"[WeaponController] Equip blocked for '{next.name}': no IBalanceConfig is assigned or resolvable.", this);
                return;
            }
            if (Runtime != null && Runtime.State == WeaponRuntimeState.Reloading)
                Runtime.CancelReload();
            // 硬重置（Docs/13 §5.3-4）：仅切枪；停火/换弹自然恢复
            _recoil.HardReset();
            _accuracy.HardReset();
            // 恢复目标枪自己的配件后再恢复弹药，避免扩容弹匣先被基础容量截断。
            // 首次装备仍为空；服务器装备回调继续用权威快照覆盖，不继承上一把枪的配件。
            _attachmentSource.Reset(_attachmentsByWeaponId.TryGetValue(next.WeaponId, out var savedAttachments)
                ? savedAttachments : null);
            Initialize(next, resolvedBalance);
            OnAttachmentsChanged?.Invoke(CurrentOpticAim);
            if (definition == next && Runtime != null)
                OnWeaponEquipped?.Invoke(next);
        }

        /// <summary>注入随机种子（测试/网络回放）；seed=0 恢复随机。</summary>
        public void SetRandomSeed(int seed)
        {
            _seed = seed;
            _random = seed == 0 ? new System.Random() : new System.Random(seed);
        }

        private int _recoilSeedApplied;

        /// <summary>
        /// F13（2026-09-19 审计）：确定性后坐种子。两端（Owner 本地预测 / 服务器权威模拟）
        /// 以同源键（weaponId+ownerClientId+生命代际）派生同一 seed → 同一 yaw 随机流起点；
        /// 旧实现 _recoil = new() 恒走 System.Random() 时间种子，两端流必然分叉。
        /// 同 seed 幂等（换枪/重生重复调用不重置进行中的 stream）；0 = 未应用（WeaponRecoilState
        /// 的 0 参数是"时间随机"语义，拒绝）。注意：接受发次两端分叉（请求被服务器拒绝）时
        /// stream 仍会错位——彻底闭环需要服务器接受序回传（wire 变更，另行设计）。
        /// </summary>
        public void ApplyDeterministicRecoilSeed(int seed)
        {
            if (seed == 0) return;
            if (_recoilSeedApplied == seed) return;
            _recoilSeedApplied = seed;
            _recoil = new WeaponRecoilState(seed);
        }

        /// <summary>测试接缝：读取已应用的后坐种子（0=未应用，仍为时间随机）。</summary>
        internal int RecoilSeedAppliedForTests => _recoilSeedApplied;

        /// <summary>
        /// 执行一次开火（本地预测/离线/服务器三路径共用）。rewindContext：服务器路径由
        /// NetworkCombatAuthority.ServerFireRequest 传入（Phase 2——实际回溯 tick 随行，供命中判定
        /// 按射击时刻快照比对目标生命代际/无敌态）；本地/离线调用省略（default = 无回溯语境）。
        /// </summary>
        private bool _serverAimOverride;
        private Vector3? _serverMuzzleOverride, _serverBodyAnchorOverride;
        private Vector3 _serverAimOrigin, _serverAimDirection;
        private WeaponFireContext? _shotContextOverride;
        private int? _shotSeedOverride;
        private Vector3 _presentedOrigin, _presentedDirection;
        private int _presentedFrame = -10;

        // Presentation publishes the camera actually rendered; Gameplay owns the shot and damage.
        public void SetPresentedAim(Vector3 origin, Vector3 direction)
        {
            _presentedOrigin = origin;
            _presentedDirection = direction.normalized;
            _presentedFrame = Time.frameCount;
        }

        internal bool TryFireWithServerSnapshot(Vector3 origin, Vector3 direction,
            WeaponFireContext fireContext, int seed, LagCompRewindContext context,
            Vector3? historicalMuzzle = null, Vector3? historicalBodyAnchor = null)
        {
            _shotContextOverride = fireContext;
            _shotSeedOverride = seed;
            _serverMuzzleOverride = historicalMuzzle;
            _serverBodyAnchorOverride = historicalBodyAnchor;
            try { return TryFireWithServerAim(origin, direction, context); }
            finally { _shotContextOverride = null; _shotSeedOverride = null;
                _serverMuzzleOverride = null; _serverBodyAnchorOverride = null; }
        }
        internal bool IsPresentedOriginUnobstructed(Vector3 authoritativeOrigin, Vector3 displayedOrigin)
            => combatResolver != null && combatResolver.IsAimOriginUnobstructed(authoritativeOrigin,
                displayedOrigin, hitMask.value, transform.root);

        internal bool TryFireWithServerAim(Vector3 origin, Vector3 direction, LagCompRewindContext context)
        {
            _serverAimOverride = true; _serverAimOrigin = origin; _serverAimDirection = direction;
            try { return TryFire(context); }
            finally { _serverAimOverride = false; }
        }

        public bool TryFire(LagCompRewindContext rewindContext = default)
        {
            if (Runtime == null || actionSystem.IsBusy) return false;
            if (!Runtime.TryConsumeRound())
            {
                if (!Runtime.HasAmmo) OnDryFire?.Invoke();
                return false;
            }

            Runtime.StartCooldown(60f / Mathf.Max(1, Stat.Rpm));

            // 五步顺序（Docs/13 §5.3-5）
            // ① 开火前状态算弹道：权威瞄准 + 动态散布锥（腰射/ADS/移动/冲刺/Bloom 均已合成）
            var ctx = _shotContextOverride ?? FireContext;
            float spreadDeg = _accuracy.CurrentSpread(ctx, Resolved);
            Vector3 origin = _serverAimOverride ? _serverAimOrigin : AimOrigin;
            Vector3 aimDirection = _serverAimOverride ? _serverAimDirection : AimDirection;
            if (!_serverAimOverride && processLocalInput && Time.frameCount - _presentedFrame <= 1)
            {
                origin = _presentedOrigin;
                aimDirection = _presentedDirection;
            }
            int? shotSeed = _shotSeedOverride;
            var networkAuthority = GetComponent<NetworkCombatAuthority>();
            if (!shotSeed.HasValue && FishNetLifecycleGuard.CanSubmitRpc(networkAuthority) && networkAuthority.IsOwnerPlayer
                && !networkAuthority.IsServerInitialized)
                shotSeed = networkAuthority.NextPredictedSpreadSeed;
            var shotRandom = shotSeed.HasValue ? new System.Random(shotSeed.Value) : _random;

            // ② 命中结算（含 Shotgun 多弹丸：主方向一次取样，每弹丸围绕主方向独立 PelletSpread 锥，聚合单次广播）
            // I4a/P4：服务器路径走两段权威命中（相机候选→逻辑枪口遮挡验证→身体锚点防伸墙，同回溯窗口单次伤害）；
            // 客户端预测/离线保持单段相机射线（不改客户端命中权威语义）。
            var serverObject = GetComponentInParent<FishNet.Object.NetworkObject>();
            bool serverTwoStage = serverObject != null && serverObject.IsServerInitialized;
            // F01（2026-09-19 审计）：仅服务器权威结算把射手传入伤害链——击杀归因在
            // DamageableTarget.ApplyDamage 的"伤害实际被结算"点登记（先于 OnDied），
            // 离线/客户端预测传 null 不进注册表；霰弹逐 pellet 传入 → 多目标各归其位。
            var attributionSource = serverTwoStage
                ? GetComponentInParent<Game.Gameplay.Network.NetworkCombatAuthority>()
                : null;
            var locomotorForLean = GetComponentInParent<Game.Gameplay.Movement.Locomotor>();
            float leanForShot = locomotorForLean != null ? locomotorForLean.Lean.Amount : 0f;
            Vector3 logicalMuzzle = _serverMuzzleOverride ?? Game.Gameplay.Player.LeanProfile.Muzzle(
                transform.root.position, transform.root.rotation, leanForShot);
            Vector3 bodyAnchor = _serverBodyAnchorOverride ?? Game.Gameplay.Player.LeanProfile.BodyAnchor(
                transform.root.position, transform.root.rotation, leanForShot);

            HitscanResult[] pellets = null;
            HitscanResult result;
            int pelletCount = Stat.Ballistic.PelletCount;
            Vector3 mainDirection = ApplySpread(aimDirection, spreadDeg, shotRandom);
            if (pelletCount > 1)
            {
                pellets = new HitscanResult[pelletCount];
                HitscanResult? primary = null, firstHit = null;
                for (int i = 0; i < pelletCount; i++)
                {
                    Vector3 dir = ApplySpread(mainDirection, Stat.Ballistic.PelletSpread, shotRandom);
                    pellets[i] = serverTwoStage
                        ? combatResolver.ResolveHitscanTwoStage(
                            origin, dir, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root, logicalMuzzle, bodyAnchor, rewindContext, attributionSource)
                        : combatResolver.ResolveHitscan(
                            origin, dir, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root, attributionSource);
                    if (primary == null && pellets[i].Damaged) primary = pellets[i];
                    if (firstHit == null && pellets[i].Hit) firstHit = pellets[i];
                }
                result = primary ?? firstHit ?? pellets[0];
            }
            else
            {
                result = serverTwoStage
                    ? combatResolver.ResolveHitscanTwoStage(
                        origin, mainDirection, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root, logicalMuzzle, bodyAnchor, rewindContext, attributionSource)
                    : combatResolver.ResolveHitscan(
                        origin, mainDirection, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root, attributionSource);
            }

            // ③ Bloom 累计（影响下一发）
            _accuracy.OnShot(Resolved);
            // ④ 后坐冲量（影响下一发；产出本发完整结果供表现消费）
            var recoil = _recoil.OnShot(ctx, Resolved);
            // ⑤ 单次广播（FiredDirection=本发实际弹道方向，拖尾/表现消费；Direction 保持瞄准语义）
            OnShotFired?.Invoke(new WeaponShot(origin, aimDirection, mainDirection, result,
                spreadDeg, recoil, recoil.ShotIndex, shotSeed ?? _seed, pellets, ctx.Ads01));
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
            if (debugRecoil)
                Debug.Log($"[Recoil] {definition.WeaponId} #{recoil.ShotIndex} kick=({recoil.PitchKickDeg:F2}°, {recoil.YawKickDeg:F2}°) " +
                          $"ads01={ctx.Ads01:F2} vmBack={recoil.ViewModelBackM:F3} offsetNow={_recoil.CurrentOffset:F2} " +
                          $"burstAcc={_recoil.BurstAccumulation:F1}", this);
            return true;
        }

        public bool TryReload()
        {
            if (Runtime == null || !Runtime.CanReload) return false;
            if (!actionSystem.TryStart(PlayerActionType.Reload, Stat.ReloadTime)) return false;
            if (!Runtime.BeginReload(Stat.ReloadTime))
            {
                actionSystem.Interrupt(ActionInterruptReason.External);
                return false;
            }
            OnReloadStarted?.Invoke();
            return true;
        }

        private void HandleActionCompleted(PlayerActionType action)
        {
            if (action != PlayerActionType.Reload || Runtime == null) return;
            Runtime.CompleteReload();
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
            OnReloadCompleted?.Invoke();
        }

        private void HandleActionInterrupted(PlayerActionType action, ActionInterruptReason reason)
        {
            if (action != PlayerActionType.Reload || Runtime == null) return;
            Runtime.CancelReload();
            OnReloadInterrupted?.Invoke(reason);
        }

        /// <summary>弹道锥取样（可播种随机源为参数——网络回放/测试确定性；几何=CP0 基线）。</summary>
        internal static Vector3 ApplySpread(Vector3 forward, float spreadDegrees, System.Random rng)
        {
            if (spreadDegrees <= 0f) return forward.normalized;
            float radius = Mathf.Sqrt((float)rng.NextDouble());
            float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
            Vector2 unit = new(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
            var offset = unit * Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
            var rotation = Quaternion.LookRotation(forward.normalized);
            return (rotation * new Vector3(offset.x, offset.y, 1f)).normalized;
        }

    }
}
