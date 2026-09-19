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

        private async void Start()
        {
            await HotUpdateBootstrap.CheckAndApplyAsync();
            AppRoot.Ensure();
            if (!string.IsNullOrWhiteSpace(lobbySceneName) && SceneManager.GetActiveScene().name != lobbySceneName)
                SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
        }
    }
}
