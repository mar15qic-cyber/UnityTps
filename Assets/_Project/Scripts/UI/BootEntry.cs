using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>Boot 只负责：热更检查（下载/校验，失败回退内置不阻塞）→ 建立持久化 AppRoot → 交给 Lobby 场景。
    /// ★ 时序红线（热更稳定 seam）：CheckAndApplyAsync 必须先于 AppRoot.Ensure()——
    /// AppRoot.Awake 挂 HotUpdateRuntime 时 LuaEnv loader 要用已下载目录（HotFilesRoot）。</summary>
    [DefaultExecutionOrder(100)]
    public sealed class BootEntry : MonoBehaviour
    {
        [SerializeField] private string lobbySceneName = "Lobby";

        private string _error;
        private bool _busy;

        private async void Start()
        {
            _busy = true;
            _error = null;
            try
            {
            var release = Game.Core.ClientReleaseEnvironment.Current;
            var result = await HotUpdateBootstrap.CheckAndApplyAsync();
            if (release?.RequiresReleaseValidation == true && result.Kind != "applied" && result.Kind != "uptodate")
                throw new System.InvalidOperationException("热更新未就绪: " + result.Kind + "。请检查网络或联系测试组织者。");
            AppRoot.Ensure();
            if (!string.IsNullOrWhiteSpace(lobbySceneName) && SceneManager.GetActiveScene().name != lobbySceneName)
                SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
            }
            catch (System.Exception exception) { _error = exception.Message; Debug.LogError("[Boot] " + _error); }
            finally { _busy = false; }
        }
        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_error)) return;
            GUILayout.BeginArea(new Rect(30, 30, Mathf.Min(Screen.width - 60, 720), 180), GUI.skin.box);
            GUILayout.Label(_error);
            GUI.enabled = !_busy;
            if (GUILayout.Button("重试")) Start();
            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }
}
