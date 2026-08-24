using System;
using UnityEngine;

namespace Game.Gameplay.Action
{
    public enum PlayerActionType
    {
        None,
        Reload,
        SwitchWeapon,
        GrenadeThrow
    }

    public enum ActionInterruptReason
    {
        None,
        Death,
        SwitchWeapon,
        AuthorityRejected,
        External
    }

    /// <summary>
    /// 上半身互斥动作槽的唯一写者。动作完成由计时器决定，动画只能订阅事件做表现。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ActionSystem : MonoBehaviour
    {
        public PlayerActionType CurrentAction { get; private set; }
        public float Duration { get; private set; }
        public float Elapsed { get; private set; }
        public float Remaining => Mathf.Max(0f, Duration - Elapsed);
        public float NormalizedProgress => CurrentAction == PlayerActionType.None || Duration <= 0f
            ? 0f
            : Mathf.Clamp01(Elapsed / Duration);
        public bool IsBusy => CurrentAction != PlayerActionType.None;

        public event Action<PlayerActionType, float> OnActionStarted;
        public event Action<PlayerActionType> OnActionCompleted;
        public event Action<PlayerActionType, ActionInterruptReason> OnActionInterrupted;

        private void Update() => Tick(Time.deltaTime);

        public bool TryStart(PlayerActionType action, float duration)
        {
            if (action == PlayerActionType.None) return false;

            if (IsBusy)
            {
                if (action != PlayerActionType.SwitchWeapon || CurrentAction == PlayerActionType.SwitchWeapon)
                    return false;
                Interrupt(ActionInterruptReason.SwitchWeapon);
            }

            CurrentAction = action;
            Duration = Mathf.Max(0f, duration);
            Elapsed = 0f;
            OnActionStarted?.Invoke(action, Duration);

            if (Duration <= 0f) CompleteCurrent();
            return true;
        }

        public bool Interrupt(ActionInterruptReason reason)
        {
            if (!IsBusy) return false;
            var interrupted = CurrentAction;
            Clear();
            OnActionInterrupted?.Invoke(interrupted, reason);
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (!IsBusy || deltaTime <= 0f) return;
            Elapsed += deltaTime;
            if (Elapsed >= Duration) CompleteCurrent();
        }

        private void CompleteCurrent()
        {
            if (!IsBusy) return;
            var completed = CurrentAction;
            Clear();
            OnActionCompleted?.Invoke(completed);
        }

        private void Clear()
        {
            CurrentAction = PlayerActionType.None;
            Duration = 0f;
            Elapsed = 0f;
        }
    }
}
