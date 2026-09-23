using System.Collections.Generic;

namespace Game.Gameplay.Weapon
{
    /// <summary>Bounded owner-side prediction ledger. It deliberately contains only ammo
    /// identity, so an acknowledgement can never reconcile a different weapon or life.</summary>
    public sealed class PendingShotAmmoLedger
    {
        public readonly struct Entry
        {
            public readonly uint RequestId;
            public readonly string WeaponId;
            public readonly uint LifeEpoch;
            public Entry(uint requestId, string weaponId, uint lifeEpoch)
            {
                RequestId = requestId;
                WeaponId = weaponId;
                LifeEpoch = lifeEpoch;
            }
        }

        private const int Capacity = 64;
        private readonly List<Entry> _entries = new(Capacity);

        public void Register(uint requestId, string weaponId, uint lifeEpoch)
        {
            if (requestId == 0 || string.IsNullOrEmpty(weaponId)) return;
            _entries.Add(new Entry(requestId, weaponId, lifeEpoch));
            if (_entries.Count > Capacity) _entries.RemoveAt(0);
        }

        /// <summary>Returns local shots later than the inclusive server acknowledgement and
        /// removes all entries that the server has processed or that belong to an old life.</summary>
        public int ConsumeAcknowledgedAndCountRemaining(string weaponId, uint lifeEpoch, uint lastProcessedRequestId)
        {
            int remaining = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.LifeEpoch != lifeEpoch || entry.RequestId <= lastProcessedRequestId)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                if (entry.WeaponId == weaponId) remaining++;
            }
            return remaining;
        }

        public void ClearForLife(uint lifeEpoch)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].LifeEpoch != lifeEpoch) _entries.RemoveAt(i);
        }

        public int Count => _entries.Count;
    }
}
