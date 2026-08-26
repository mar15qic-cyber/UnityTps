using System;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    public readonly struct WeaponShot
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly HitscanResult Result;

        public WeaponShot(Vector3 origin, Vector3 direction, HitscanResult result)
        {
            Origin = origin;
            Direction = direction;
            Result = result;
        }
    }

    /// <summary>武器运行时的唯一写者。只发 gameplay 事件，不操作 Animator、特效或 HUD。</summary>
    /// <remarks>
    /// CP2 瞄准权威（Docs/13 §5.3-1）：FireRay 与相机中心射线同源同线——
    /// AimOrigin = CameraPivot.position（头部权威挂点，不取 Brain 驱动的 Main Camera 位置）；
    /// AimDirection = CameraPivot.rotation × WeaponRecoilState.CurrentOffset（后坐弹簧唯一存在处，
    /// CmFPCameraRecoil 只是该 Offset 的视觉回声）。射线不再经过任何 CM 修正（sway/bob 等扩展）。
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
        [SerializeField] private LayerMask hitMask = ~0;
        [SerializeField] private bool processLocalInput = true;

        [Header("CP2 过渡后坐参数（等价自旧 CmFPCameraRecoil 硬编码；CP4 迁入 WeaponStat）")]
        [SerializeField, Min(0f)] private float recoilPitchKickDegrees = 1.1f;
        [SerializeField, Min(0f)] private float recoilYawKickDegrees = 0.3f;
        [SerializeField, Min(0.1f)] private float recoilSpringFrequency = 9f;
        [SerializeField, Range(0.1f, 1f)] private float recoilSpringDamping = 0.75f;

        public WeaponDefinition Definition => definition;
        public WeaponRuntime Runtime { get; private set; }
        public WeaponStat Stat { get; private set; }
        public ActionSystem Actions => actionSystem;
        public bool IsInitialized => Runtime != null;

        /// <summary>当前瞄准偏移（度，x=pitch/y=yaw）。CmFPCameraRecoil 回声与 FireRay 共用（恒等约束）。</summary>
        public Vector2 CurrentRecoilOffset => _recoil.CurrentOffset;
        /// <summary>权威射线原点：CameraPivot 头位（与 Brain 硬锁后的相机位置一致）。</summary>
        public Vector3 AimOrigin => aimPivot != null ? aimPivot.position : transform.position;
        /// <summary>权威瞄准方向：pivot 旋转 × 后坐偏移。开火射线与相机最终朝向同源。</summary>
        public Vector3 AimDirection => (aimPivot != null ? aimPivot.rotation : transform.rotation)
            * _recoil.OffsetRotation * Vector3.forward;

        public event System.Action<WeaponShot> OnShotFired;
        public event System.Action OnDryFire;
        public event System.Action<int, int> OnAmmoChanged;
        public event System.Action OnReloadStarted;
        public event System.Action OnReloadCompleted;
        public event System.Action<ActionInterruptReason> OnReloadInterrupted;
        public event System.Action<WeaponDefinition> OnWeaponEquipped;

        private IBalanceConfig _balance;
        private WeaponRecoilState _recoil = new();

        private void Awake()
        {
            if (input == null) input = GetComponentInParent<InputReader>();
            if (actionSystem == null) actionSystem = GetComponent<ActionSystem>();
            if (combatResolver == null) combatResolver = GetComponent<CombatResolver>();
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
            Runtime.Tick(Time.deltaTime);
            _recoil.Tick(Time.deltaTime, recoilSpringFrequency, recoilSpringDamping);
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
            Runtime = new WeaponRuntime(Stat.MagSize, Stat.ReserveAmmo);
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
        }

        /// <summary>切枪中途换装（Arsenal 在交换点调用）：重置运行时并广播，供 FP/TP 表现切换视图与动画集。</summary>
        public void EquipDefinition(WeaponDefinition next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (Runtime != null && Runtime.State == WeaponRuntimeState.Reloading)
                Runtime.CancelReload();
            // 切枪硬重置：后坐弹簧归零（Docs/13 §5.3-4；停火/换弹不重置，自然恢复）
            _recoil.HardReset();
            Initialize(next, _balance);
            OnWeaponEquipped?.Invoke(next);
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

            // CP2 瞄准权威五步顺序（Docs/13 §5.3-5）：
            // ① 用开火前状态算本发弹道：AimOrigin/Direction 取 CameraPivot×当前后坐偏移（未含本发冲量）
            Vector3 origin = AimOrigin;
            Vector3 aimDirection = AimDirection;
            Vector3 direction = ApplySpread(aimDirection, Stat.Spread);
            // ② 命中结算
            var result = combatResolver.ResolveHitscan(
                origin, direction, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root);
            // ③ Bloom 累计（CP4 接入 WeaponAccuracyState，本检查点无操作）
            // ④ 后坐冲量（影响下一发；弹道方向已在 ① 取定）
            _recoil.OnShot(recoilPitchKickDegrees, recoilYawKickDegrees, recoilSpringFrequency);
            // ⑤ 广播，表现层消费同一份结果
            OnShotFired?.Invoke(new WeaponShot(origin, direction, result));
            OnAmmoChanged?.Invoke(Runtime.CurrentAmmo, Runtime.ReserveAmmo);
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

        private static Vector3 ApplySpread(Vector3 forward, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return forward.normalized;
            var offset = UnityEngine.Random.insideUnitCircle * Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
            var rotation = Quaternion.LookRotation(forward.normalized);
            return (rotation * new Vector3(offset.x, offset.y, 1f)).normalized;
        }
    }
}
