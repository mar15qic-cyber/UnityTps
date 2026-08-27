using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>Boot 只负责建立持久化 AppRoot，然后把 UI 交给 Lobby 场景。</summary>
    [DefaultExecutionOrder(100)]
    public sealed class BootEntry : MonoBehaviour
    {
        [SerializeField] private string lobbySceneName = "Lobby";

        private void Start()
        {
            AppRoot.Ensure();
            if (!string.IsNullOrWhiteSpace(lobbySceneName) && SceneManager.GetActiveScene().name != lobbySceneName)
                SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
        }
    }
}
