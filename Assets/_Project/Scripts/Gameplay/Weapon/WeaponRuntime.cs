using System;

namespace Game.Gameplay.Weapon
{
    public enum WeaponRuntimeState
    {
        Ready,
        Reloading
    }

    /// <summary>每把武器独占的纯 C# 运行时状态。只有 WeaponController 可以调用写方法。</summary>
    public sealed class WeaponRuntime
    {
        public int CurrentAmmo { get; private set; }
        public int ReserveAmmo { get; private set; }
        public int MagazineSize { get; }
        public float CooldownRemaining { get; private set; }
        public float ReloadRemaining { get; private set; }
        public WeaponRuntimeState State { get; private set; }
        public bool HasAmmo => CurrentAmmo > 0;
        public bool CanReload => State == WeaponRuntimeState.Ready && CurrentAmmo < MagazineSize && ReserveAmmo > 0;

        public WeaponRuntime(int magazineSize, int reserveAmmo)
        {
            if (magazineSize <= 0) throw new ArgumentOutOfRangeException(nameof(magazineSize));
            MagazineSize = magazineSize;
            CurrentAmmo = magazineSize;
            ReserveAmmo = Math.Max(0, reserveAmmo);
            State = WeaponRuntimeState.Ready;
        }

        internal bool TryConsumeRound(bool cadenceValidated = false)
        {
            if (State != WeaponRuntimeState.Ready || (!cadenceValidated && CooldownRemaining > 0f) || CurrentAmmo <= 0) return false;
            CurrentAmmo--;
            return true;
        }

        internal void StartCooldown(float seconds) => CooldownRemaining = Math.Max(0f, seconds);

        internal void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            CooldownRemaining = Math.Max(0f, CooldownRemaining - deltaTime);
            if (State == WeaponRuntimeState.Reloading)
                ReloadRemaining = Math.Max(0f, ReloadRemaining - deltaTime);
        }

        internal bool BeginReload(float duration)
        {
            if (!CanReload) return false;
            State = WeaponRuntimeState.Reloading;
            ReloadRemaining = Math.Max(0f, duration);
            return true;
        }

        internal void SyncReloadRemaining(float remaining)
        {
            if (State == WeaponRuntimeState.Reloading)
                ReloadRemaining = Math.Max(0f, remaining);
        }

        internal int CompleteReload()
        {
            if (State != WeaponRuntimeState.Reloading) return 0;
            int moved = Math.Min(MagazineSize - CurrentAmmo, ReserveAmmo);
            CurrentAmmo += moved;
            ReserveAmmo -= moved;
            State = WeaponRuntimeState.Ready;
            ReloadRemaining = 0f;
            return moved;
        }

        internal void CancelReload()
        {
            if (State != WeaponRuntimeState.Reloading) return;
            State = WeaponRuntimeState.Ready;
            ReloadRemaining = 0f;
        }

        /// <summary>
        /// Day4 残余审计 P0-2：跨切槽/配件重建的弹药恢复（服务器权威状态持久化）。
        /// 当前弹药按新弹匣容量钳制（配件增减容量不得隐式补满/清零），备弹原样保留；
        /// 换弹态不恢复（切枪已取消换弹），冷却清零（切枪时长已覆盖）。
        /// </summary>
        internal void RestoreAmmo(int currentAmmo, int reserveAmmo)
        {
            CurrentAmmo = Math.Clamp(currentAmmo, 0, MagazineSize);
            ReserveAmmo = Math.Max(0, reserveAmmo);
            State = WeaponRuntimeState.Ready;
            ReloadRemaining = 0f;
            CooldownRemaining = 0f;
        }

        /// <summary>Network reconciliation changes ammunition without treating an ordinary
        /// acknowledgement as a weapon switch. In particular it preserves cooldown and a
        /// locally valid reload; a changed Ready snapshot completes that reload.</summary>
        internal void ReconcileAuthoritativeAmmo(int currentAmmo, int reserveAmmo,
            WeaponRuntimeState authoritativeState, float authoritativeReloadRemaining)
        {
            bool changed = CurrentAmmo != Math.Clamp(currentAmmo, 0, MagazineSize)
                || ReserveAmmo != Math.Max(0, reserveAmmo);
            CurrentAmmo = Math.Clamp(currentAmmo, 0, MagazineSize);
            ReserveAmmo = Math.Max(0, reserveAmmo);

            if (authoritativeState == WeaponRuntimeState.Reloading)
            {
                State = WeaponRuntimeState.Reloading;
                ReloadRemaining = Math.Max(0f, authoritativeReloadRemaining);
            }
            else if (State == WeaponRuntimeState.Reloading && changed)
            {
                // A changed Ready snapshot is the authoritative reload completion (for
                // example 0/30 -> 30/0). An unchanged fire ACK must not cancel reload.
                State = WeaponRuntimeState.Ready;
                ReloadRemaining = 0f;
            }
        }

        internal void ApplyPredictedAmmoDebt(int rounds)
        {
            if (rounds <= 0) return;
            CurrentAmmo = Math.Max(0, CurrentAmmo - rounds);
        }
    }
}
