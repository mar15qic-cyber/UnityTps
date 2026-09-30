using System;

namespace Game.Gameplay.Weapon
{
    public enum ShellReloadPhase : byte { None, Open, Insert, Close }

    /// <summary>Gameplay clock only: animation events never transfer ammunition.</summary>
    public sealed class ShellReloadState
    {
        public const float OpenSeconds = 0.9333333f, InsertSeconds = 0.7333333f, CloseSeconds = 0.8666667f;
        public ShellReloadPhase Phase { get; private set; }
        public float PhaseElapsed { get; private set; }
        public int Inserts { get; private set; }
        public uint Generation { get; private set; }
        public bool FinishRequested { get; private set; }
        public float Duration => OpenSeconds + Inserts * InsertSeconds + CloseSeconds;
        private float _phaseStart;
        private int _completed;
        public void Begin(int missing, int reserve)
        {
            Generation++; Inserts = Math.Max(0, Math.Min(missing, reserve));
            Phase = ShellReloadPhase.Open; PhaseElapsed = 0; _phaseStart = 0; _completed = 0; FinishRequested = false;
        }
        public void RequestFinish(float elapsed, bool hasAmmo)
        {
            FinishRequested = true;
            if (hasAmmo && Phase != ShellReloadPhase.None && Phase != ShellReloadPhase.Close)
            { Phase = ShellReloadPhase.Close; _phaseStart = elapsed; PhaseElapsed = 0; }
        }
        public int Advance(float elapsed, bool hasAmmo)
        {
            int transferred = 0;
            if (Phase == ShellReloadPhase.Open && elapsed >= _phaseStart + OpenSeconds)
            { Phase = ShellReloadPhase.Insert; _phaseStart += OpenSeconds; }
            while (Phase == ShellReloadPhase.Insert && elapsed + .00001f >= _phaseStart + InsertSeconds)
            {
                transferred++; _completed++; _phaseStart += InsertSeconds;
                if (_completed >= Inserts || FinishRequested) { Phase = ShellReloadPhase.Close; break; }
            }
            PhaseElapsed = Math.Max(0, elapsed - _phaseStart);
            return transferred;
        }
        public float Remaining(float elapsed) => Phase switch
        {
            ShellReloadPhase.Open => Math.Max(0, OpenSeconds - PhaseElapsed) + (Inserts - _completed) * InsertSeconds + CloseSeconds,
            ShellReloadPhase.Insert => Math.Max(0, InsertSeconds - PhaseElapsed) + Math.Max(0, Inserts - _completed - 1) * InsertSeconds + CloseSeconds,
            ShellReloadPhase.Close => Math.Max(0, CloseSeconds - PhaseElapsed), _ => 0
        };
        public void Restore(ShellReloadPhase phase, float progress, uint generation, int remainingInserts, bool finishRequested = false)
        {
            Phase = phase; PhaseElapsed = Math.Max(0, progress); Generation = generation;
            _phaseStart = -PhaseElapsed; _completed = 0; Inserts = remainingInserts;
            FinishRequested = finishRequested || phase == ShellReloadPhase.Close;
        }
        public void Cancel() { Phase = ShellReloadPhase.None; PhaseElapsed = 0; FinishRequested = false; Generation++; }
    }
}
