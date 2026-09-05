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

        /// <summary>已解析的目标槽位意图（Docs/23 表现迭代 2026-09-05）：数字键/滚轮/Q 统一经
        /// TrySelectSlot 解析，成功发起切枪动作时触发一次——PlayerNetworkAdapter 据此转发服务器
        /// SubmitSwitchRequest，保证离线/预测/联网共用同一目标索引且每次只提交一次。
        /// 初始化装备、失败、被拒、忙碌时不触发。</summary>
        public event Action<int> OnSlotIntentResolved;

        private bool _swapped;
        private int _pendingIndex;
        private float _swapElapsedThreshold;
        private int _configuredInitialIndex;
        private bool _hasStarted;
        private int _previousSlotIndex = -1; // 最近一次成功装备的槽位（Q 快速切枪；交换点才更新，失败不污染）

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
            if (input != null)
            {
                // 同帧优先级：数字键 > 快速切枪(Q) > 滚轮（Docs/23 表现迭代 2026-09-05）
                if (input.SlotPressed >= 0)
                    TrySelectSlot(input.SlotPressed);
                else if (input.QuickSwapPressed)
                    TryQuickSwap();
                else if (input.SwapAxis != 0f)
                    TryScrollSwap(input.SwapAxis);
            }

            EvaluateSwap();
        }

        /// <summary>滚轮循环切枪：+1=上一把、-1=下一把；首尾循环，跳过 null/空槽；
        /// 仅一把有效武器时无动作。最终仍经 TrySelectSlot 统一解析（单一通路）。</summary>
        public void TryScrollSwap(float direction)
        {
            if (slots.Length == 0) return;
            int step = direction > 0f ? -1 : 1;
            int start = ActiveIndex >= 0 ? ActiveIndex : 0;
            for (int i = 1; i <= slots.Length; i++)
            {
                int candidate = (start + step * i) % slots.Length;
                if (candidate < 0) candidate += slots.Length;
                if (candidate == start) break; // 绕满一圈（含仅一把有效武器的情形）
                if (slots[candidate] == null) continue;
                TrySelectSlot(candidate);
                return;
            }
        }

        /// <summary>Q 快速切枪：切换到最近一次成功装备的武器（最近两把往返）。
        /// 无历史（初始化阶段）或历史槽无效时不做任何事。</summary>
        public void TryQuickSwap()
        {
            if (_previousSlotIndex < 0 || _previousSlotIndex >= slots.Length || slots[_previousSlotIndex] == null)
                return;
            TrySelectSlot(_previousSlotIndex);
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
                // Q 往返语义：记录交换前的武器为"上一把"（失败/中断的请求不会走到这里，不污染历史）
                _previousSlotIndex = ActiveIndex;
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
            // 单一通路：切枪动作真正发起（未被忙碌/越界/同槽拒绝）才广播意图——
            // 网络转发（PlayerNetworkAdapter→SubmitSwitchRequest）与本地共用这一目标
            OnSlotIntentResolved?.Invoke(slotIndex);
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
