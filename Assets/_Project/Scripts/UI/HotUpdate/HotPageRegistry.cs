using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 热更页注册表（稳定 seam）：Lua 侧经 HotLuaFacade.RegisterPage 写入，
    /// LobbyPresenter 建壳时读取并追加页签。注册发生在 AppRoot 启动（Boot 场景），
    /// 早于 LobbyPresenter.Initialize——时序天然满足；会话中途重注册 v1 不热刷页签（重启生效）。
    /// 同 id 重复注册 = 覆盖（幂等，供热更包替换脚本后重启使用）。
    /// </summary>
    public static class HotPageRegistry
    {
        public sealed class HotPage
        {
            public string Id;
            public string Label;
            public Action<RectTransform> Render;
            internal Action<RectTransform> Callback;
        }

        private static readonly List<HotPage> ordered = new();
        private static readonly Dictionary<string, HotPage> byId = new();
        private static readonly HashSet<RectTransform> renderedRoots = new();

        public static void Register(string id, string label, Action<RectTransform> render)
        {
            if (string.IsNullOrWhiteSpace(id) || render == null) return;
            var key = id.Trim();
            if (byId.TryGetValue(key, out var existing))
            {
                existing.Label = label;
                existing.Callback = render;
                return;
            }
            var page = new HotPage { Id = key, Label = label, Callback = render };
            page.Render = root =>
            {
                if (root != null) renderedRoots.Add(root);
                page.Callback?.Invoke(root);
            };
            byId[key] = page;
            ordered.Add(page);
        }

        public static bool TryGet(string id, out HotPage page)
        {
            page = null;
            return !string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id.Trim(), out page);
        }

        /// <summary>注册顺序枚举（页签顺序确定性）。</summary>
        public static IReadOnlyList<HotPage> All => ordered;

        public static void ClearAll()
        {
            foreach (var root in renderedRoots)
            {
                if (root == null) continue;
                foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                    button.onClick.RemoveAllListeners();
            }
            renderedRoots.Clear();
            foreach (var page in ordered) { page.Callback = null; page.Render = null; }
            ordered.Clear();
            byId.Clear();
        }
    }
}
