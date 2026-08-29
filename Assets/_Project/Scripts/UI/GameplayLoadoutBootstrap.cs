using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>Injects the authenticated server loadout before Arsenal initializes its first slot.</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameplayLoadoutBootstrap : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private WeaponAssetCatalog weaponAssets;
        [SerializeField] private string lobbySceneName = "Lobby";

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponent<Arsenal>() ?? GetComponentInChildren<Arsenal>(true);

            // Direct-open development path: keep the authored ten-slot debug arsenal.
            if (AppRoot.Instance == null) return;
            var session = AppRoot.Instance.Session;
            if (session == null || !session.IsAuthenticated)
            {
                Fail("账号会话无效，无法进入 Arena。请重新登录。");
                return;
            }
            if (session.Loadout == null)
            {
                Fail("服务器配装缺失，无法进入 Arena。");
                return;
            }
            if (weaponAssets == null || arsenal == null)
            {
                Fail("Arena 缺少武器目录或 Arsenal 映射。");
                return;
            }
            if (!weaponAssets.TryResolveDefinition(session.Loadout.primaryWeaponId, out var primary))
            {
                Fail("主武器资源映射缺失：" + session.Loadout.primaryWeaponId);
                return;
            }
            if (!weaponAssets.TryResolveDefinition(session.Loadout.secondaryWeaponId, out var secondary))
            {
                Fail("副武器资源映射缺失：" + session.Loadout.secondaryWeaponId);
                return;
            }

            arsenal.ConfigureSlots(new List<WeaponDefinition> { primary, secondary });
        }

        private void Fail(string message)
        {
            Debug.LogError("[GameplayLoadoutBootstrap] " + message, this);
            AppRoot.Instance?.Session?.SetGameplayError(message);
            if (!string.IsNullOrWhiteSpace(lobbySceneName)) SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
        }
    }
}
