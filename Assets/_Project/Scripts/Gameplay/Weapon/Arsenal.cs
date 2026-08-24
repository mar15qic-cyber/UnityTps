using System;
using Game.Gameplay.Action;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 武器槽位唯一写者（架构表A）：数字键选槽 → ActionSystem.SwitchWeapon 动作
    /// （可打断 Reload，见打断矩阵）→ 计时器过半 EquipDefinition 交换武器 → 完成收尾。
    /// 真相在 ActionSystem 计时器；动画只订阅事件做表现。
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class Arsenal : MonoBehaviour
    {
        [SerializeField] private WeaponDefinition[] slots = Array.Empty<WeaponDefinition>();
        [SerializeField] private WeaponController controller;
        [SerializeField] private ActionSystem actionSystem;
        [SerializeField] private InputReader input;
        [SerializeField, Range(0f, 1f)] private float swapProgressPoint = 0.5f;
        [SerializeField] private bool equipInitialWeaponOnStart = true;

        public int ActiveIndex { get; private set; } = -1;
        public WeaponDefinition ActiveWeapon => ActiveIndex >= 0 && ActiveIndex < slots.Length ? slots[ActiveIndex] : null;
        public int SlotCount => slots.Length;

        /// <summary>切枪开始（旧武器收枪表现）。参数：旧武器、目标槽位。</summary>
        public event Action<WeaponDefinition, int> OnSwitchStarted;
        /// <summary>计时器过半，武器已实际交换（唯一交换点）。参数：新武器。</summary>
        public event Action<WeaponDefinition> OnActiveWeaponChanged;
        /// <summary>切枪完整结束（新武器出枪完成）。</summary>
        public event Action<WeaponDefinition> OnSwitchCompleted;
        /// <summary>切枪被打断（死亡等）。</summary>
        public event Action<ActionInterruptReason> OnSwitchInterrupted;

        private bool _swapped;
        private int _pendingIndex;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (input == null) input = GetComponentInParent<InputReader>();
        }

        private void OnEnable()
        {
            if (actionSystem == null) return;
            actionSystem.OnActionStarted += HandleActionStarted;
            actionSystem.OnActionCompleted += HandleActionCompleted;
            actionSystem.OnActionInterrupted += HandleActionInterrupted;
        }

        private void Start()
        {
            // 初始武器：槽 0（等价 TrySelectSlot(0) 的初始化分支；Controller.Start 已用 definition 初始化）
            if (equipInitialWeaponOnStart && slots.Length > 0 && ActiveIndex < 0)
                TrySelectSlot(0);
        }

        private void OnDisable()
        {
            if (actionSystem == null) return;
            actionSystem.OnActionStarted -= HandleActionStarted;
            actionSystem.OnActionCompleted -= HandleActionCompleted;
            actionSystem.OnActionInterrupted -= HandleActionInterrupted;
        }

        private void Update()
        {
            if (input != null && input.SlotPressed >= 0)
                TrySelectSlot(input.SlotPressed);

            EvaluateSwap();
        }

        /// <summary>交换点求值：计时器过半才真正换武器（前半收旧枪、后半出新枪）。与 ActionSystem.Tick 同款可测入口。</summary>
        public void EvaluateSwap()
        {
            var actions = ResolveActionSystem();
            if (actions == null) return;

            if (actions.CurrentAction == PlayerActionType.SwitchWeapon
                && !_swapped
                && actions.NormalizedProgress >= swapProgressPoint)
            {
                _swapped = true;
                ActiveIndex = _pendingIndex;
                if (controller != null && slots[ActiveIndex] != null)
                    controller.EquipDefinition(slots[ActiveIndex]);
                OnActiveWeaponChanged?.Invoke(slots[ActiveIndex]);
            }
        }

        public bool TrySelectSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= slots.Length || slots[slotIndex] == null) return false;
            var actions = ResolveActionSystem();
            if (actions == null) return false;

            // 无当前武器（初始化）：直接持有槽位，不发切枪动作（出枪动画由 Equip 事件驱动）
            if (ActiveIndex < 0)
            {
                ActiveIndex = slotIndex;
                _swapped = true;
                if (controller != null) controller.EquipDefinition(slots[slotIndex]);
                OnActiveWeaponChanged?.Invoke(slots[slotIndex]);
                return true;
            }

            if (slotIndex == ActiveIndex && actions.CurrentAction != PlayerActionType.SwitchWeapon) return false;

            float duration = CurrentHolsterTime() + slots[slotIndex].DrawTime;
            if (!actions.TryStart(PlayerActionType.SwitchWeapon, duration)) return false;

            _pendingIndex = slotIndex;
            _swapped = false;
            OnSwitchStarted?.Invoke(ActiveWeapon, slotIndex);
            return true;
        }

        /// <summary>EditMode 测试与网络生成路径下 AddComponent 不跑 Awake，字段可能未解析——惰性兜底。</summary>
        private ActionSystem ResolveActionSystem()
        {
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            return actionSystem;
        }

        private float CurrentHolsterTime()
        {
            var current = ActiveWeapon;
            return current != null ? current.HolsterTime : 0f;
        }

        private void HandleActionStarted(PlayerActionType action, float duration) { }

        private void HandleActionCompleted(PlayerActionType action)
        {
            if (action != PlayerActionType.SwitchWeapon) return;
            OnSwitchCompleted?.Invoke(ActiveWeapon);
        }

        private void HandleActionInterrupted(PlayerActionType action, ActionInterruptReason reason)
        {
            if (action != PlayerActionType.SwitchWeapon) return;
            // 已过交换点：保留新武器（换弹被打断同理不回滚弹药）；未交换：维持旧武器
            OnSwitchInterrupted?.Invoke(reason);
        }
    }
}
