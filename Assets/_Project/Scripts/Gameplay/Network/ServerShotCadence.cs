using System;

namespace Game.Gameplay.Network
{
    /// <summary>Cadence on the validated movement input clock, independent of RPC
    /// arrival batching. One tick covers render sampling; the due time carries
    /// forward, so that tolerance cannot be spent on every shot indefinitely.</summary>
    internal sealed class ServerShotCadence
    {
        private bool _started;
        private uint _lastTick;
        private double _nextDue;

        public void Reset() { _started = false; _lastTick = 0; _nextDue = 0; }

        public bool CanFire(uint inputTick)
            => !_started || (inputTick > _lastTick && inputTick + 1.0 + 1e-6 >= _nextDue);

        public void Record(uint inputTick, int rate, float rpm)
        {
            double interval = Math.Max(1, rate) * 60.0 / Math.Max(1f, rpm);
            _nextDue = (_started ? Math.Max(inputTick, _nextDue) : inputTick) + interval;
            _lastTick = inputTick;
            _started = true;
        }
    }
}
