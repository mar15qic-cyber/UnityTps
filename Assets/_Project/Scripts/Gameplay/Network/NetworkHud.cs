using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 联机启动器（Docs/19 N1）：F1 键 = 房主（Host，即服务器+客户端）；F2 = 客人连接。
    /// client-hosted 决策（Docs/04：Dedicated Server "C 放弃"）——房主即权威。
    /// 单人离线模式不受影响：不按任何键则 NetworkManager 不启动。
    /// 输入走 Input System（项目 Active Input Handling = 新输入系统，旧 Input 类会每帧抛异常）。
    /// </summary>
    public sealed class NetworkHud : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private ushort port = 7770;
        [Tooltip("客户端连接地址（本机测试=127.0.0.1；局域网=房主 IPv4）")]
        [SerializeField] private string clientAddress = "127.0.0.1";

        private void Awake()
        {
            if (networkManager == null) networkManager = GetComponent<NetworkManager>();
            // Docs/23 P1-2【实施适配 2】：MatchLifecycle 运行时挂载（纯 MonoBehaviour，
            // 零资产改动；NetworkHud 位于 Arena 场景 NetworkSystems 对象——已按 GUID 实地确认）
            if (GetComponent<MatchLifecycle>() == null) gameObject.AddComponent<MatchLifecycle>();
        }

        private void Update()
        {
            if (networkManager == null) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            bool running = networkManager.IsServerStarted || networkManager.IsClientStarted;

            if (kb.f1Key.wasPressedThisFrame && !running)
            {
                var tugboat = networkManager.TransportManager.Transport as Tugboat;
                if (tugboat != null) tugboat.SetPort(port);
                networkManager.ServerManager.StartConnection();
                networkManager.ClientManager.StartConnection();
                Debug.Log($"[NetworkHud] HOST started on port {port}");
            }

            if (kb.f2Key.wasPressedThisFrame && !running)
            {
                var tugboat = networkManager.TransportManager.Transport as Tugboat;
                if (tugboat != null)
                {
                    tugboat.SetClientAddress(clientAddress);
                    tugboat.SetPort(port);
                }
                networkManager.ClientManager.StartConnection();
                Debug.Log($"[NetworkHud] CLIENT connecting to {clientAddress}:{port}");
            }

            if (kb.f3Key.wasPressedThisFrame && running)
            {
                networkManager.ServerManager.StopConnection(true);
                networkManager.ClientManager.StopConnection();
                Debug.Log("[NetworkHud] connection stopped");
            }
        }
    }
}

