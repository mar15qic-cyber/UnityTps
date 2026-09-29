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
        private bool _serverStarted;
        private double _nextServerDue;
        private double _lastShotSeconds = double.NegativeInfinity;
        private double _nextShotSeconds;
        private object _equipment;

        internal string DiagnosticState(double serverSeconds)
            => $"last={_lastShotSeconds:R} next={_nextShotSeconds:R} arrival={serverSeconds:R}/{_nextServerDue:R}";

        public void BindEquipment(object equipment)
        {
            if (ReferenceEquals(_equipment, equipment)) return;
            Reset();
            _equipment = equipment;
        }

        public void Reset()
        {
            _equipment = null;
            _started = _serverStarted = false;
            _lastTick = 0;
            _nextDue = _nextServerDue = 0;
            _lastShotSeconds = double.NegativeInfinity;
            _nextShotSeconds = 0;
        }

        public bool CanFireAt(double seconds, double serverSeconds)
            => !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0
               && seconds > _lastShotSeconds && seconds + .0001 >= _nextShotSeconds
               && (!_serverStarted || serverSeconds + .2 + 1e-6 >= _nextServerDue);

        public void RecordAt(double seconds, float rpm, double serverSeconds)
        {
            double interval = 60.0 / Math.Max(1, rpm);
            _lastShotSeconds = seconds;
            _nextShotSeconds = Math.Max(seconds, _nextShotSeconds) + interval;
            _nextServerDue = (_serverStarted ? Math.Max(serverSeconds, _nextServerDue) : serverSeconds) + interval;
            _serverStarted = true;
        }

        // Arrival batching may borrow up to the existing 200ms shot history
        // window, but the debt is carried forward on a server-owned clock.
        public bool CanFire(uint inputTick, double serverSeconds)
            => CanFire(inputTick) && (!_serverStarted || serverSeconds + .2 + 1e-6 >= _nextServerDue);

        public void Record(uint inputTick, int rate, float rpm, double serverSeconds)
        {
            Record(inputTick, rate, rpm);
            _nextServerDue = (_serverStarted ? Math.Max(serverSeconds, _nextServerDue) : serverSeconds)
                + 60.0 / Math.Max(1f, rpm);
            _serverStarted = true;
        }

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
