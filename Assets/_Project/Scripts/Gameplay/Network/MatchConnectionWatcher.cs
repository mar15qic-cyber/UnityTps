using FishNet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 对局连接观测（Phase C ⑦⑨）：客户端侧「非主动断线」的统一本地处理——
    /// 纯客户端在对局中（Countdown/InProgress）与服务器失去连接时：
    /// ① 判定 HostLost（client-hosted：房主即服务器，无法迁移时本局作废）；
    /// ② 本地安全清理（输入门控复位 + 硬锁 + 菜单关闭 + 光标交还）；
    /// ③ 回大厅（结算流未接管导航时；断线未收到 Ended → 不提交结算，Docs/17 §3.3 既定规则）。
    /// 主动退出的纯客户端也经此路径回大厅（服务器 Kick → 连接停止 → 同一套策略），
    /// 「主动/非主动」的差异只体现在服务器侧终局语义（MatchLeavePolicy），客户端清理完全一致。
    /// 挂载：NetworkHud.Awake 与 MatchLifecycle 同点运行时 AddComponent（零资产改动）。
    /// </summary>
    public sealed class MatchConnectionWatcher : MonoBehaviour
    {
        private bool _wasConnected;
        private bool _handled;

        private void OnEnable()
        {
            _wasConnected = false;
            _handled = false;
        }

        private void Update()
        {
            var nm = InstanceFinder.NetworkManager;
            bool isPureClient = nm != null && nm.IsClientStarted && !nm.IsServerStarted;
            bool inMatch = MatchLifecycle.Phase == MatchPhase.Countdown || MatchLifecycle.Phase == MatchPhase.InProgress;

            if (isPureClient)
            {
                _wasConnected = true;
                return;
            }

            if (!_wasConnected || _handled) return;
            _handled = true; // 连接从有到无：只处理一次

            if (!inMatch) return; // 大厅/结算后正常断开：无对局语义

            // 对局中断线（含主动退出被服务器 Kick / 房主消失 / 网络中断）：
            // 主动退出者在断线前已收到 Ended（客户端镜像 Phase=Ended，不进此分支）；
            // 未收到 Ended 的断线 = 对局作废（HostLost 语义，本局不提交结算）。
            if (MatchLifecycle.Phase == MatchPhase.Ended || MatchExitState.SettlementNavigationPending)
                return; // 结算流接管导航（Results 页）；无需本地兜底

            Debug.LogWarning("[MatchConnectionWatcher] 对局中断线（HostLost/网络中断）：本局作废，返回大厅（不提交结算）");
            HandleDisconnectLocally();
        }

        /// <summary>本地安全清理（菜单/门控/光标）并回大厅；公开给菜单控制器复用。</summary>
        public static void HandleDisconnectLocally()
        {
            if (MatchExitState.DisconnectHandled) return;
            MatchExitState.DisconnectHandled = true;
            Gameplay.Menu.GameplayInputGate.ResetAll();
            var controller = Gameplay.Menu.GameplayMenuController.Instance;
            if (controller != null)
            {
                controller.Machine.ForceCloseAndLock(Gameplay.Menu.GameplayMenuLockReason.SceneTransition);
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var nm = InstanceFinder.NetworkManager;
            if (nm != null && nm.IsClientStarted) nm.ClientManager.StopConnection();
            Gameplay.Menu.GameplayMenuController.ReturnToLobbyLocally();
        }
    }
}
