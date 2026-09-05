using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Settings
{
    /// <summary>重绑判定结果（纯数据）。</summary>
    public enum RebindOutcome
    {
        /// <summary>候选键可用（无冲突、非保留键）。</summary>
        Available = 0,
        /// <summary>候选键已被其他动作占用（需用户选择交换或取消）。</summary>
        Conflict = 1,
        /// <summary>候选键为系统保留键（Escape 等），禁止绑定为普通动作。</summary>
        Reserved = 2,
    }

    /// <summary>
    /// 键位重绑纯规则（共享设置 Phase B）：冲突检测 / 保留键 / 交换应用。
    /// 判定唯一真相；大厅设置页与 Arena 游戏菜单共用，禁止 UI 各自实现冲突逻辑
    /// （静默产生重复绑定是明确禁止项）。
    /// </summary>
    public static class KeybindRules
    {
        /// <summary>系统保留键：Escape 永远是菜单键，不允许重绑为普通动作。</summary>
        public static bool IsReserved(Key key) => key == Key.Escape;

        /// <summary>找出当前占用 candidate 键的动作（不含 target 自身）；无占用返回 null。</summary>
        public static SettingsKeyMap.Action? FindConflict(SettingsKeyMap.Action target, Key candidate)
        {
            if (candidate == Key.None) return null;
            foreach (var b in SettingsKeyMap.Bindings)
            {
                if (b.action == target) continue;
                if (SettingsKeyMap.Get(b.action) == candidate) return b.action;
            }
            return null;
        }

        /// <summary>评估候选键（纯判定，不写任何状态）：可用 / 冲突（附占用者）/ 保留键。</summary>
        public static RebindOutcome Evaluate(SettingsKeyMap.Action target, Key candidate, out SettingsKeyMap.Action? conflicting)
        {
            conflicting = null;
            if (IsReserved(candidate)) return RebindOutcome.Reserved;
            conflicting = FindConflict(target, candidate);
            return conflicting.HasValue ? RebindOutcome.Conflict : RebindOutcome.Available;
        }

        /// <summary>
        /// 应用绑定并在冲突时交换：target 得到 candidate，原占用者得到 target 的旧键。
        /// persist=false 时只写运行时缓存（草稿预览）；返回被交换的动作（无冲突返回 null）。
        /// </summary>
        public static SettingsKeyMap.Action? ApplyWithSwap(SettingsKeyMap.Action target, Key candidate, bool persist = true)
        {
            var oldKey = SettingsKeyMap.Get(target);
            var conflicted = FindConflict(target, candidate);
            SettingsKeyMap.Set(target, candidate, persist);
            if (conflicted.HasValue)
                SettingsKeyMap.Set(conflicted.Value, oldKey, persist);
            return conflicted;
        }
    }
}
