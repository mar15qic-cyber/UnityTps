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
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(ActionSystem), typeof(CombatResolver))]
    public sealed class WeaponController : MonoBehaviour
    {
        [SerializeField] private WeaponDefinition definition;
        [SerializeField] private ScriptableObject balanceConfigAsset;
        [SerializeField] private InputReader input;
        [SerializeField] private ActionSystem actionSystem;
        [SerializeField] private CombatResolver combatResolver;
        [SerializeField] private Camera aimCamera;
        [SerializeField] private LayerMask hitMask = ~0;
        [SerializeField] private bool processLocalInput = true;

        public WeaponDefinition Definition => definition;
        public WeaponRuntime Runtime { get; private set; }
        public WeaponStat Stat { get; private set; }
        public ActionSystem Actions => actionSystem;
        public bool IsInitialized => Runtime != null;

        public event System.Action<WeaponShot> OnShotFired;
        public event System.Action OnDryFire;
        public event System.Action<int, int> OnAmmoChanged;
        public event System.Action OnReloadStarted;
        public event System.Action OnReloadCompleted;
        public event System.Action<ActionInterruptReason> OnReloadInterrupted;
        public event System.Action<WeaponDefinition> OnWeaponEquipped;

        private IBalanceConfig _balance;

        private void Awake()
        {
            if (input == null) input = GetComponentInParent<InputReader>();
            if (actionSystem == null) actionSystem = GetComponent<ActionSystem>();
            if (combatResolver == null) combatResolver = GetComponent<CombatResolver>();
            if (aimCamera == null) aimCamera = Camera.main;
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
            var ray = aimCamera != null
                ? aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : new Ray(transform.position, transform.forward);
            Vector3 direction = ApplySpread(ray.direction, Stat.Spread);
            var result = combatResolver.ResolveHitscan(
                ray.origin, direction, Stat.MaxRange, Stat.Damage, hitMask.value, transform.root);

            OnShotFired?.Invoke(new WeaponShot(ray.origin, direction, result));
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
