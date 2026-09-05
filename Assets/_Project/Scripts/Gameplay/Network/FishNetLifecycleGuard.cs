using FishNet;
using FishNet.Object;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// FishNet 生命周期安全判定（Docs/23 P0 离线回归修复，Codex 复盘 2026-09-05）：
    /// Arena authored player 经 GameplayLoadoutBootstrap 调 NetworkObject.SetIsNetworked(false)
    /// 取消网络化后，其上 NetworkBehaviour 的所有权缓存未建立——直接读 FishNet IsOwner
    /// （QOL.cs 内解引用 Owner）每帧 NullReferenceException。
    /// 约定：任何 FishNet 所有权/远端判定必须先过本门——
    /// ① 网络未启动（离线）→ authored player 视为本地玩家（IsLocalOwner=true / IsRemoteProxy=false）；
    /// ② 已启动 → 仅 NetworkObject 有效且对象已生成（IsSpawned）才读 IsOwner，未生成短路。
    /// 修复红线：不修改 Assets/FishNet 第三方源码；不吞异常；不禁用组件。
    /// </summary>
    public static class FishNetLifecycleGuard
    {
        /// <summary>FishNet 网络是否已启动（服务器或客户端任一；判定写法同 OfflinePlayerGate）。</summary>
        public static bool IsNetworkActive()
        {
            var nm = InstanceFinder.NetworkManager;
            return nm != null && (nm.IsServerStarted || nm.IsClientStarted);
        }

        /// <summary>所有权安全判定：离线 → 本地；在线 → 对象已生成才读 IsOwner（未生成短路）。</summary>
        public static bool IsLocalOwner(NetworkBehaviour behaviour)
        {
            if (!IsNetworkActive()) return true;
            if (behaviour == null) return false;
            var netObject = behaviour.NetworkObject;
            if (netObject == null || !netObject.IsSpawned) return false;
            return netObject.IsOwner;
        }

        /// <summary>远端代理判定（UseRemoteState 同生命周期序列）：离线 → false；
        /// 在线 → 已生成且非 Owner 且客户端初始化完成。</summary>
        public static bool IsRemoteProxy(NetworkBehaviour behaviour)
        {
            if (!IsNetworkActive()) return false;
            if (behaviour == null) return false;
            var netObject = behaviour.NetworkObject;
            if (netObject == null || !netObject.IsSpawned) return false;
            return !netObject.IsOwner && netObject.IsClientInitialized;
        }

        /// <summary>RPC 提交前置序列（Submit 系列专用）：网络未启动 / 对象未生成时不发。
        /// 与 IsLocalOwner 的区别：此处离线返回 false（离线不应尝试发送任何 RPC）。</summary>
        public static bool CanSubmitRpc(NetworkBehaviour behaviour)
        {
            if (!IsNetworkActive()) return false;
            if (behaviour == null) return false;
            var netObject = behaviour.NetworkObject;
            return netObject != null && netObject.IsSpawned;
        }
    }
}
