using System;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
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

        public WeaponShot(Vector3 origin, Vector3 direction, HitscanResult result)
            : this(origin, direction, direction, result, 0f, default, 0, 0, null) { }

        public WeaponShot(Vector3 origin, Vector3 direction, Vector3 firedDirection, HitscanResult result,
            float finalSpreadDegrees, ShotRecoilResult recoil, int shotIndex, int seed,
            HitscanResult[] pellets)
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

        /// <summary>解析后数值（唯一持有者；Initialize/EquipDefinition 重算，CP4 起为消费源）。</summary>
        public ResolvedWeaponStats Resolved { get; private set; }
        /// <summary>当前瞄准偏移（度；Pitch 向上为正、Yaw 向右为正）。CmFPCameraRecoil 回声与 FireRay 共用。</summary>
        public Vector2 CurrentRecoilOffset => _recoil.CurrentOffset;
        public Quaternion CurrentRecoilRotation => _recoil.OffsetRotation;
        /// <summary>权威射线原点：CameraPivot 头位。</summary>
        public Vector3 AimOrigin => aimPivot != null ? aimPivot.position : transform.position;
        /// <summary>权威瞄准方向：pivot 旋转 × 后坐偏移。</summary>
        public Vector3 AimDirection => (aimPivot != null ? aimPivot.rotation : transform.rotation)
            * _recoil.OffsetRotation * Vector3.forward;

        /// <summary>
        /// 让玩家输入优先抵消后坐债务；返回仍应写入基础视角的剩余“向上/向右”角度。
        /// </summary>
        public Vector2 ConsumeRecoilCompensation(Vector2 requestedAimDeltaDeg)
            => _recoil.ConsumeCompensation(requestedAimDeltaDeg);
        /// <summary>当前合成散布锥角（度）——弹道与准心 HUD 的同一数据源。</summary>
        public float CurrentSpreadDegrees => _accuracy.CurrentSpread(FireContext, Resolved);

        public event System.Action<WeaponShot> OnShotFired;
        public event System.Action OnDryFire;
        public event System.Action<int, int> OnAmmoChanged;
        public event System.Action OnReloadStarted;
        public event System.Action OnReloadCompleted;
        public event System.Action<ActionInterruptReason> OnReloadInterrupted;
        public event System.Action<WeaponDefinition> OnWeaponEquipped;

        private IBalanceConfig _balance;
        private WeaponRecoilState _recoil = new();
        private readonly WeaponAccuracyState _accuracy = new();
        private System.Random _random = new();     // 可播种（seed=0 随机）；弹道散布唯一随机源
        private int _seed;

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
            if (definition == null || _balance == null)
            {
                Debug.LogError("[WeaponController] WeaponDefinition or IBalanceConfig is not assigned.", this);
                enabled = false;
                return;
            }
            Initialize(definition, _balance);
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

            if (!processLocalInput || input == null) return;
            bool wantsFire = definition.FireMode == WeaponFireMode.Automatic ? input.FireHeld : input.FirePressed;
            if (wantsFire) TryFire();
            if (input.ReloadPressed) TryReload();
        }

        public void Initialize(WeaponDefinition weaponDefinition, IBalanceConfig balance)
        {
            definition = weaponDefinition != null ? weaponDefinition : throw new ArgumentNullException(nameof(weaponDefinition));
            _balance = balance ?? throw new ArgumentNullException(nameof(balance));
            Stat = _balance.GetWeaponStat(definition.WeaponId);
            Resolved = WeaponStatResolver.Resolve(Stat, null);   // Modifier 来源 Day4 无；接口就绪
            Runtime = new WeaponRuntime(Stat.MagSize, Stat.ReserveAmmo);
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        /// <summary>切枪中途换装（Arsenal 在交换点调用）：硬重置运行时并广播，供 FP/TP 表现切换。</summary>
        public void EquipDefinition(WeaponDefinition next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (Runtime != null && Runtime.State == WeaponRuntimeState.Reloading)
                Runtime.CancelReload();
            // 硬重置（Docs/13 §5.3-4）：仅切枪；停火/换弹自然恢复
            _recoil.HardReset();
            _accuracy.HardReset();
            Initialize(next, _balance);
            OnWeaponEquipped?.Invoke(next);
        }

        /// <summary>注入随机种子（测试/网络回放）；seed=0 恢复随机。</summary>
        public void SetRandomSeed(int seed)
        {
            _seed = seed;
            _random = seed == 0 ? new System.Random() : new System.Random(seed);
        }

        public bool TryFire()
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
            var ctx = FireContext;
            float spreadDeg = _accuracy.CurrentSpread(ctx, Resolved);
            Vector3 origin = AimOrigin;
            Vector3 aimDirection = AimDirection;

            // ② 命中结算（含 Shotgun 多弹丸：主方向一次取样，每弹丸围绕主方向独立 PelletSpread 锥，聚合单次广播）
            HitscanResult[] pellets = null;
            HitscanResult result;
            int pelletCount = Stat.Ballistic.PelletCount;
            Vector3 mainDirection = ApplySpread(aimDirection, spreadDeg);
            if (pelletCount > 1)
            {
                pellets = new HitscanResult[pelletCount];
                HitscanResult? primary = null, firstHit = null;
                for (int i = 0; i < pelletCount; i++)
                {
                    Vector3 dir = ApplySpread(mainDirection, Stat.Ballistic.PelletSpread);
                    pellets[i] = combatResolver.ResolveHitscan(
                        origin, dir, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root);
                    if (primary == null && pellets[i].Damaged) primary = pellets[i];
                    if (firstHit == null && pellets[i].Hit) firstHit = pellets[i];
                }
                result = primary ?? firstHit ?? pellets[0];
            }
            else
            {
                result = combatResolver.ResolveHitscan(
                    origin, mainDirection, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root);
            }

            // ③ Bloom 累计（影响下一发）
            _accuracy.OnShot(Resolved);
            // ④ 后坐冲量（影响下一发；产出本发完整结果供表现消费）
            var recoil = _recoil.OnShot(ctx, Resolved);
            // ⑤ 单次广播（FiredDirection=本发实际弹道方向，拖尾/表现消费；Direction 保持瞄准语义）
            OnShotFired?.Invoke(new WeaponShot(origin, aimDirection, mainDirection, result,
                spreadDeg, recoil, recoil.ShotIndex, _seed, pellets));
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
            Vector2 unit = new(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 2f - 1f);
            if (unit.sqrMagnitude > 1f) unit /= unit.sqrMagnitude; // 拒绝采样≈均匀圆盘
            var offset = unit * Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
            var rotation = Quaternion.LookRotation(forward.normalized);
            return (rotation * new Vector3(offset.x, offset.y, 1f)).normalized;
        }

        private Vector3 ApplySpread(Vector3 forward, float spreadDegrees)
            => ApplySpread(forward, spreadDegrees, _random);
    }
}
