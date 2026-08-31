using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 远端玩家的武器状态同步（Docs/19 N2）：
    /// - 服务器广播 weaponId（SyncVar）：远端 EquipDefinition 切换 TP 持枪（WeaponController
    ///   在远端只作表现数据源——processLocalInput 关闭，Runtime 不 Tick 开火）；
    /// - 开火/换弹动画事件（ObserversRpc）：远端 TPAnimDriver 的上层动作层播放；
    /// - Owner 侧：本地事件照常（本地 TP 动画走既有 PlayerStateView 链路）。
    /// N3 将把开火升级为服务器权威（FireRequest→验证→Raycast→广播）；当前 N2 只同步表现。
    /// </summary>
    public sealed class NetworkWeaponState : NetworkBehaviour
    {
        private readonly SyncVar<string> _weaponId = new();

        private WeaponController _controller;
        private Arsenal _arsenal;

        private void Awake()
        {
            _controller = GetComponent<WeaponController>();
            _arsenal = GetComponentInParent<Arsenal>();
        }

        public override void OnStartServer()
        {
            // 服务器采集初始武器 + 订阅后续切换与开火/换弹事件（服务器上每个玩家的
            // WeaponController 都在跑——Host 玩家本地开火即服务器事件；远端玩家的
            // 开火在 N3 之前无本地结算，N3 经 FireRequest 服务器验证后才产生事件）
            if (_controller != null)
            {
                _weaponId.Value = _controller.Definition != null ? _controller.Definition.WeaponId : string.Empty;
                _controller.OnWeaponEquipped += HandleServerWeaponEquipped;
                _controller.OnShotFired += HandleServerShotFired;
                _controller.OnReloadStarted += HandleServerReloadStarted;
            }
        }

        public override void OnStartClient()
        {
            if (IsOwner) return; // Owner 本地切枪照旧（服务器会广播回来，远端才应用）

            // 远端：禁本地输入处理 + 订阅 SyncVar 变化切枪 + 应用当前值
            if (_controller != null)
            {
                var flag = _controller.GetType().GetField("processLocalInput",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (flag != null) flag.SetValue(_controller, false);
            }
            _weaponId.OnChange += HandleWeaponChanged;
            ApplyWeapon(_weaponId.Value);
        }

        private void OnDestroy()
        {
            if (_controller == null) return;
            // 退订未注册的事件同样安全；不要在离线对象销毁阶段读取 FishNet 状态，
            // 此时 NetworkBehaviour 的 NetworkObject 缓存可能尚未绑定或已经释放。
            _controller.OnWeaponEquipped -= HandleServerWeaponEquipped;
            _controller.OnShotFired -= HandleServerShotFired;
            _controller.OnReloadStarted -= HandleServerReloadStarted;
        }

        private void HandleServerWeaponEquipped(WeaponDefinition def)
        {
            if (IsServerInitialized)
                _weaponId.Value = def != null ? def.WeaponId : string.Empty;
        }

        private void HandleServerShotFired(WeaponShot _) => BroadcastFire();
        private void HandleServerReloadStarted() => BroadcastReload();

        private void HandleWeaponChanged(string prev, string next, bool asServer)
        {
            if (asServer || !IsOwner) ApplyWeapon(next);
        }

        /// <summary>远端按 weaponId 查 Arsenal 槽位并 EquipDefinition（表现数据源）。
        /// slots 为私有字段——反射读取（NetworkAdapter 同层的反向依赖规避惯例）。</summary>
        private void ApplyWeapon(string weaponId)
        {
            if (_controller == null || string.IsNullOrEmpty(weaponId)) return;
            if (_arsenal != null)
            {
                var slotsField = _arsenal.GetType().GetField("slots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var slots = slotsField?.GetValue(_arsenal) as WeaponDefinition[];
                if (slots != null)
                {
                    foreach (var def in slots)
                    {
                        if (def != null && def.WeaponId == weaponId)
                        {
                            _controller.EquipDefinition(def);
                            return;
                        }
                    }
                }
            }
            Debug.LogWarning($"[NetworkWeaponState] 远端槽位未找到 weaponId={weaponId}", this);
        }

        // ---- 开火/换弹动画事件广播（N2：表现层；N3 升级为权威结算） ----

        /// <summary>服务器/Owner 调用：广播开火动画事件（远端 TP 上身动作）。</summary>
        public void BroadcastFire()
        {
            if (IsServerInitialized) ObserversFire();
        }

        /// <summary>服务器/Owner 调用：广播换弹动画事件。</summary>
        public void BroadcastReload()
        {
            if (IsServerInitialized) ObserversReload();
        }

        [ObserversRpc(ExcludeOwner = true, ExcludeServer = false, RunLocally = false)]
        private void ObserversFire()
        {
            // 远端 TP：TPAnimDriver 已订阅 _controller.OnShotFired——直接触发同链路
            _controller?.InvokeRemoteFireForPresentation();
        }

        [ObserversRpc(ExcludeOwner = true, ExcludeServer = false, RunLocally = false)]
        private void ObserversReload()
        {
            _controller?.InvokeRemoteReloadForPresentation();
        }
    }
}
