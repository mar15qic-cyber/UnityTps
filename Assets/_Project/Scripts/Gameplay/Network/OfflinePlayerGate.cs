using FishNet;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 离线 Player 实例的网络门（Docs/19 N1）：
    /// 场景中预置的 Player_Day2_Rebuilt 服务于单人离线模式；一旦 FishNet
    /// 连接启动（Host 或 Client），该实例立即停用，由 PlayerSpawner 生成的
    /// 网络 Player 接管；断线后恢复（回到单人离线）。
    /// 判定源：InstanceFinder.NetworkManager 的连接状态（每帧轮询，无事件依赖）。
    /// </summary>
    public sealed class OfflinePlayerGate : MonoBehaviour
    {
        private bool _deactivatedByNetwork;

        private void Update()
        {
            var nm = InstanceFinder.NetworkManager;
            bool online = nm != null && (nm.IsServerStarted || nm.IsClientStarted);

            if (online && gameObject.activeSelf)
            {
                _deactivatedByNetwork = true;
                gameObject.SetActive(false);
            }
            else if (!online && _deactivatedByNetwork && !gameObject.activeSelf)
            {
                _deactivatedByNetwork = false;
                gameObject.SetActive(true);
            }
        }

        private void OnDisable()
        {
            // SetActive(false) 由本组件自己触发时事件链安全（无订阅清理需求）
        }
    }
}
