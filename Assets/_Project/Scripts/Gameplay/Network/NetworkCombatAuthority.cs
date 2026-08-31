using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Health;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 服务器权威战斗网关（Docs/19 N3）：
    /// ① FireRequest：远端玩家把"开火意图+瞄准快照"发给服务器；服务器用 WeaponController.TryFire()
    ///    走完整既有链路（冷却/弹药/动作槽/散布/Raycast/后坐/事件）——**服务器即结算真相**；
    /// ② 开火表现广播：服务器 OnShotFired → ObserversRpc 转发给所有端（含 Owner 做本地表现）；
    /// ③ 生命值网络化：PlayerHealth SyncVar（服务器写、全员读）+ 死亡广播 + 重生。
    /// 单人离线零影响：无网络时本组件不注册任何事件。
    /// </summary>
    public sealed class NetworkCombatAuthority : NetworkBehaviour
    {
        private WeaponController _controller;
        private WeaponFireContextProvider _fireContext;

        private void Awake()
        {
            _controller = GetComponent<WeaponController>();
            _fireContext = GetComponentInParent<WeaponFireContextProvider>();
        }

        // ---- ① 远端玩家 → 服务器 开火请求 ----

        /// <summary>远端客户端调用（Owner 专属）：把本帧开火意图发服务器验证结算。
        /// FireHeld 连发时每帧调用；服务器 TryFire 自带冷却/弹药闸。</summary>
        public void SubmitFireRequest()
        {
            NetworkObject networkObject = NetworkObject;
            if (networkObject != null && networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerFireRequest();
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerFireRequest()
        {
            // 服务器权威结算：走完整 TryFire（冷却/弹药/动作槽/散布/Raycast/伤害/事件）。
            // 命中判定用服务器上的瞄准状态（NetworkTransform 同步的位姿）——
            // 拉枪补偿/LagCompensator 属 Docs/04 Day9 范畴，当前为直连简式。
            _controller?.TryFire();
        }

        // ---- ② 开火表现广播（服务器事件 → 全端） ----

        public override void OnStartServer()
        {
            if (_controller != null)
            {
                _controller.OnShotFired += HandleServerShot;
                _controller.OnDryFire += HandleServerDryFire;
            }
        }

        private void OnDestroy()
        {
            if (_controller == null) return;
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
            {
                _controller.OnShotFired -= HandleServerShot;
                _controller.OnDryFire -= HandleServerDryFire;
            }
        }

        private void HandleServerShot(WeaponShot shot) => ObserversShot(shot.Origin, shot.FiredDirection, shot.Result.Point, shot.Result.Hit);
        private void HandleServerDryFire() { /* 空仓表现仅 Owner 本地有音效需求，无需广播 */ }

        [ObserversRpc(ExcludeOwner = false, ExcludeServer = false, RunLocally = false)]
        private void ObserversShot(Vector3 origin, Vector3 direction, Vector3 hitPoint, bool hit)
        {
            // 表现端钩子：远端 TP 弹道拖尾/枪口光（表现组件订阅；Owner 端本地已有 FP 表现，
            // 本 RPC ExcludeOwner=false 但 Owner 的 WeaponView 已由本地事件驱动——远端表现组件
            // 自行按 IsOwner 过滤）
            OnRemoteShot?.Invoke(origin, direction, hitPoint, hit);
        }

        /// <summary>远端开火表现事件（弹道/枪口光订阅；参数：起点/方向/命中点/是否命中）。</summary>
        public event System.Action<Vector3, Vector3, Vector3, bool> OnRemoteShot;

        // ---- ③ 生命值网络化 ----

        private readonly SyncVar<int> _health = new();
        private readonly SyncVar<bool> _dead = new();
        private DamageableTarget _target;

        [ObserversRpc(ExcludeOwner = false)]
        private void ObserversDied()
        {
            // 死亡表现（简单版：TP 模型倒地/禁用碰撞由表现层订阅；当前仅日志级钩子）
            OnRemoteDied?.Invoke();
        }

        /// <summary>远端死亡事件。</summary>
        public event System.Action OnRemoteDied;

        private void Update()
        {
            // 服务器采集生命值（Player 上挂 DamageableTarget 后生效）
            if (NetworkObject != null && NetworkObject.IsServerInitialized && _target == null)
            {
                _target = GetComponentInChildren<DamageableTarget>(true);
                if (_target != null)
                {
                    _health.Value = _target.CurrentHealth;
                    _target.OnDied += HandleServerDied;
                    _target.OnHealthChanged += (cur, max) => _health.Value = cur;
                }
            }
        }

        private void HandleServerDied()
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized) return;
            _dead.Value = true;
            ObserversDied();
            // 简单重生：3 秒后满血复活（Docs/04 Day9 才做完整 LifeFSM）
            Invoke(nameof(ServerRespawn), 3f);
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerRespawn()
        {
            _target?.ResetHealth();
            _dead.Value = false;
            // 位置重置到出生点
            var spawner = FindFirstObjectByType<FishNet.Component.Spawning.PlayerSpawner>();
            if (spawner != null)
            {
                var spawns = (Transform[])spawner.GetType().GetField("Spawns").GetValue(spawner);
                if (spawns != null && spawns.Length > 0)
                {
                    transform.position = spawns[0].position;
                    transform.rotation = spawns[0].rotation;
                }
            }
        }

        public int Health => _health.Value;
        public bool IsDead => _dead.Value;
    }
}
