using System;
using Game.Gameplay.Weapon;

namespace Game.Gameplay.Network
{
    /// <summary>One atomic server-ammo observation for an owner weapon.
    /// Sequence is monotonic for a player life; LastProcessedShotRequestId is inclusive and
    /// acknowledges both accepted and rejected requests.</summary>
    [Serializable]
    public struct AuthoritativeAmmoSnapshot
    {
        public string WeaponId;
        public uint LifeEpoch;
        public uint Sequence;
        public uint LastProcessedShotRequestId;
        public int CurrentAmmo;
        public int ReserveAmmo;
        public WeaponRuntimeState ReloadState;
        public float ReloadRemaining;
    }
}
