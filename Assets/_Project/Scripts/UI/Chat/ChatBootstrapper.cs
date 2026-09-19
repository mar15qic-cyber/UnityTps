using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI.Chat
{
    /// <summary>
    /// 聊天挂载引导（C4/I2）：Gameplay 侧 OnStartClient 反射入口（Gameplay 不引用 UI）。
    /// Arena：锚点 = MatchHud 所在画布（先于本调用挂载）；兜底 = 场景首个画布。
    /// 等待房间页由 LobbyPresenter 直接调 ChatController.EnsureRunning（同程序集）。
    /// </summary>
    public static class ChatBootstrapper
    {
        public static void TryMount()
        {
            var appRoot = AppRoot.Instance;
            if (appRoot == null || appRoot.ApiClient == null || appRoot.Session == null) return;

            var canvas = ResolveCanvas();
            if (canvas == null)
            {
                UnityEngine.Debug.LogWarning("[ChatBootstrapper] 未找到可用画布，聊天未挂载");
                return;
            }
            ChatController.EnsureRunning(canvas, appRoot.ApiClient, appRoot.Session);
        }

        private static Canvas ResolveCanvas()
        {
            var matchHud = GameObject.Find("MatchHud"); // MatchHudView.TryMount 先于本调用创建
            var canvas = matchHud != null ? matchHud.GetComponentInParent<Canvas>() : null;
            if (canvas != null) return canvas;
            return Object.FindFirstObjectByType<Canvas>();
        }
    }
}
