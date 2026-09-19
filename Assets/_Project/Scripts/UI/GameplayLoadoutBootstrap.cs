using System.Collections.Generic;
using FishNet.Object;
using Game.Gameplay.Network;
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
            // ── 在线/Dedicated 模式门（2026-09-08 追加 P0 §6 一.1，审计 §5.1 缺口 1）──
            // 本组件只挂在 Arena 场景预置离线 Player 上（联网 spawn prefab 不含本组件——不会误禁
            // 网络玩家）。[-1000] 执行序保证本 Awake 先于本对象及其子树一切其他脚本的 Awake/Start/
            // Update：联网/Dedicated 上下文成立时整树禁用——InputReader/Locomotor/Camera/AudioListener/
            // HUD/Arsenal/WeaponController 全部不运行（不是只关 Renderer）。离线直开/StartGameplay
            // 路径不受影响（authored Player 照旧启用）。
            if (ShouldDisableAuthoredPlayer(
                    NetworkLaunchContext.HasPendingClientLaunch,
                    NetworkLaunchContext.DedicatedServer != null))
            {
                // 先断网再禁树（关键顺序）：FishNet 会把场景 NetworkObject 当作场景对象注册——
                // 客户端 spawn 对位时按服务器指令强制 SetActive(true)（实机取证 IsSpawned=True →
                // authored 复活、inputReaders=2）。SetIsNetworked(false) 让 FishNet 完全忽略本对象
                //（离线路径防"scene-object startup 反噬"用的同一防护，反向缺口同源）。
                var gateNetworkObject = GetComponent<NetworkObject>();
                if (gateNetworkObject != null) gateNetworkObject.SetIsNetworked(false);
                gameObject.SetActive(false);
                Debug.Log("[GameplayLoadoutBootstrap] 在线/Dedicated 上下文：场景预置离线 Player 已整树禁用（影子玩家根治）", this);
                return;
            }

            // This authored Arena instance is the offline player. Keep its
            // NetworkObject from running FishNet scene-object startup, which
            // would otherwise deactivate the player before the local setup.
            // Runtime PlayerSpawner instances still use the networked prefab.
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null) networkObject.SetIsNetworked(false);

            // Arena's Player is a scene prefab instance. Keep the authored
            // offline player usable even when a stale prefab/scene override
            // left the root or its two weapon owners disabled. FishNet may
            // still gate owner-only components later in OnStartClient.
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (arsenal == null) arsenal = GetComponent<Arsenal>() ?? GetComponentInChildren<Arsenal>(true);
            if (arsenal != null && !arsenal.enabled) arsenal.enabled = true;
            var controller = GetComponent<WeaponController>();
            if (controller != null && !controller.enabled) controller.enabled = true;

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
            if (!TryResolveLpfpDefinition(session.Loadout.primaryWeaponId, "主武器", out var primary))
            {
                return;
            }
            if (!TryResolveLpfpDefinition(session.Loadout.secondaryWeaponId, "副武器", out var secondary))
            {
                return;
            }

            arsenal.ConfigureSlots(new List<WeaponDefinition> { primary, secondary });
        }

        /// <summary>在线/Dedicated 模式判定（纯函数，EditMode 锁定）：任一成立即禁用场景预置离线玩家——
        /// ① NetworkLaunchContext 有待消费的客户端启动上下文（生产路径：建房/加入后加载 Arena）；
        /// ② Dedicated Server 进程（NetworkLaunchContext.DedicatedServer 非空）。</summary>
        public static bool ShouldDisableAuthoredPlayer(bool hasPendingClientLaunch, bool isDedicatedServer)
            => hasPendingClientLaunch || isDedicatedServer;

        private bool TryResolveLpfpDefinition(string itemId, string slotLabel, out WeaponDefinition definition)
        {
            definition = null;
            if (!weaponAssets.TryGet(itemId, out var entry) || entry == null || !entry.IsLpfp)
            {
                Fail(slotLabel + "仅支持 LPFP 资源，已拒绝非 LPFP 条目：" + itemId);
                return false;
            }
            if (!weaponAssets.TryResolveDefinition(itemId, out definition) || definition == null)
            {
                Fail(slotLabel + " LPFP 资源映射缺失：" + itemId);
                return false;
            }
            return true;
        }

        private void Fail(string message)
        {
            Debug.LogError("[GameplayLoadoutBootstrap] " + message, this);
            AppRoot.Instance?.Session?.SetGameplayError(message);
            if (!string.IsNullOrWhiteSpace(lobbySceneName)) SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
        }
    }
}
