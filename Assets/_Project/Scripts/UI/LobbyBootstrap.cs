using UnityEngine;

namespace Game.UI
{
    /// <summary>Scene composition root. Navigation, views and API orchestration live in LobbyPresenter.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class LobbyBootstrap : MonoBehaviour
    {
        [SerializeField] private string gameplaySceneName = "Arena";
        [SerializeField] private WeaponAssetCatalog weaponAssets;

        private void Start()
        {
            AppRoot.Ensure();
            var presenter = GetComponent<LobbyPresenter>() ?? gameObject.AddComponent<LobbyPresenter>();
            presenter.Initialize(gameplaySceneName, weaponAssets);
        }
    }
}
