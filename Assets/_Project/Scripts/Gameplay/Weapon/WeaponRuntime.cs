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

        internal bool TryConsumeRound()
        {
            if (State != WeaponRuntimeState.Ready || CooldownRemaining > 0f || CurrentAmmo <= 0) return false;
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
    }
}
