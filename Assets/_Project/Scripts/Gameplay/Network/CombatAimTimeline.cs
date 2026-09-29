using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>Bounded intent history on the shooter's monotonic clock. Arrival frames do
    /// not determine ADS. The server rebuilds the same rate-limited transition from intents.</summary>
    internal sealed class CombatAimTimeline
    {
        private readonly List<(double time, float value, bool target)> _keys = new();
        public void Reset() => _keys.Clear();
        public bool SetTarget(bool target, double seconds, float duration)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return false;
            if (_keys.Count > 0 && seconds < _keys[_keys.Count - 1].time) return false;
            float value = Evaluate(seconds, duration);
            _keys.Add((seconds, value, target));
            if (_keys.Count > 128) _keys.RemoveAt(0);
            return true;
        }
        public float Evaluate(double seconds, float duration)
        {
            for (int i = _keys.Count - 1; i >= 0; i--)
            {
                var key = _keys[i];
                if (seconds < key.time) continue;
                return Mathf.MoveTowards(key.value, key.target ? 1f : 0f,
                    (float)(seconds - key.time) / Mathf.Max(.01f, duration));
            }
            return 0;
        }
    }
}
