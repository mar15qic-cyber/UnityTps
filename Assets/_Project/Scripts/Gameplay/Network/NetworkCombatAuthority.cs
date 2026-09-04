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

        // ---- ①b 换弹/切枪服务器验证（Docs/23 P0-1/P0-2，G1） ----

        /// <summary>远端客户端调用（Owner 专属）：把换弹意图发服务器验证；服务器 ActionSystem
        /// 自带忙碌/弹满闸。Owner 本地 TryReload（预测）与服务器通道都跑是设计意图（Docs/04 §8）。</summary>
        public void SubmitReloadRequest()
        {
            NetworkObject networkObject = NetworkObject;
            if (networkObject != null && networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerReloadRequest();
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerReloadRequest()
        {
            // 服务器权威：TryReload 自带 ActionSystem 忙碌/弹满闸，无需另写验证（Docs/23 P0-1）
            _controller?.TryReload();
        }

        /// <summary>远端客户端调用（Owner 专属）：把切枪意图发服务器验证；服务器合法则
        /// EquipDefinition 换装 → OnWeaponEquipped → NetworkWeaponState._weaponId 广播链自动生效。</summary>
        public void SubmitSwitchRequest(int slot)
        {
            NetworkObject networkObject = NetworkObject;
            if (networkObject != null && networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerSwitchRequest(slot);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerSwitchRequest(int slot)
        {
            if (_controller == null) return;
            // 反射读 Arsenal.slots（规避反向依赖惯例，同 NetworkWeaponState.ApplyWeapon）；越界/空槽拒绝
            var arsenal = _controller.GetComponentInParent<Arsenal>();
            if (arsenal == null) return;
            var slotsField = arsenal.GetType().GetField("slots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var slots = slotsField?.GetValue(arsenal) as WeaponDefinition[];
            if (slots == null || slot < 0 || slot >= slots.Length || slots[slot] == null) return;
            _controller.EquipDefinition(slots[slot]);
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
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
            {
                if (_target == null)
                {
                    _target = GetComponentInChildren<DamageableTarget>(true);
                    if (_target != null)
                    {
                        _health.Value = _target.CurrentHealth;
                        _target.OnDied += HandleServerDied;
                        _target.OnHealthChanged += (cur, max) => _health.Value = cur;
                    }
                }
                // 服务器采集瞄准俯仰 → SyncVar（值变化才写，Docs/23 P0-5）
                TrySyncAimPitch();
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

        // ---- ④ 瞄准俯仰同步（Docs/23 P0-5，G2） ----

        private readonly SyncVar<float> _aimPitch = new();
        private Transform _aimPivotCached;
        private float _lastSentPitch = float.NaN;

        public override void OnStartClient()
        {
            if (IsOwner) return; // Owner 俯仰由 FPMouseLook 本地驱动，不回灌服务器值
            _aimPitch.OnChange += HandleAimPitchChanged;
        }

        /// <summary>服务器侧：反射读 WeaponController.aimPivot 当前俯仰写入 SyncVar
        /// （反射惯例 + try-null 兜底，防域重载字段漂移）。</summary>
        private void TrySyncAimPitch()
        {
            var pivot = ResolveAimPivot();
            if (pivot == null) return;
            // CameraPivot.localRotation = Euler(pitch,0,0)（FPMouseLook 约定：抬头=负 Euler X）；
            // eulerAngles 域 [0,360)，换回带符号俯仰
            float eulerX = pivot.localRotation.eulerAngles.x;
            float pitch = eulerX > 180f ? eulerX - 360f : eulerX;
            if (!Mathf.Approximately(pitch, _lastSentPitch))
            {
                _lastSentPitch = pitch;
                _aimPitch.Value = pitch;
            }
        }

        private void HandleAimPitchChanged(float prev, float next, bool asServer)
        {
            if (asServer) return;
            var pivot = ResolveAimPivot();
            if (pivot != null) pivot.localRotation = Quaternion.Euler(next, 0f, 0f);
        }

        /// <summary>反射解析 aimPivot（私有序列化字段；远端玩家在服务器实例上无相机可回退，
        /// 只认已有引用，解析不到返回 null——调用方自兜底）。</summary>
        private Transform ResolveAimPivot()
        {
            if (_aimPivotCached == null && _controller != null)
            {
                var field = typeof(WeaponController).GetField("aimPivot",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                _aimPivotCached = field?.GetValue(_controller) as Transform;
            }
            return _aimPivotCached;
        }

        /// <summary>瞄准方向（世界）= aimPivot 前向，供表现层（TPAimDriver）消费；不暴露 FishNet 类型。</summary>
        public Vector3 AimDirectionWorld
        {
            get
            {
                var pivot = ResolveAimPivot();
                return pivot != null ? pivot.forward : transform.forward;
            }
        }

        /// <summary>本实例是否为本地客户端所拥有（表现层区分本地/远端玩家用）。</summary>
        public bool IsOwnerPlayer => IsOwner;
    }
}
