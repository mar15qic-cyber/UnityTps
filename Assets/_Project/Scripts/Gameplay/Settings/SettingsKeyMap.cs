using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Settings
{
    /// <summary>
    /// 键位重绑映射（PlayerPrefs 持久化，InputReader 读取）。默认键沿用原映射；
    /// Sprint 动作名与存档键为兼容旧设置保留，玩家看到的动作是静步。
    /// </summary>
    public static class SettingsKeyMap
    {
        public enum Action : int
        {
            MoveForward = 0, MoveBack, MoveLeft, MoveRight,
            Sprint, Jump, Reload,
            Slot1, Slot2, Slot3,
            QuickSwap,
            ThrowFrag, ThrowFlash, ThrowSmoke,
            SelectThrowable,
            LeanLeft, LeanRight,
        }

        public sealed class Binding
        {
            public Action action;
            public string label;
            public Key defaultKey;
            public string prefsKey;
        }

        public static readonly Binding[] Bindings =
        {
            new() { action = Action.MoveForward, label = "前进", defaultKey = Key.W, prefsKey = "unityfps.key.forward" },
            new() { action = Action.MoveBack,    label = "后退", defaultKey = Key.S, prefsKey = "unityfps.key.back" },
            new() { action = Action.MoveLeft,    label = "左移", defaultKey = Key.A, prefsKey = "unityfps.key.left" },
            new() { action = Action.MoveRight,   label = "右移", defaultKey = Key.D, prefsKey = "unityfps.key.right" },
            new() { action = Action.Sprint,      label = "静步", defaultKey = Key.LeftShift, prefsKey = "unityfps.key.sprint" },
            new() { action = Action.Jump,        label = "跳跃", defaultKey = Key.Space, prefsKey = "unityfps.key.jump" },
            new() { action = Action.Reload,      label = "换弹", defaultKey = Key.R, prefsKey = "unityfps.key.reload" },
            new() { action = Action.Slot1,       label = "武器 1", defaultKey = Key.Digit1, prefsKey = "unityfps.key.slot1" },
            new() { action = Action.Slot2,       label = "武器 2", defaultKey = Key.Digit2, prefsKey = "unityfps.key.slot2" },
            new() { action = Action.SelectThrowable, label = "选择 / 切换投掷物", defaultKey = Key.Digit3, prefsKey = "unityfps.key.selectThrowable" },
            new() { action = Action.LeanLeft, label = "左探头", defaultKey = Key.Q, prefsKey = "unityfps.key.leanLeft" },
            new() { action = Action.LeanRight, label = "右探头", defaultKey = Key.E, prefsKey = "unityfps.key.leanRight" },
        };

        private static readonly Dictionary<Action, Key> cache = new();
        private static bool loaded;

        /// <summary>读取某动作当前生效键（自定义优先，未自定义用默认）。</summary>
        public static Key Get(Action action)
        {
            EnsureLoaded();
            return cache.TryGetValue(action, out var key) ? key : DefaultOf(action);
        }

        /// <summary>
        /// 写入映射。persist=false 时只改运行时缓存（设置页重绑即时预览，「应用」才落 PlayerPrefs）。
        /// </summary>
        public static void Set(Action action, Key key, bool persist = true)
        {
            EnsureLoaded();
            cache[action] = key;
            if (!persist) return;
            var b = Find(action);
            if (b != null) PlayerPrefs.SetString(b.prefsKey, key.ToString());
        }

        public static void Reset(Action action, bool persist = true)
        {
            EnsureLoaded();
            cache.Remove(action);
            if (!persist) return;
            var b = Find(action);
            if (b != null) PlayerPrefs.DeleteKey(b.prefsKey);
        }

        public static bool IsCustomized(Action action)
        {
            var b = Find(action);
            return b != null && PlayerPrefs.HasKey(b.prefsKey);
        }

        public static Key DefaultOf(Action action)
        {
            var b = Find(action);
            return b != null ? b.defaultKey : Key.None;
        }

        public static Binding Find(Action action)
        {
            foreach (var b in Bindings) if (b.action == action) return b;
            return null;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            cache.Clear();
            foreach (var b in Bindings)
            {
                var raw = PlayerPrefs.GetString(b.prefsKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<Key>(raw, out var key) && key != Key.None)
                    cache[b.action] = key;
            }
        }

        /// <summary>仅供测试：清空内存缓存强制下次从 PlayerPrefs 重读。</summary>
        public static void InvalidateCache() => loaded = false;

        public static string DisplayName(Key key)
        {
            switch (key)
            {
                case Key.Digit3: return "3";
                case Key.Numpad3: return "小键盘 3";
                case Key.Space: return "空格";
                case Key.LeftShift: return "左Shift";
                case Key.LeftCtrl: return "左Ctrl";
                case Key.LeftAlt: return "左Alt";
                case Key.Escape: return "Esc";
                case Key.Enter: return "回车";
                case Key.Tab: return "Tab";
                case Key.None: return "—";
                default: return key.ToString();
            }
        }
    }
}
