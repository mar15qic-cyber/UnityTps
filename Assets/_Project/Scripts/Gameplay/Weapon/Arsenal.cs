using System;
using System.Collections.Generic;
using Game.Gameplay.Action;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 武器槽位唯一写者（架构表A）：数字键选槽 → ActionSystem.SwitchWeapon 动作
    /// （可打断 Reload，见打断矩阵）→ 收枪时长耗尽即 EquipDefinition 交换武器 → 完成收尾。
    /// 真相在 ActionSystem 计时器；动画只订阅事件做表现。
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class Arsenal : MonoBehaviour
    {
        [SerializeField] private WeaponDefinition[] slots = Array.Empty<WeaponDefinition>();
        [SerializeField] private WeaponController controller;
        [SerializeField] private ActionSystem actionSystem;
        [SerializeField] private InputReader input;
        [SerializeField] private bool equipInitialWeaponOnStart = true;

        public int ActiveIndex { get; private set; } = -1;
        public WeaponDefinition ActiveWeapon => ActiveIndex >= 0 && ActiveIndex < slots.Length ? slots[ActiveIndex] : null;
        public int SlotCount => slots.Length;

        /// <summary>切枪开始（旧武器收枪表现）。参数：旧武器、目标槽位。</summary>
        public event Action<WeaponDefinition, int> OnSwitchStarted;
        /// <summary>收枪时长耗尽，武器已实际交换（唯一交换点）。参数：新武器。</summary>
        public event Action<WeaponDefinition> OnActiveWeaponChanged;
        /// <summary>切枪完整结束（新武器出枪完成）。</summary>
        public event Action<WeaponDefinition> OnSwitchCompleted;
        /// <summary>切枪被打断（死亡等）。</summary>
        public event Action<ActionInterruptReason> OnSwitchInterrupted;

        private bool _swapped;
        private int _pendingIndex;
        private float _swapElapsedThreshold;
        private int _configuredInitialIndex;
        private bool _hasStarted;

        /// <summary>Replaces authored debug slots with an authoritative runtime loadout.</summary>
        public void ConfigureSlots(IReadOnlyList<WeaponDefinition> definitions, int initialIndex = 0)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            var next = new List<WeaponDefinition>(definitions.Count);
            for (int i = 0; i < definitions.Count; i++)
                if (definitions[i] != null) next.Add(definitions[i]);

            slots = next.ToArray();
            ActiveIndex = -1;
            _pendingIndex = -1;
            _swapped = false;
            _configuredInitialIndex = slots.Length == 0 ? 0 : Mathf.Clamp(initialIndex, 0, slots.Length - 1);
            if (_hasStarted && slots.Length > 0) TrySelectSlot(_configuredInitialIndex);
        }

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
            _hasStarted = true;
            // 初始武器：槽 0（等价 TrySelectSlot(0) 的初始化分支；Controller.Start 已用 definition 初始化）
            if (equipInitialWeaponOnStart && slots.Length > 0 && ActiveIndex < 0)
                TrySelectSlot(_configuredInitialIndex);
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

        /// <summary>交换点求值：旧枪收枪时长耗尽即交换——新枪出枪恰好接续收枪完成帧，
        /// 无空窗（按比例过半会在长出枪武器上把交换点推后，制造持收枪姿态的死区）。
        /// 与 ActionSystem.Tick 同款可测入口。</summary>
        public void EvaluateSwap()
        {
            var actions = ResolveActionSystem();
            if (actions == null) return;

            if (actions.CurrentAction == PlayerActionType.SwitchWeapon
                && !_swapped
                && actions.Elapsed >= _swapElapsedThreshold)
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

            float holsterTime = CurrentHolsterTime();
            float duration = holsterTime + slots[slotIndex].DrawTime;
            if (!actions.TryStart(PlayerActionType.SwitchWeapon, duration)) return false;

            _pendingIndex = slotIndex;
            _swapped = false;
            _swapElapsedThreshold = holsterTime;
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
