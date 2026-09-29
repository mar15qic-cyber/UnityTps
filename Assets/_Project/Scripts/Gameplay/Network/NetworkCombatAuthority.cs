using System;
using System.Collections.Generic;
using FishNet;
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
        // Owner 客户端的本地 WeaponController 是射速/弹药预测的唯一事实。网络层只把
        // 已经成功发生的本地 shot 上行；绝不能把 FireHeld 的每个渲染帧都排进可靠队列，
        // 否则松开扳机后服务器仍会按冷却节奏消费积压请求，造成 HUD 和换弹守恒失真。
        private uint _latestEstimatedServerTick;

        private void Awake()
        {
            _controller = GetComponent<WeaponController>();
            _fireContext = GetComponentInParent<WeaponFireContextProvider>();
        }

        // ---- ① 远端玩家 → 服务器 开火请求 ----

        /// <summary>Owner 输入侧每帧提供最新服务器 tick 估算。
        /// 真正的 RPC 只由 <see cref="HandleOwnerPredictedShot"/> 在本地成功开火后发送。
        /// 这样自动武器每发恰好一条可靠请求，既保留射击时刻的回溯 tick，也不会积压 FireHeld。
        /// estimatedServerTick：客户端对服务器当前 tick 的估算（Day3 Phase 2），服务器据此在
        /// LagCompensationConfig.MaxRewindMs 窗口内回滚 hitbox 做命中判定；越窗/无历史按服务器权威拒绝补偿。</summary>
        public void SubmitFireRequest(uint estimatedServerTick = 0)
        {
            _latestEstimatedServerTick = estimatedServerTick;
        }

        /// <summary>本地预测已实际消耗一发才上行。Host 的本地开火本身已经是服务器权威，
        /// 不再回送 RPC；离线/authored 玩家也不会读取未建立的 FishNet 所有权缓存。</summary>
        private void HandleOwnerPredictedShot(WeaponShot shot)
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (!networkObject.IsOwner || networkObject.IsServerInitialized) return;

            uint shotRequestId = ++_nextShotRequestId;
            // The round has already been consumed by the local controller at this point.
            // Record the exact identity before presentation/RPC so an ACK can replay only
            // later local shots.
            _controller?.RegisterPredictedShotForAmmo(shotRequestId, OwnerAmmoLifeEpoch);
            // 先把本发的确定 id 与同一份 WeaponShot 快照交给表现层，再发 RPC。不得从
            // Controller 当前 AimDirection 重读：TryFire 已对后坐状态施加下一发偏移。
            OnOwnerPredictedShot?.Invoke(shot, shotRequestId);
            Vector3 aimOrigin = shot.Origin;
            Vector3 aimDirection = shot.Direction;
            if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] submit id={shotRequestId} estTick={_latestEstimatedServerTick} conn={networkObject.LocalConnection?.ClientId} match={MatchLifecycle.ClientMatchId}"
                + $" aimO={aimOrigin.ToString("F2")} aimD={aimDirection.ToString("F3")}");
            var adapter = GetComponent<PlayerNetworkAdapter>();
            var request = new TimedFireRequest { ShotId = shotRequestId,
                CommandId = ++_nextCombatCommandId, WeaponId = _controller.Definition.WeaponId,
                EquipmentCommandId = _submittedEquipmentCommand,
                LatestObservedTick = ObserverTimeline.LatestTick,
                ShotSeconds = shot.ShotSeconds,
                InputTick = adapter != null ? adapter.LocalInputTick : 0,
                LifeEpoch = adapter != null ? adapter.KnownLifeEpoch : 0,
                DisplayTick = _controller != null
                    ? _controller.DisplayTickForShot(shot.Origin, shot.Direction, ObserverTimeline.LatestTick)
                    : ObserverTimeline.LatestTick,
                AimOrigin = shot.Origin, AimDirection = shot.Direction, Ads01 = shot.Ads01 };
            if (PublicTestTelemetry.Enabled) PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "shot-submit", shotId = request.ShotId,
                commandId = request.CommandId, equipmentCommandId = request.EquipmentCommandId,
                weaponId = request.WeaponId, shotSeconds = request.ShotSeconds, seed = shot.Seed,
                ads = shot.Ads01, spread = shot.FinalSpreadDegrees,
                origin = shot.Origin, aimDirection = shot.Direction, firedDirection = shot.FiredDirection,
                inputTick = request.InputTick, lifeEpoch = request.LifeEpoch, displayTick = request.DisplayTick,
                connection = (int)OwnerClientId });
            ServerFireRequest(request);
        }

        private uint _nextShotRequestId;
        private uint _nextCombatCommandId, _lastCombatCommandId;
        private uint _submittedEquipmentCommand, _executedEquipmentCommand;
        private uint _lastSubmittedActionCommand;
        private double _combatClockOffset = double.NaN;
        private double _actionStartedSeconds = double.NaN;
        private bool IsCombatClockValid(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return false;
            double offset = Time.unscaledTimeAsDouble - seconds;
            if (double.IsNaN(_combatClockOffset)) _combatClockOffset = offset;
            return Math.Abs(offset - _combatClockOffset) <= ShotTimingPolicy.MaxHistorySeconds;
        }

        private void AdvanceCombatAction(double seconds)
        {
            if (_controller == null || double.IsNaN(_actionStartedSeconds) || !_controller.Actions.IsBusy) return;
            float elapsed = (float)Math.Max(0, seconds - _actionStartedSeconds);
            _controller.Actions.Tick(Mathf.Max(0, elapsed - _controller.Actions.Elapsed));
            _controller.GetComponentInParent<Arsenal>()?.EvaluateSwap();
        }
        internal int NextPredictedSpreadSeed => ShotAimPolicy.SpreadSeed(_nextShotRequestId + 1,
            GetComponent<PlayerNetworkAdapter>()?.KnownLifeEpoch ?? 0u);
        private bool? _sentAimIntent;

        internal void SubmitAimIntentAt(bool wantsAim, double seconds)
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this) || !IsOwnerPlayer) return;
            if (_sentAimIntent == wantsAim) return;
            _sentAimIntent = wantsAim;
            if (IsServerInitialized)
            {
                var aim = GetComponent<Game.Gameplay.Player.PlayerAimState>();
                _serverAimTimeline.SetTarget(wantsAim, seconds, aim != null ? aim.AdsTransitionSeconds : .16f);
                return;
            }
            ServerAimIntent(wantsAim, seconds);
        }

        private bool _lastTimedShotAccepted;
        internal bool TryHostFire(Vector3 origin, Vector3 direction, double seconds)
        {
            var adapter = GetComponent<PlayerNetworkAdapter>();
            if (adapter == null || !adapter.TryGetServerAim(adapter.LocalInputTick, CurrentLifeEpoch,
                out var historicalOrigin, out var historicalDirection)) return false;
            var request = new TimedFireRequest { ShotId = ++_nextShotRequestId,
                InputTick = adapter.LocalInputTick, LifeEpoch = CurrentLifeEpoch,
                WeaponId = _controller.Definition.WeaponId, ShotSeconds = seconds,
                DisplayTick = CurrentServerTick(), AimOrigin = origin, AimDirection = direction,
                Ads01 = GetComponent<Game.Gameplay.Player.PlayerAimState>()?.Ads01 ?? 0f };
            if (!ValidateTimedShotAim(request, adapter, historicalOrigin, historicalDirection)) return false;
            ExecuteTimedShot(request, historicalOrigin, historicalDirection);
            return _lastTimedShotAccepted;
        }

        private readonly CombatAimTimeline _serverAimTimeline = new();
        [ServerRpc(RequireOwnership = true)]
        private void ServerAimIntent(bool wantsAim, double seconds)
        {
            if (!IsCombatClockValid(seconds)) return;
            var aim = GetComponent<Game.Gameplay.Player.PlayerAimState>();
            if (_serverAimTimeline.SetTarget(wantsAim, seconds, aim != null ? aim.AdsTransitionSeconds : .16f))
                aim?.SetRemoteAimIntent(wantsAim);
        }

        /// <summary>纯客户端 Owner 的一发本地预测与其预留请求 id 的原子关联。表现层必须
        /// 消费此事件而不是用确认 FIFO 猜测对应关系；事件在 ServerRpc 前触发。</summary>
        public event System.Action<WeaponShot, uint> OnOwnerPredictedShot;

        private readonly System.Collections.Generic.Queue<(TimedFireRequest request, double arrived)> _timedShots = new();
        private uint _lastReceivedShotId;
        private uint _lastExecutedInputTick, _lastExecutedInputLife;
        private readonly ServerShotCadence _shotCadence = new();
        private uint _cadenceLife;
        [ServerRpc(RequireOwnership = true)]
        private void ServerFireRequest(TimedFireRequest request)
        {
            if (request.CommandId != _lastCombatCommandId + 1 || request.Kind > 2) return;
            _lastCombatCommandId = request.CommandId;
            if (request.Kind == 0 && (request.ShotId == 0 || request.ShotId <= _lastReceivedShotId))
            {
                Debug.LogWarning($"[FireTrace] duplicate-or-zero id={request.ShotId} last={_lastReceivedShotId} conn={OwnerClientId}");
                return;
            }
            // Reliable ordered RPC: no legitimate submitted shot can skip an id. This also
            // prevents choosing a later id just to select a favourable deterministic sample.
            if (request.Kind == 0 && request.ShotId != _lastReceivedShotId + 1)
            { RejectShot(request.ShotId, ShotRejectReason.InvalidInputOrder); return; }
            if (request.Kind == 0) _lastReceivedShotId = request.ShotId;
            if (!IsCombatClockValid(request.ShotSeconds))
            { if (request.Kind == 0) RejectTimedShot(request, ShotRejectReason.InvalidTime);
              else ReplyCombatAction(request, false); return; }
            if (_timedShots.Count >= 32)
            {
                while (_timedShots.Count > 0)
                { var dropped = _timedShots.Dequeue().request; if (dropped.Kind == 0) RejectTimedShot(dropped, ShotRejectReason.InputTimeout);
                  else ReplyCombatAction(dropped, false); }
                if (request.Kind == 0) RejectTimedShot(request, ShotRejectReason.InputTimeout);
                else ReplyCombatAction(request, false);
                return;
            }
            _timedShots.Enqueue((request, Time.unscaledTimeAsDouble));
        }

        private void ProcessTimedShots()
        {
            var adapter = GetComponent<PlayerNetworkAdapter>();
            int rate = TimeManager != null ? (int)TimeManager.TickRate : 30;
            while (_timedShots.Count > 0)
            {
                var pending = _timedShots.Peek(); var request = pending.request;
                if (request.Kind != 0)
                {
                    _timedShots.Dequeue();
                    bool accepted = request.LifeEpoch == CurrentLifeEpoch && !_dead.Value && MatchLifecycle.AllowsCombat(true)
                        && Time.unscaledTimeAsDouble - pending.arrived <= ShotTimingPolicy.MaxWaitSeconds
                        && ExecuteCombatAction(request);
                    ReplyCombatAction(request, accepted);
                    continue;
                }
                Vector3 origin = default, direction = default;
                bool hasInput = adapter != null && adapter.TryGetServerAim(request.InputTick, request.LifeEpoch, out origin, out direction);
                var decision = ShotTimingPolicy.Evaluate(request, CurrentLifeEpoch, CurrentServerTick(), rate,
                    hasInput, adapter != null ? adapter.ServerInputTick : uint.MaxValue, Time.unscaledTimeAsDouble - pending.arrived);
                if (decision == ShotTimingPolicy.Decision.Wait) break;
                var rejection = decision == ShotTimingPolicy.Decision.Ready ? ShotRejectReason.None
                    : decision == ShotTimingPolicy.Decision.StaleLife ? ShotRejectReason.StaleLife
                    : decision == ShotTimingPolicy.Decision.InvalidTime ? ShotRejectReason.InvalidTime : ShotRejectReason.InputTimeout;
                if (request.LifeEpoch == _lastExecutedInputLife && request.InputTick < _lastExecutedInputTick)
                    rejection = ShotRejectReason.InvalidInputOrder;
                _timedShots.Dequeue();
                if (request.LifeEpoch == CurrentLifeEpoch && rejection == ShotRejectReason.None)
                { _lastExecutedInputLife = request.LifeEpoch; _lastExecutedInputTick = request.InputTick; }
                if (rejection == ShotRejectReason.None && !ValidateTimedShotAim(request, adapter, origin, direction))
                    rejection = ShotRejectReason.InvalidAim;
                if (rejection != ShotRejectReason.None) RejectTimedShot(request, rejection);
                else ExecuteTimedShot(request, origin, direction);
            }
        }

        private bool ValidateTimedShotAim(TimedFireRequest request, PlayerNetworkAdapter adapter,
            Vector3 origin, Vector3 direction)
            => _controller != null && adapter != null && ShotAimPolicy.Validate(request, origin, direction)
               && adapter.IsCameraOriginConsistent(request.InputTick, request.LifeEpoch, request.AimOrigin)
               && adapter.TryGetServerEyeAnchor(request.InputTick, request.LifeEpoch, out var eyeAnchor)
               && _controller.IsPresentedOriginUnobstructed(eyeAnchor, origin)
               && _controller.IsPresentedOriginUnobstructed(origin, request.AimOrigin);

        private void ExecuteTimedShot(TimedFireRequest request, Vector3 origin, Vector3 direction)
        {
            _lastTimedShotAccepted = false;
            AdvanceCombatAction(request.ShotSeconds);
            uint shotRequestId = request.ShotId;
            if (!MatchLifecycle.AllowsCombat(true))
            { RejectTimedShot(request, ShotRejectReason.NotReady); return; }
            double estimatedServerTick = request.DisplayTick;
            // 服务器权威结算：走完整 TryFire（冷却/弹药/动作槽/散布/Raycast/伤害/事件）。
            // Display, network and scheduling age have passed their separate bounded budgets.
            // 审计 2026-09-15 §5.2：射手自身不回溯（以当前权威姿态开火）——否则 AimOrigin/AimDirection
            // 随历史位姿移动，射线与判定基准错位。
            // 审计 2026-09-16 §6.4：廉价准入前置——必被冷却/弹药/动作槽拒绝的请求不再付出回溯代价
            // （逐渲染帧 FireHeld 会造成每帧一次全玩家 hitbox 移动 + SyncTransforms）。
            // 射手死亡闸（Phase 1，P0-补漏）：正常客户端靠 LocalMovementFrozen 不发包；本闸封堵
            // 被篡改客户端在死亡窗口内继续提交开火并由服务器结算伤害的路径（此前服务器全链无存活校验）。
            if (_dead.Value)
            {
                if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] reject id={shotRequestId} estTick={estimatedServerTick} conn={OwnerClientId} "
                    + $"match={MatchLifecycle.ClientMatchId}（射手死亡——服务器拒绝，未回溯）");
                RejectShot(shotRequestId, ShotRejectReason.ShooterDead);
                return;
            }
            // Phase 6 重放防护：同一 shotRequestId 只结算一次（正常客户端逐发自增不会命中；
            // 被篡改客户端重放旧 id/复制包在冷却结束后再次伤害的路径在此封堵）。容量 64 环形有界。
            if (IsDuplicateShotRequest(shotRequestId))
            {
                if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] reject id={shotRequestId} estTick={estimatedServerTick} conn={OwnerClientId} "
                    + $"match={MatchLifecycle.ClientMatchId}（shotId DUP——重放拒绝，未回溯未结算）");
                RejectShot(shotRequestId, ShotRejectReason.Duplicate);
                return;
            }
            if (_cadenceLife != request.LifeEpoch)
            {
                _cadenceLife = request.LifeEpoch;
                _shotCadence.Reset();
            }
            _shotCadence.BindEquipment(_controller != null ? _controller.Runtime : null);
            if (_controller == null || !_controller.CanAttemptTimedServerFire
                || request.WeaponId != _controller.Definition.WeaponId
                || (!IsOwnerPlayer && request.EquipmentCommandId != _executedEquipmentCommand)
                || !_shotCadence.CanFireAt(request.ShotSeconds, Time.unscaledTimeAsDouble))
            {
                if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] reject id={shotRequestId} estTick={estimatedServerTick} conn={OwnerClientId} "
                    + $"match={MatchLifecycle.ClientMatchId} ready={(_controller != null && _controller.CanAttemptTimedServerFire)} "
                    + $"weapon={request.WeaponId}/{_controller?.Definition?.WeaponId} equipment={request.EquipmentCommandId}/{_executedEquipmentCommand} "
                    + $"clock={request.ShotSeconds:R} cadence={_shotCadence.CanFireAt(request.ShotSeconds, Time.unscaledTimeAsDouble)} "
                    + $"{_shotCadence.DiagnosticState(Time.unscaledTimeAsDouble)}");
                RejectTimedShot(request, ShotRejectReason.NotReady);
                return;
            }
            var lagComp = ServerLagCompensation.Instance;
            double usedTick = 0;
            bool rewound = lagComp != null
                && lagComp.TryBeginRewind(estimatedServerTick, transform.root, out usedTick, shotRequestId, (int)OwnerClientId);
            // Phase 2：把实际回溯 tick 随结算上下文下传——命中判定按"射击时刻快照"比对
            // 目标生命代际/无敌态（未回溯 = default 语境，判定跳过代际闸）
            if (ServerLagCompensation.Enabled && !rewound)
            {
                RejectTimedShot(request, ShotRejectReason.HistoryUnavailable);
                return;
            }
            var rewindContext = new LagCompRewindContext(usedTick, rewound, estimatedServerTick);
            _pendingShotRequestId = shotRequestId;
            AdvanceLastProcessedShotRequestId(shotRequestId);
            bool accepted;
            try
            {
                var fireContext = _fireContext != null ? _fireContext.Context : WeaponFireContext.Default;
                var adapter = GetComponent<PlayerNetworkAdapter>();
                if (adapter != null && adapter.TryGetServerFireContext(request.InputTick, request.LifeEpoch, out var historical))
                    fireContext = historical;
                // Reconstruct the shot's ADS from rate-limited intent history, not the RPC processing frame.
                var aimState = GetComponent<Game.Gameplay.Player.PlayerAimState>();
                float ads = _serverAimTimeline.Evaluate(request.ShotSeconds, aimState != null ? aimState.AdsTransitionSeconds : .16f);
                fireContext = new WeaponFireContext(ads, fireContext.HorizontalSpeed01,
                    fireContext.IsSprinting, fireContext.IsGrounded, fireContext.IsCrouching);
                accepted = _controller.TryFireWithServerSnapshot(request.AimOrigin, request.AimDirection,
                    fireContext, ShotAimPolicy.SpreadSeed(request.ShotId, request.LifeEpoch), rewindContext,
                    cadenceValidated: true, shotSeconds: request.ShotSeconds);
                if (accepted)
                    _shotCadence.RecordAt(request.ShotSeconds, _controller.Stat.Rpm, Time.unscaledTimeAsDouble);
                if (PublicTestTelemetry.Enabled) PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "shot-result", shotId = shotRequestId,
                    lifeEpoch = request.LifeEpoch, commandId = request.CommandId, equipmentCommandId = request.EquipmentCommandId,
                    weaponId = request.WeaponId, shotSeconds = request.ShotSeconds, ads = ads,
                    inputTick = request.InputTick, displayTick = request.DisplayTick, usedTick = usedTick,
                    serverTick = CurrentServerTick(), connection = (int)OwnerClientId, reason = accepted ? "accepted" : "not-ready" });
            }
            finally
            {
                if (rewound) lagComp.EndRewind(); // 判定完成立即恢复当前位姿
                _pendingShotRequestId = 0;
            }
            if (!accepted)
            {
                if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] reject id={shotRequestId} estTick={estimatedServerTick} conn={OwnerClientId} match={MatchLifecycle.ClientMatchId}（冷却/弹药/动作槽忙碌）");
                RejectTimedShot(request, ShotRejectReason.NotReady);
            }
            else
            {
                _lastTimedShotAccepted = true;
                // OnAmmoChanged already publishes the normal SyncVar update. This direct
                // owner delivery closes the prediction loop in the same RPC response path.
                var state = GetComponent<NetworkWeaponState>();
                if (state != null && NetworkObject != null && NetworkObject.Owner != null)
                {
                    var snapshot = state.PublishAuthoritativeAmmoSnapshot(_lastProcessedShotRequestId);
                    TargetAuthoritativeAmmoSnapshot(NetworkObject.Owner, snapshot);
                }
            }
        }

        /// <summary>ShotRejectReason 的 wire 枚举（byte 扁平，避免枚举序列化歧义）。</summary>
        public enum ShotRejectReason : byte
        {
            None = 0,
            ShooterDead = 1,
            Duplicate = 2,
            NotReady = 3,
            StaleLife = 4, InvalidTime = 5, InputTimeout = 6, HistoryUnavailable = 7, InvalidInputOrder = 8, InvalidAim = 9,
        }

        /// <summary>本请求的开火 id（ServerFireRequest → HandleServerShot 同调用栈内消费；
        /// 服务器单线程，跨请求复用安全。0=非请求路径（Host 本地开火））。</summary>
        private uint _pendingShotRequestId;
        private uint _lastProcessedShotRequestId;
        // _lifeGeneration is server-only for lag compensation. Owner prediction learns its
        // replicated counterpart from atomic ammo snapshots, so post-respawn requests carry
        // the same epoch as the server instead of being discarded as life zero.
        private uint _ownerAmmoLifeEpoch;
        internal uint LastProcessedShotRequestId => _lastProcessedShotRequestId;
        /// <summary>The inclusive ACK value carried by every authoritative ammo snapshot.</summary>
        internal uint SnapshotAcknowledgementForTests => _lastProcessedShotRequestId;
        internal uint OwnerAmmoLifeEpoch => _ownerAmmoLifeEpoch;
        internal void ObserveOwnerAmmoLifeEpoch(uint epoch)
        {
            if (epoch > _ownerAmmoLifeEpoch)
                _submittedEquipmentCommand = _lastSubmittedActionCommand = 0;
            if (epoch >= _ownerAmmoLifeEpoch) _ownerAmmoLifeEpoch = epoch;
        }
        private void AdvanceLastProcessedShotRequestId(uint shotRequestId)
        {
            // Session ids do not wrap. A duplicate/old packet must not make an ACK regress.
            if (shotRequestId > _lastProcessedShotRequestId)
                _lastProcessedShotRequestId = shotRequestId;
        }
        internal void RecordProcessedShotForTests(uint shotRequestId)
            => AdvanceLastProcessedShotRequestId(shotRequestId);

        // ---- Phase 6：shotRequestId 重放去重（每实例环形窗口，内存有界） ----

        internal const int SeenShotIdsCapacity = 64;
        private readonly HashSet<ulong> _seenShotRequestIds = new();
        private readonly Queue<ulong> _seenShotRequestIdOrder = new();

        /// <summary>internal 薄壳（EditMode 直驱）：首见登记并返回 false；窗口内重复返回 true；
        /// 超容量后最老 id 逐出（正常客户端逐发自增远小于窗口，永不误伤）。</summary>
        internal bool IsDuplicateShotRequest(ulong shotRequestId)
        {
            if (_seenShotRequestIds.Contains(shotRequestId)) return true;
            _seenShotRequestIds.Add(shotRequestId);
            _seenShotRequestIdOrder.Enqueue(shotRequestId);
            while (_seenShotRequestIdOrder.Count > SeenShotIdsCapacity)
                _seenShotRequestIds.Remove(_seenShotRequestIdOrder.Dequeue());
            return false;
        }

        // ---- ①b 换弹/切枪服务器验证（Docs/23 P0-1/P0-2，G1） ----

        /// <summary>远端客户端调用（Owner 专属）：把换弹意图发服务器验证；服务器 ActionSystem
        /// 自带忙碌/弹满闸。Owner 本地 TryReload（预测）与服务器通道都跑是设计意图（Docs/04 §8）。</summary>
        public void SubmitReloadRequest()
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (networkObject.IsOwner && !networkObject.IsServerInitialized)
                SubmitCombatAction(1, -1);
        }

        private void SubmitCombatAction(byte kind, int slot)
        {
            var adapter = GetComponent<PlayerNetworkAdapter>();
            uint commandId = ++_nextCombatCommandId;
            _lastSubmittedActionCommand = commandId;
            uint previousEquipment = _submittedEquipmentCommand;
            if (kind == 2) _submittedEquipmentCommand = commandId;
            ServerFireRequest(new TimedFireRequest { CommandId = commandId, Kind = kind, Slot = slot,
                EquipmentCommandId = previousEquipment,
                InputTick = adapter != null ? adapter.LocalInputTick : 0,
                LifeEpoch = adapter != null ? adapter.KnownLifeEpoch : 0,
                WeaponId = _controller != null && _controller.Definition != null ? _controller.Definition.WeaponId : "",
                ShotSeconds = Time.timeAsDouble });
        }

        /// <summary>远端客户端调用（Owner 专属）：把切枪意图发服务器验证；服务器合法则
        /// EquipDefinition 换装 → OnWeaponEquipped → NetworkWeaponState._weaponId 广播链自动生效。</summary>
        public void SubmitSwitchRequest(int slot)
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (networkObject.IsOwner && !networkObject.IsServerInitialized)
                SubmitCombatAction(2, slot);
        }

        private bool ExecuteCombatAction(TimedFireRequest request)
        {
            if (_controller == null) return false;
            AdvanceCombatAction(request.ShotSeconds);
            if (request.Kind == 1)
            {
                if (request.WeaponId == _controller.Definition.WeaponId && _controller.TryReload())
                {
                    _actionStartedSeconds = request.ShotSeconds;
                    return true;
                }
                return false;
            }
            int slot = request.Slot;
            var arsenal = _controller.GetComponentInParent<Arsenal>();
            if (arsenal == null) return false;
            var slots = arsenal.Slots;
            if (slot < 0 || slot >= slots.Count || slots[slot] == null)
            {
                Debug.Log($"[SwitchTrace] reject slot={slot} conn={OwnerClientId}（越界/空槽）");
                return false;
            }
            if (_dead.Value) return false;
            // Slot identity is authoritative. Two slots may reference the same definition;
            // rejecting only on definition equality leaves the owner's command epoch ahead.
            if ((arsenal.ActiveIndex == slot && _controller.Definition == slots[slot] && !_controller.Actions.IsBusy) || arsenal.TrySelectSlot(slot))
            {
                _executedEquipmentCommand = request.CommandId;
                _actionStartedSeconds = request.ShotSeconds;
                Debug.Log($"[SwitchTrace] accept slot={slot} itemId={slots[slot].CatalogItemId} conn={OwnerClientId} match={MatchLifecycle.ClientMatchId}");
                return true;
            }
            return false;
        }

        private void ReplyCombatAction(TimedFireRequest request, bool accepted)
        {
            if (NetworkObject == null || NetworkObject.Owner == null) return;
            TargetCombatActionResult(NetworkObject.Owner, request.LifeEpoch, request.CommandId, request.Kind,
                accepted, _executedEquipmentCommand, _controller?.Definition?.WeaponId ?? "");
            if (!accepted && request.LifeEpoch == CurrentLifeEpoch)
            {
                var state = GetComponent<NetworkWeaponState>();
                if (state != null) TargetAuthoritativeAmmoSnapshot(NetworkObject.Owner,
                    state.PublishAuthoritativeAmmoSnapshot(_lastProcessedShotRequestId));
            }
        }

        [TargetRpc]
        private void TargetCombatActionResult(FishNet.Connection.NetworkConnection connection, uint life,
            uint commandId, byte kind, bool accepted, uint equipmentCommand, string weaponId)
            => ApplyCombatActionResult(life, commandId, kind, accepted, equipmentCommand, weaponId);

        internal void ApplyCombatActionResult(uint life, uint commandId, byte kind, bool accepted,
            uint equipmentCommand, string weaponId)
        {
            if (life != _ownerAmmoLifeEpoch || commandId != _lastSubmittedActionCommand) return;
            // A reload may already supersede a rejected switch. The latest action's
            // authoritative equipment identity must repair that chain as well.
            _submittedEquipmentCommand = equipmentCommand;
            if (accepted || _controller == null) return;
            var expected = kind == 2 ? Game.Gameplay.Action.PlayerActionType.SwitchWeapon : Game.Gameplay.Action.PlayerActionType.Reload;
            if (_controller.Actions.CurrentAction == expected)
                _controller.Actions.Interrupt(Game.Gameplay.Action.ActionInterruptReason.AuthorityRejected);
            GetComponent<NetworkWeaponState>()?.CorrectOwnerWeapon(weaponId);
        }

        // ---- ①c 退出对局（Phase C）：Owner 请求，服务器权威判定人数与终局语义 ----

        /// <summary>Owner（菜单「退出对局」确认后）调用：把离开意图发服务器。客户端不上报人数。</summary>
        public void SubmitLeaveMatchRequest()
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerLeaveMatchRequest();
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerLeaveMatchRequest()
        {
            // 服务器权威：移除前统计有效人数 → 2 人终局（PlayerLeft）/ >2 仅移除（MatchLeavePolicy）
            MatchLifecycle.ServerRequestLeave(this, PlayerLeaveReason.Voluntary);
        }

        /// <summary>本实例 Owner 的连接临时键（未生成/无 Owner 返回 -1）。Day2 起终局载荷 playerId 改用稳定 userId（MatchPlayerIdentity），本属性仅为连接级诊断保留。</summary>
        public long OwnerClientId
        {
            get
            {
                var nob = NetworkObject;
                return nob != null && nob.Owner != null ? nob.Owner.ClientId : -1;
            }
        }

        // ---- ② 开火表现广播（服务器事件 → 全端） ----

        public override void OnStartServer()
        {
            _nextMatchSnapshotRequest = 0;
            _nextRadarLookupTime = 0;
            _radarAcceptTimes.Clear();
            _radarReportTimes.Clear();
            MatchLifecycle.ServerSyncPlayerTeam(this);
            _displayName.Value = MatchLifecycle.ResolveDisplayName(this);
            _timedShots.Clear(); _lastReceivedShotId = 0; _lastExecutedInputTick = _lastExecutedInputLife = 0;
            _lastCombatCommandId = 0; _combatClockOffset = _actionStartedSeconds = double.NaN;
            _executedEquipmentCommand = 0;
            _serverAimTimeline.Reset();
            _shotCadence.Reset(); _cadenceLife = 0;
            _seenShotRequestIds.Clear(); _seenShotRequestIdOrder.Clear();
            _lastProcessedShotRequestId = 0;
            ServerLagCompensation.AfterCapture += ProcessTimedShots;
            if (MatchLifecycle.Phase == MatchPhase.Countdown || MatchLifecycle.Phase == MatchPhase.InProgress)
                _matchJoinedRealtime = Time.realtimeSinceStartup; // I3：补入者参与时长从入场起算
            if (_controller != null)
            {
                _controller.OnShotFired += HandleServerShot;
                _controller.OnDryFire += HandleServerDryFire;
            }
            // 2026-09-18 审计 §5：原"生成即注册根 CC 进物理命中集"（实机问题6）已随方法删除——
            // 玩家移动控制器不再参与射击判定，注册它没有意义。
            // 2026-09-18 实机问题2：对局中补入的系统消息（Countdown 开局波次不刷；MatchLifecycle 内闸）
            MatchLifecycle.ServerNotifyPlayerJoined(this);
        }

        public override void OnStopServer()
        {
            ServerLagCompensation.AfterCapture -= ProcessTimedShots;
            _timedShots.Clear();
            // 重生调度清理（Phase 1）：对象停止（退出/断线/池化）即作废未到期的重生排队；
            // 复用生成时 _dead SyncVar 随新局同步，Update 的 TryConsumeRespawnDue 仅服务器分支运行。
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
                _respawnAtTick.Value = 0u;
            if (_target != null)
            {
                _target.OnDied -= HandleServerDied;
                if (_healthChanged != null) _target.OnHealthChanged -= _healthChanged;
                // 复用安全（池化/重生成）：解绑即释放引用，Update 会重新解析并重订阅生命事件。
                // 若保留非空 _target，Update 的健康采集块会整体跳过 → 复用后的死亡/重生链路静默断裂。
                _target = null;
            }
            // 玩家对象移除（退出/断线/场景重置）：清掉本实例相关的命中归因登记，防跨局泄漏
            MatchLifecycle.ClearHitEntriesFor(this);
        }

        /// <summary>
        /// 网络生命周期结束（退出/池化/场景重置）：死亡表现状态机必须一并复位
        /// （审计 2026-09-16 D3：新增的表现状态也要在池化/停止/再次生成边界明确清理，
        /// 否则复用实例会带着上一局的倒地姿态与"被停用的姿态写者"回来）。
        /// </summary>
        public override void OnStopNetwork()
        {
            _sentAimIntent = null;
            GetComponent<Game.Gameplay.Player.PlayerAimState>()?.ClearRemoteAimIntent();
            if (_deathVisualActive) ResetDeathVisual();
            _deathPoseWriters.Clear();
            _tpModelCached = null;
            _nextTpModelLookupTime = 0f;
            base.OnStopNetwork();
        }

        private void OnDestroy()
        {
            ServerLagCompensation.AfterCapture -= ProcessTimedShots;
            if (_controller == null) return;
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
            {
                _controller.OnShotFired -= HandleServerShot;
                _controller.OnDryFire -= HandleServerDryFire;
            }
        }

        private void HandleServerShot(WeaponShot shot)
        {
            // S2（2026-09-19 ADS 审计）：全量表现载荷广播——旧 4 标量广播丢弃散布/弹丸/最终点，
            // 远端无从正确复现弹着；新载荷含逐弹丸终点（RemoteShotPresentation.FromShot）。
            // 纯客户端 Owner 额外定向确认（Host/服务器本地权威无预测分叉，跳过）。
            var presentation = RemoteShotPresentation.FromShot(shot, _pendingShotRequestId);
            presentation.WeaponId = _controller != null && _controller.Definition != null
                ? _controller.Definition.WeaponId : string.Empty;
            presentation.IsSuppressed = _controller != null && _controller.IsSuppressed;
            presentation.LifeEpoch = CurrentLifeEpoch;
            ObserversShot(presentation);
            bool ownerIsRemote = NetworkObject != null && NetworkObject.Owner != null
                && !NetworkObject.Owner.IsLocalClient;
            if (ownerIsRemote)
                TargetShotConfirmed(NetworkObject.Owner, presentation);
            // 审计 2026-09-15 §6：同 shotRequestId 的服务器结算日志（到达+几何结果+HP after）
            // 审计 2026-09-16 §6.1：补齐"命中但不可归属/已死/友军"与"根本没命中"的机械区分，
            // 并给出最终碰撞体/层/所属 NetworkObject——`target=null` 不再是无法解释的终态。
            var target = shot.Result.Target;
            var evidence = _controller != null ? _controller.LastFireEvidence : default;
            if (!shot.Result.Damaged)
                Debug.Log($"[FireTrace] no-damage id={_pendingShotRequestId} conn={OwnerClientId} "
                    + $"hit={shot.Result.Hit} reason={(string.IsNullOrEmpty(evidence.MissReason) ? "unknown" : evidence.MissReason)} "
                    + $"collider={(string.IsNullOrEmpty(evidence.FinalCollider) ? "-" : evidence.FinalCollider)} "
                    + $"rewind={evidence.RewindTick} point={shot.Result.Point.ToString("F2")}");
            if (PublicTestTelemetry.Enabled) PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "shot-hit", shotId = _pendingShotRequestId,
                lifeEpoch = CurrentLifeEpoch, weaponId = _controller.Definition.WeaponId, shotSeconds = shot.ShotSeconds,
                seed = shot.Seed, ads = shot.Ads01, spread = shot.FinalSpreadDegrees,
                origin = shot.Origin, aimDirection = shot.Direction, firedDirection = shot.FiredDirection, hitPoint = shot.Result.Point,
                damage = shot.Result.DamageAmount, healthAfter = target != null ? target.CurrentHealth : -1,
                collider = evidence.FinalCollider, targetObjectId = evidence.FinalOwnerObjectId,
                pelletCount = shot.Pellets != null ? shot.Pellets.Length : 1,
                connection = (int)OwnerClientId, serverTick = CurrentServerTick(),
                reason = shot.Result.Damaged ? "damaged" : shot.Result.Hit ? "blocked-or-protected" : evidence.MissReason });
            if (PublicTestTelemetry.Enabled) Debug.Log($"[FireTrace] resolve id={_pendingShotRequestId} hit={shot.Result.Hit} damaged={shot.Result.Damaged}" +
                      $" target={(target != null ? target.name : "null")} hpAfter={(target != null ? target.CurrentHealth : -1)}" +
                      $" selfSkipped={shot.Result.SelfHitsSkipped} point={shot.Result.Point.ToString("F2")} conn={OwnerClientId}" +
                      $" miss={((string.IsNullOrEmpty(evidence.MissReason) ? "OK" : evidence.MissReason))}" +
                      $" finalCollider={(string.IsNullOrEmpty(evidence.FinalCollider) ? "-" : evidence.FinalCollider)}" +
                      $" finalLayer={(evidence.FinalLayer >= 0 ? UnityEngine.LayerMask.LayerToName(evidence.FinalLayer) : "-")}" +
                      $" finalOwnerObj={evidence.FinalOwnerObjectId} decision={evidence.Decision}");
            // 击杀归因登记（F01，2026-09-19 审计）：已不在开火事件补登记——登记下沉到
            // DamageableTarget.ApplyDamage 的"伤害实际被结算"点（先于 OnDied 回调），
            // 避免首发致死注册表为空、死亡清表后又被本事件把已死目标登记回去。
            // 此处仅保留表现广播与机械证据日志。
        }
        private void HandleServerDryFire() { /* 空仓表现仅 Owner 本地有音效需求，无需广播 */ }

        [ObserversRpc(ExcludeOwner = false, ExcludeServer = false, RunLocally = false)]
        private void ObserversShot(RemoteShotPresentation shot)
        {
            // S2 表现端钩子：远端 TP 弹道（RemoteShotFxView）按 IsOwner 过滤——Owner 端本地
            // 已有 FP 表现；实例事件保留给本对象关联的表现消费者。
            OnRemoteShot?.Invoke(shot);
            OnRemoteShotGlobal?.Invoke(this, shot);
        }

        /// <summary>远端开火表现事件（本实例=射手；载荷含最终点/逐弹丸）。旧 4 标量事件全仓零订阅者，
        /// 已按 S2 载荷替换。</summary>
        public event System.Action<RemoteShotPresentation> OnRemoteShot;

        /// <summary>全局远端开火中继（静态）：任意射手的任意一发（含 Owner 自身——订阅者自行按
        /// shooter.IsOwnerPlayer 过滤）。RemoteShotFxView 据此为观察者补弹道表现。</summary>
        public static event System.Action<NetworkCombatAuthority, RemoteShotPresentation> OnRemoteShotGlobal;

        // ---- S2b：Owner 定向确认/拒绝（预测→权威闭环） ----

        /// <summary>服务器权威确认：接受=载荷即本发最终权威结果（Owner 端纠偏持久表现——弹孔/命中反馈）；
        /// 拒绝=本发未发生（Owner 端撤销预测表现：弹孔不得留存）。确认按 shotRequestId 去重（消费端）。</summary>
        [TargetRpc]
        private void TargetShotConfirmed(FishNet.Connection.NetworkConnection connection, RemoteShotPresentation shot)
        {
            OnShotConfirmed?.Invoke(shot, true);
        }

        /// <summary>服务器拒绝：本发未结算（未回溯未伤害）。Owner 端必须撤销对应预测表现。
        /// FishNet 织入契约：TargetRpc 首参必须是 NetworkConnection（显式传 Owner 连接）。</summary>
        private void RejectTimedShot(TimedFireRequest request, ShotRejectReason reason)
        {
            if (request.LifeEpoch == CurrentLifeEpoch && !_dead.Value && MatchLifecycle.AllowsCombat(true)
                && NetworkObject != null && !NetworkObject.IsOwner)
            {
                if (IsCombatClockValid(request.ShotSeconds)) AdvanceCombatAction(request.ShotSeconds);
                if (_controller != null && _controller.Definition != null
                    && request.WeaponId == _controller.Definition.WeaponId
                    && request.EquipmentCommandId == _executedEquipmentCommand)
                    _controller.AdvanceRejectedPrediction(request.ShotSeconds);
            }
            RejectShot(request.ShotId, reason);
        }

        private void RejectShot(uint shotRequestId, ShotRejectReason reason)
        {
            Debug.LogWarning($"[FireTrace] reject id={shotRequestId} conn={OwnerClientId} reason={reason} tick={CurrentServerTick()}");
            if (PublicTestTelemetry.Enabled) PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "shot-reject", shotId = shotRequestId,
                connection = (int)OwnerClientId, serverTick = CurrentServerTick(), reason = reason.ToString() });
            AdvanceLastProcessedShotRequestId(shotRequestId);
            var state = GetComponent<NetworkWeaponState>();
            AuthoritativeAmmoSnapshot snapshot = state != null
                ? state.PublishAuthoritativeAmmoSnapshot(_lastProcessedShotRequestId)
                : default;
            TargetShotRejected(NetworkObject.Owner, shotRequestId, reason, snapshot);
        }

        [TargetRpc]
        private void TargetShotRejected(FishNet.Connection.NetworkConnection connection, uint shotRequestId, ShotRejectReason reason,
            AuthoritativeAmmoSnapshot snapshot)
        {
            var rejected = new RemoteShotPresentation { ShotRequestId = shotRequestId, PelletCount = 1 };
            OnShotConfirmed?.Invoke(rejected, false);
            // Rejects do not mutate server ammo, so they need this immediate atomic snapshot
            // instead of waiting for an unrelated SyncVar change.
            ObserveOwnerAmmoLifeEpoch(snapshot.LifeEpoch);
            _controller?.ApplyAuthoritativeAmmoSnapshot(snapshot);
            Debug.LogWarning($"[FireTrace] confirm reject id={shotRequestId} reason={reason}（已通知 Owner 撤销预测表现）");
        }

        [TargetRpc]
        private void TargetAuthoritativeAmmoSnapshot(FishNet.Connection.NetworkConnection connection,
            AuthoritativeAmmoSnapshot snapshot)
        {
            ObserveOwnerAmmoLifeEpoch(snapshot.LifeEpoch);
            _controller?.ApplyAuthoritativeAmmoSnapshot(snapshot);
        }

        /// <summary>Owner 端确认事件（TargetRpc 落点；false=拒绝）。表现层（WeaponView）消费：
        /// 去重/纠偏/撤销——预测暂态允许，确认后不得残留错误落点。</summary>
        public event System.Action<RemoteShotPresentation, bool> OnShotConfirmed;

        // ---- ③ 生命值网络化 ----

        private readonly SyncVar<int> _health = new();
        private readonly SyncVar<uint> _deathStartedTick = new();
        private readonly SyncVar<bool> _dead = new();
        private DamageableTarget _target;

        // ---- ③b 重生调度与出生保护（Phase 1 服务器权威化，协议 v5：新增 SyncVar ×2）----
        // 旧实现 = 死亡时 Invoke(nameof(ServerRespawn), 3f)（scaled time、deadline 对客户端不可见）；
        // 现 = tick 驱动 deadline，SyncVar 下发全端（HUD 倒计时消费），服务器 Update 到点单次执行。

        /// <summary>重生执行 tick（0=未排队）。服务器写；全端读（RespawnProtectionHudView 倒计时）。</summary>
        private readonly SyncVar<uint> _respawnAtTick = new();
        /// <summary>出生保护截止 tick（0=无保护）。服务器写；全端读（HUD 金色保护条）+ 服务器伤害闸。</summary>
        private readonly SyncVar<uint> _invincibleUntilTick = new();
        /// <summary>当前生命代际：每次服务器重生 +1。纯服务器语义（不进 wire）——
        /// LagComp 快照按采集 tick 携带代际，命中判定据此拒绝"旧生命回溯伤害新生命"（Phase 2）。</summary>
        private ulong _lifeGeneration;
        /// <summary>健康值转发（具名委托，便于 OnStopServer 精确解绑——匿名 lambda 无法解绑会跨生成累积）。</summary>
        private System.Action<int, int> _healthChanged;

        [ObserversRpc(ExcludeOwner = false)]
        private void ObserversDied()
        {
            // 2026-09-16 审计 D3：RPC 只做"同代再求值"，不自成状态源——表现严格由 _dead SyncVar 决定。
            // 这样 RPC 与 SyncVar 的到达先后（RPC 通常先到、SyncVar 下一 tick 才刷）都不会造成
            // 重复 Apply/Reset，也不需要"见过 true"这类锁存。
            OnRemoteDied?.Invoke();
            SyncDeathVisualWithState();
        }

        [ObserversRpc(ExcludeOwner = false)]
        private void ObserversRespawned()
        {
            SyncDeathVisualWithState();
            // 2026-09-18 实机问题1 兜底：观察端远端视觉缓冲立即失效（幂等），下一帧必走对位分支；
            // 并开启 3s [RespawnTrace] 采样（服务器/观察端双侧取证，定位后可拆）。
            GetComponent<PlayerNetworkAdapter>()?.HandleRespawnedFromNetwork();
            // F12（2026-09-19 审计）：Owner 本地预测弹药/两槽缓存对齐重生基线——服务器已在
            // ServerRespawn 权威重置（ServerResetAmmoToLoadoutDefault）并经 SyncVar 下发 HUD，
            // 但 Owner 的本地 Runtime 与切枪缓存仍停在死亡前残弹：HUD 满弹而本地 TryFire 被
            // TryConsumeRound 拒绝（本地表现缺失、与服务器发次分叉）。Host/服务器走本地权威
            // 路径已重置（IsServerInitialized 排除）；远端观察者 Runtime 不 Tick 无需重置。
            if (!IsServerInitialized && FishNetLifecycleGuard.IsLocalOwner(this))
            {
                var ownerWeapons = GetComponentsInChildren<WeaponController>(true);
                for (int i = 0; i < ownerWeapons.Length; i++)
                    ownerWeapons[i].OwnerResetAmmoToRespawnBaseline();
                // F13：重生后后坐随机流以新生命代际重种（两端同键同流）
                GetComponent<PlayerNetworkAdapter>()?.ApplyDeterministicRecoilSeeds();
            }
        }

        /// <summary>远端死亡事件。</summary>
        public event System.Action OnRemoteDied;

        private void Update()
        {
            // 服务器采集生命值（Player 上挂 DamageableTarget 后生效）
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
            {
                // 重生到点消费（Phase 1）：tick 驱动、单次执行，替代历史 Invoke(ServerRespawn, 3f)
                TryConsumeRespawnDue();
                if (_target == null)
                {
                    _target = GetComponentInChildren<DamageableTarget>(true);
                    if (_target != null)
                    {
                        _health.Value = _target.CurrentHealth;
                        _target.OnDied += HandleServerDied;
                        _healthChanged ??= (cur, _) => _health.Value = cur;
                        _target.OnHealthChanged += _healthChanged;
                    }
                }
                // 服务器采集瞄准俯仰 → SyncVar（值变化才写，Docs/23 P0-5）
                TrySyncAimPitch();
            }
            // 死亡/重生表现按权威 SyncVar 幂等收敛（迟到观察者/丢 RPC 自愈；见方法注释）
            SyncDeathVisualWithState();
            // 审计 §5：根位姿（网络插值）在死亡后仍会推动尸体 → 按节流重贴承重面
            if (_deathVisualActive) MaintainDeathPoseOnMovingGround();
        }

        /// <summary>
        /// 重生到点消费（Phase 1）：deadline 单次清零后执行 ServerRespawn——即使 ServerRespawn
        /// 因终局/实例守卫早退也不重试（与旧 Invoke 的"排队定时器自然作废"语义等价，且天然幂等）。
        /// </summary>
        private void TryConsumeRespawnDue()
        {
            TryConsumeRespawnDue(CurrentServerTick());
        }

        /// <summary>internal 薄壳（EditMode 直驱纪律：服务器逻辑可注入 currentTick 直测，RPC/Update 只转发）。</summary>
        internal bool TryConsumeRespawnDue(uint currentTick)
        {
            uint deadline = _respawnAtTick.Value;
            if (deadline == 0u) return false;
            if (currentTick < deadline) return false;
            _respawnAtTick.Value = 0u;
            ServerRespawn();
            return true;
        }

        private void HandleServerDied()
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized) return;
            _deathStartedTick.Value = CurrentServerTick();
            _dead.Value = true; // 死亡表现保留（受击/倒地/禁碰）——authored 目标同样保留世界表现
            // 击杀归因（Docs/23 P1-3）：查登记表得击杀者 → killer kills+1、自己 deaths+1 → 广播 Kill
            var killer = MatchLifecycle.ConsumeKillerOf(_target);
            var killDetail = MatchLifecycle.ConsumeKillDetailOf(_target);
            // Day2 任务 B：死亡/击杀归因路径统一检查 killer 与 victim 的联网比赛资格——
            // authored/server-owned/未认证目标不贡献联网成绩（不加 kills/deaths、不广播 Kill、
            // 不参与 20 杀终局）；真实联网玩家互杀保持原行为（MatchKillAttributionPolicy）。
            // 无归因/killer==自身 时 killerEligible=false（环境死亡只计 victim deaths，原语义）
            bool killerEligible = killer != null && killer != this
                && MatchLifecycle.IsEligibleNetworkPlayer(killer);
            bool victimEligible = MatchLifecycle.IsEligibleNetworkPlayer(this);
            switch (MatchKillAttributionPolicy.Evaluate(killerEligible, victimEligible))
            {
                case MatchKillAttributionPolicy.Outcome.FullAttribution:
                    killer.ServerAddKill();
                    ServerAddDeath();
                    MatchLifecycle.AddTeamKill(MatchLifecycle.ServerSyncPlayerTeam(killer));
                    AwardAssists(_target, killer);
                    MatchLifecycle.BroadcastKill(killer, this, killDetail);
                    break;
                case MatchKillAttributionPolicy.Outcome.DeathOnly:
                    ServerAddDeath(); // 真实玩家的环境死亡/无归因死亡仍计 deaths（原行为）
                    AwardAssists(_target, null);
                    break;
                case MatchKillAttributionPolicy.Outcome.NoScore:
                    // victim 无资格（假人/未认证目标）：零成绩零广播，只保留死亡表现与重生
                    MatchLifecycle.ConsumeAssistsOf(_target, null); // 仅清理助攻登记
                    break;
            }
            ObserversDied();
            // 重生延迟（Phase 1 服务器权威化）：tick 驱动 deadline 经 SyncVar 下发（HUD 倒计时），
            // Update → TryConsumeRespawnDue 到点执行；替代旧 Invoke(nameof(ServerRespawn), 3f)
            // （scaled time 依赖 + deadline 不可见 + 与 tick 判定双轨）。
            // Docs/04 Day9 完整 LifeFSM 仍为远期（本轮以 deadline+代际+保护窗覆盖所需语义）。
            _respawnAtTick.Value = CurrentServerTick()
                + MatchRules.SecondsToTicks(MatchRules.RespawnDelaySeconds, ServerTickRate());
        }

        private void ServerRespawn()
        {
            // Server timer callback, never a client-callable RPC.
            // NetworkObject 先判空（EditMode 直驱安全：IsServerInitialized 属性经 _networkObjectCache 解引用）。
            var nob = NetworkObject;
            if (nob == null || !nob.IsServerInitialized || !_dead.Value || _target == null) return;
            // 终局后不复活（用户规则：比赛已结束时不得重新开放对局操作）：已经排队到期的重生
            // 定时器在此自然作废，死亡状态与倒地表现保留到返回房间/大厅。
            if (MatchLifecycle.Phase == MatchPhase.Ended) return;
            var spawns = SceneSpawnPoints.Current();
            if (spawns.Length == 0)
            {
                // Never revive at the death position when a persistent PlayerSpawner
                // still references an unloaded map. Wait for the live scene markers.
                Debug.LogError("[Respawn] 当前地图没有可用的 Spawn_ 点，保持死亡并在 1 秒后重试", this);
                _respawnAtTick.Value = CurrentServerTick() + MatchRules.SecondsToTicks(1f, ServerTickRate());
                return;
            }
            Vector3[] basePositions = new Vector3[spawns.Length];
            Quaternion[] baseRotations = new Quaternion[spawns.Length];
            for (int i = 0; i < spawns.Length; i++)
            { basePositions[i] = spawns[i].position; baseRotations[i] = spawns[i].rotation; }
            Vector3 spawnPosition;
            Quaternion spawnRotation;
            var occupied = CollectEnemyPositions(); // all living players, including teammates
            if (MatchLifecycle.IsTeamMatch() && (TeamId == MatchRules.TeamRed || TeamId == MatchRules.TeamBlue))
            {
                var red = new List<TeamSpawnDirectory.SpawnSlot>();
                var blue = new List<TeamSpawnDirectory.SpawnSlot>();
                TeamSpawnDirectory.BuildTeamSlots(basePositions, baseRotations, red, blue);
                var slots = (TeamId == MatchRules.TeamRed ? red : blue).ToArray();
                int picked = TeamSpawnDirectory.PickTeamSlot(slots, occupied, 0, out _);
                if (picked < 0)
                {
                    _respawnAtTick.Value = CurrentServerTick() + MatchRules.SecondsToTicks(1f, ServerTickRate());
                    return;
                }
                spawnPosition = slots[picked].Position;
                spawnRotation = slots[picked].Rotation;
            }
            else
            {
                int picked = MatchRules.SelectRespawnPoint(basePositions, occupied);
                if (picked < 0)
                {
                    _respawnAtTick.Value = CurrentServerTick() + MatchRules.SecondsToTicks(1f, ServerTickRate());
                    return;
                }
                spawnPosition = basePositions[picked];
                spawnRotation = baseRotations[picked];
            }
            spawnPosition = SpawnGrounding.Align(spawnPosition, GetComponent<CharacterController>());
            // 生命代际递增（Phase 1/2）：LagComp 历史快照携带旧代际 → 跨生命回溯命中被拒绝
            //（"旧生命误伤新生命"的结构性修复，无需清历史）；同时开启出生保护窗口。
            _lifeGeneration++;
            _executedEquipmentCommand = 0;
            _invincibleUntilTick.Value = CurrentServerTick()
                + MatchRules.SecondsToTicks(MatchRules.SpawnProtectionSeconds, ServerTickRate());
            var locomotor = GetComponent<Game.Gameplay.Movement.Locomotor>();
            if (locomotor != null)
            {
                var snapshot = locomotor.CaptureSnapshot();
                snapshot.Position = spawnPosition;
                snapshot.Rotation = spawnRotation;
                snapshot.HorizontalVelocity = Vector3.zero;
                snapshot.VerticalVelocity = 0f;
                snapshot.LocomotionState = Game.Gameplay.Movement.LocomotionState.Idle;
                snapshot.GaitPhase = 0f;
                // 2026-09-16 审计 M3：重生快照必须把确定性重放状态也写成"地面静止"，
                // 否则客户端会按死亡时刻（可能在空中）的 grounded/coyote/sprintIntent 继续积分。
                snapshot.Grounded = true;
                snapshot.GroundSpeed = 0f;
                snapshot.CoyoteTimer = locomotor.CoyoteSeconds;
                snapshot.LandTimer = 0f;
                snapshot.SprintIntent = false;
                snapshot.RecoilDebt = Vector2.zero;
                // 审计 2026-09-16 §6.2：重生基线俯仰归零——服务器权威俯仰不得停在死亡瞬间的角度
                // （客户端俯仰仍由本地输入驱动；快照携带权威值供两端对账）
                snapshot.Pitch = 0f;
                locomotor.ApplyAuthoritativeSnapshot(snapshot);
            }
            else transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            // 服务器权威俯仰基线同步归零（否则远端观察者看到的 TP 抬头姿态会滞留在死亡角度）
            GetComponent<PlayerNetworkAdapter>()?.ServerResetAimPitch();
            // 2026-09-16 审计 D4：重生是真传送——必须显式通知 NetworkTransform，否则观察者沿旧插值
            // 历史从死亡点滑向出生点（表现先恢复、根位置还在路上）。
            TeleportNetworkTransform();
            _target.ResetHealth();
            // 2026-09-18 实机问题8：重生弹药重置（服务器权威）——两槽弹匣补满+备弹回配装
            // 初始值、切枪弹药缓存清空；SyncVar 经 OnAmmoChanged 推送，客户端 HUD 自动刷新。
            var weaponControllers = GetComponentsInChildren<WeaponController>(true);
            for (int i = 0; i < weaponControllers.Length; i++)
                weaponControllers[i].ServerResetAmmoToLoadoutDefault();
            GetComponent<Game.Gameplay.Combat.ThrowableController>()?.ServerResetInventory();
            // F13（2026-09-19 审计）：服务器权威侧同步重种后坐随机流（与 Owner 同键：
            // weaponId+ownerClientId+新生命代际）——两端 stream 起点一致
            GetComponent<PlayerNetworkAdapter>()?.ApplyDeterministicRecoilSeeds();
            // 2026-09-18 审计 §5：此处原有"重生瞬移后补一次 1mm 沉降 Move 让根 CC 进入 Raycast
            // 命中集"（实机问题6）。玩家移动控制器现在被显式排除在射击判定之外，该 Move 不再
            // 修复任何东西，只会给出生沉降引入一个无人需要的副作用——连同方法一起删除。
            _dead.Value = false;
            ObserversRespawned();
            // [RespawnTrace] 服务器侧一次性取证（2026-09-18 问题1 陷地定位；定位后可拆）
            var ccForTrace = GetComponent<CharacterController>();
            Debug.Log($"[RespawnTrace] server obj={(NetworkObject != null ? NetworkObject.ObjectId : -1)} " +
                $"conn={(NetworkObject != null ? NetworkObject.OwnerId : -1)} spawnY={spawnPosition.y:F3} " +
                $"rootYAfter={transform.position.y:F3} ccEnabled={(ccForTrace != null && ccForTrace.enabled)} " +
                $"ccGrounded={(ccForTrace != null && ccForTrace.enabled && ccForTrace.isGrounded)} " +
                $"weaponsReset={weaponControllers.Length}", this);
        }

        /// <summary>死亡表现是否仍处于"倒地姿态 + 写者冻结未复原"状态。
        /// 与 <see cref="IsDead"/> 的区别（2026-09-18 复核 R1）：复活广播/SyncVar 与本组件复原之间存在
        /// 先后（PlayerNetworkAdapter 的 DefaultExecutionOrder 早于本组件 Update），那一帧 IsDead 已是
        /// false 而 TP_Model 仍是前倾/抬升的尸体位姿。需要"表现真的复位完了"这一事实的调用方必须用本闸，
        /// 不能用 IsDead 代替。同装配体可见（Game.Gameplay），供 PlayerNetworkAdapter 基准门禁使用。</summary>
        internal bool IsDeathVisualActive => _deathVisualActive;

        /// <summary>[RespawnTrace] 取证接缝（2026-09-18 问题1，临时）：死亡表现是否激活（只读）。</summary>
        internal bool DeathVisualActiveForTrace => IsDeathVisualActive;

        /// <summary>
        /// 重生/传送后的网络位姿跳变通知（2026-09-16 审计 D4）：调用 FishNet NetworkTransform.Teleport，
        /// 让观察者立即对位而不是沿旧插值历史滑过（_enableTeleport 由 PlayerNetworkAdapter 运行时置 true）。
        /// 仅服务器侧有意义；无 NetworkTransform 时安全跳过。
        /// </summary>
        private void TeleportNetworkTransform()
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized) return;
            var networkTransform = GetComponent<FishNet.Component.Transforming.NetworkTransform>();
            networkTransform?.Teleport();
        }

        /// <summary>服务器上其他存活玩家的位置（重生点选择输入；死者不算敌人）。</summary>
        private Vector3[] CollectEnemyPositions()
        {
            var others = FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None);
            var positions = new System.Collections.Generic.List<Vector3>(others.Length);
            foreach (var other in others)
            {
                if (other == this || other.IsDead) continue;
                positions.Add(other.transform.position);
            }
            return positions.ToArray();
        }

        /// <summary>助攻计入（战绩面板）：目标死亡时取走助攻名单并计入各射手（服务器侧）。</summary>
        private void AwardAssists(DamageableTarget target, NetworkCombatAuthority killer)
        {
            var assists = MatchLifecycle.ConsumeAssistsOf(target, killer);
            if (assists == null) return;
            foreach (var assistant in assists)
            {
                assistant.ServerAddAssist();
                MatchLifecycle.BroadcastAssist(assistant, this);
            }
        }

        public int Health => _health.Value;
        public bool IsDead => _dead.Value;

        /// <summary>重生执行 tick（0=未排队；HUD 倒计时读，与本地 TimeManager.Tick 对比换算剩余秒）。</summary>
        public uint RespawnAtTick => _respawnAtTick.Value;

        /// <summary>出生保护截止 tick（0=无保护；HUD 金条与服务器伤害闸读）。</summary>
        public uint InvincibleUntilTick => _invincibleUntilTick.Value;

        /// <summary>当前生命代际（每次服务器重生 +1；仅服务器侧有意义，LagComp 命中判定消费）。</summary>
        public ulong LifeGeneration => _lifeGeneration;

        /// <summary>F14（2026-09-19 审计）：生命代际的 uint 视图——输入批次盖章（Owner）与
        /// 批次校验/快照回传（服务器）统一取值口径（ulong→uint 截断在会话寿命内无回绕风险）。</summary>
        internal uint CurrentLifeEpoch => (uint)_lifeGeneration;

        /// <summary>生命代际（表现层可读，S2 预测表现注册表按代际丢弃跨生命残留；
        /// 语义同 CurrentLifeEpoch，仅暴露口径不同）。</summary>
        public uint LifeEpochForPresentation => NetworkObject != null && NetworkObject.IsServerInitialized
            ? (uint)_lifeGeneration : _ownerAmmoLifeEpoch;

        /// <summary>F14 测试接缝：无头直驱递增代际（真实路径在 ServerRespawn 内递增，
        /// 但该路径需要完整 NetworkObject/目标组件——EditMode 用接缝直接推进代际时钟）。</summary>
        internal void BumpLifeGenerationForTests() => _lifeGeneration++;

        /// <summary>指定 tick 是否处于出生保护窗口（服务器判定语义：快照采集 tick &lt; 截止 tick）。</summary>
        public bool IsInvincibleAt(uint tick) => _invincibleUntilTick.Value != 0u && tick < _invincibleUntilTick.Value;

        /// <summary>服务器"当前时刻"是否处于出生保护（DamageableTarget 终闸兜底口径）。</summary>
        public bool IsInvincibleNow => IsInvincibleAt(CurrentServerTick());

        /// <summary>服务器当前 tick（FishNet TimeManager；未初始化回退 0——调用方均在 IsServerInitialized 分支内）。</summary>
        private uint CurrentServerTick()
        {
            var nob = NetworkObject;
            var tm = nob != null ? nob.TimeManager : null;
            return tm != null ? tm.Tick : 0u;
        }

        /// <summary>服务器 tick 节拍（TimeManager.TickRate；未初始化回退 MatchRules.DefaultTickRate）。</summary>
        private int ServerTickRate()
        {
            var nob = NetworkObject;
            var tm = nob != null ? nob.TimeManager : null;
            return tm != null ? tm.TickRate : MatchRules.DefaultTickRate;
        }

        // ---- ④ 瞄准俯仰同步（Docs/23 P0-5，G2） ----

        private readonly SyncVar<float> _aimPitch = new();
        private Transform _aimPivotCached;
        private float _lastSentPitch = float.NaN;

        public override void OnStartClient()
        {
            base.OnStartClient();
            _nextShotRequestId = 0;
            _nextCombatCommandId = 0;
            _submittedEquipmentCommand = _lastSubmittedActionCommand = 0;
            _sentAimIntent = null;
            if (_controller != null)
                _controller.OnShotFired += HandleOwnerPredictedShot;
            if (IsOwnerPlayer) // 生命周期安全判定（authored player 防护，同 IsOwnerPlayer 注释）
            {
                ServerRequestMatchSnapshot();
                // Phase A：本地 Owner 生成 → 挂载游戏菜单（MenuMountPolicy 幂等闸；远端玩家不生成菜单）
                Gameplay.Menu.GameplayMenuController.EnsureMounted();
                // Docs/23 P1-6：本端玩家生成 → 挂载比赛 HUD（TryMount 内部幂等去重）。
                // Gameplay 禁止引用 Presentation（三层单向依赖），按本项目反射惯例
                // （同 PlayerNetworkAdapter.WireRemotePresentation）按名调静态入口，try-null 兜底
                var hudType = System.Type.GetType("Game.Presentation.HUD.MatchHudView, Game.Presentation");
                var mount = hudType?.GetMethod("TryMount",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                mount?.Invoke(null, null);
                // C4/I2：局内聊天挂载（Game.UI 侧引导器；Gameplay 不引用 UI，按同一反射惯例）
                var chatType = System.Type.GetType("Game.UI.Chat.ChatBootstrapper, Game.UI");
                chatType?.GetMethod("TryMount",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.Invoke(null, null);
                return; // Owner 俯仰由 FPMouseLook 本地驱动，不回灌服务器值
            }
            _aimPitch.OnChange += HandleAimPitchChanged;
        }

        public override void OnStopClient()
        {
            if (_controller != null)
                _controller.OnShotFired -= HandleOwnerPredictedShot;
            _aimPitch.OnChange -= HandleAimPitchChanged;
            base.OnStopClient();
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
            // Owner 的 FPMouseLook 是本地基础俯仰唯一写者。若把滞后 SyncVar 回灌 Owner，
            // WeaponController.Update 取射击方向与 LateUpdate 画面会落在不同俯仰帧。
            if (asServer || IsOwnerPlayer) return;
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

        /// <summary>本实例是否为本地客户端所拥有（表现层区分本地/远端玩家用）。
        /// 生命周期安全（Docs/23 离线回归修复）：离线/authored player 视为本地玩家——
        /// authored player 被 SetIsNetworked(false) 后所有权缓存未建立，直接读 FishNet IsOwner 每帧 NRE。</summary>
        public bool IsOwnerPlayer => FishNetLifecycleGuard.IsLocalOwner(this);

        // ---- ⑤ 击杀竞赛比分（Docs/23 P1-3，G4） ----

        private readonly SyncVar<int> _kills = new();
        private readonly SyncVar<int> _deaths = new();
        private readonly SyncVar<int> _assists = new();
        /// <summary>所属队伍（C3/Q04 TDM：服务器权威 SyncVar；CombatResolver 友伤过滤与比分/终局/HUD 消费）。</summary>
        private readonly SyncVar<string> _team = new();
        private readonly SyncVar<string> _displayName = new();

        /// <summary>本局击杀数（服务器权威 SyncVar；HUD/终局判定读）。</summary>
        public int Kills => _kills.Value;

        /// <summary>所属队伍（"None"/"Red"/"Blue"；服务器写、全端读）。</summary>
        public string TeamId => string.IsNullOrEmpty(_team.Value) ? MatchRules.TeamNone : _team.Value;

        /// <summary>服务器认证档案中的游戏名，随玩家对象同步给观察者。</summary>
        public string DisplayName => _displayName.Value;

        // A radar sighting is accepted only after the server checks the observer's team,
        // current view direction and unobstructed line of sight. No client position is trusted.
        public static event Action<int, Vector3> OnTeamRadarSpot;
        private readonly Dictionary<int, float> _radarReportTimes = new();
        private readonly Dictionary<int, float> _radarAcceptTimes = new();
        private float _nextRadarLookupTime;

        public void ReportRadarSighting(NetworkCombatAuthority enemy)
        {
            if (enemy == null || !IsOwnerPlayer || TeamId == MatchRules.TeamNone
                || !FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            var enemyObject = enemy.NetworkObject;
            if (enemyObject == null || !enemyObject.IsSpawned) return;
            int id = enemyObject.ObjectId;
            float now = Time.unscaledTime;
            if (_radarReportTimes.TryGetValue(id, out float last) && now - last < 0.2f) return;
            _radarReportTimes[id] = now;
            ServerReportRadarSighting(id);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerReportRadarSighting(int enemyObjectId)
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized || IsDead
                || !MatchLifecycle.IsTeamMatch()
                || (TeamId != MatchRules.TeamRed && TeamId != MatchRules.TeamBlue)
                || !MatchLifecycle.IsEligibleNetworkPlayer(this)) return;
            float now = Time.unscaledTime;
            if (now < _nextRadarLookupTime) return;
            _nextRadarLookupTime = now + 0.05f;
            if (_radarAcceptTimes.TryGetValue(enemyObjectId, out float last) && now - last < 0.15f) return;
            if (!ServerManager.Objects.Spawned.TryGetValue(enemyObjectId, out var enemyObject)
                || enemyObject == null) return;
            var enemy = enemyObject.GetComponent<NetworkCombatAuthority>();
            if (enemy == null || enemy == this || enemy.IsDead || enemy.TeamId == TeamId
                || !MatchLifecycle.IsEligibleNetworkPlayer(enemy)) return;
            if (_radarAcceptTimes.Count >= 32) _radarAcceptTimes.Clear();
            _radarAcceptTimes[enemyObjectId] = now;

            Vector3 origin = transform.position + Vector3.up * 1.55f;
            Vector3 target = enemy.transform.position + Vector3.up * 1.2f;
            Vector3 delta = target - origin;
            float distance = delta.magnitude;
            if (distance < 0.1f || distance > 85f || Vector3.Angle(AimDirectionWorld, delta) > 55f) return;
            foreach (var hit in Physics.RaycastAll(origin, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore))
            {
                var hitPlayer = hit.collider.GetComponentInParent<NetworkCombatAuthority>();
                if (hitPlayer == this || hitPlayer == enemy) continue;
                return;
            }

            foreach (var spawned in ServerManager.Objects.Spawned.Values)
            {
                var teammate = spawned != null ? spawned.GetComponent<NetworkCombatAuthority>() : null;
                if (teammate == null || teammate.TeamId != TeamId
                    || !MatchLifecycle.IsEligibleNetworkPlayer(teammate)) continue;
                var teammateObject = teammate.NetworkObject;
                if (teammateObject?.Owner == null) continue;
                if (teammateObject.Owner.IsLocalClient)
                    OnTeamRadarSpot?.Invoke(enemyObjectId, enemy.transform.position);
                else
                    teammate.TargetRadarSpot(teammateObject.Owner, enemyObjectId, enemy.transform.position);
            }
        }

        [TargetRpc]
        private void TargetRadarSpot(FishNet.Connection.NetworkConnection connection, int enemyObjectId, Vector3 position)
            => OnTeamRadarSpot?.Invoke(enemyObjectId, position);

        // ---- EditMode 测试接缝（InternalsVisibleTo；归因顺序回归直驱 SyncVar） ----
        internal void SetTeamForTests(string team) => _team.Value = team;
        internal void SetInvincibleUntilTickForTests(uint tick) => _invincibleUntilTick.Value = tick;

        /// <summary>本局入场时刻（服务器 realtime；I3：补入/重连从重生成时刻起算参与时长，Docs/26 §2.4）。</summary>
        private float _matchJoinedRealtime = -1f;
        public float MatchJoinedRealtime => _matchJoinedRealtime;

        /// <summary>服务器权威设置队伍（MatchLifecycle.SyncPlayerTeams 在倒计时/认证档案就绪后调用）。</summary>
        public void ServerSetTeam(string teamId)
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized) return;
            _team.Value = string.IsNullOrEmpty(teamId) ? MatchRules.TeamNone : teamId;
        }

        /// <summary>本局死亡数。</summary>
        public int Deaths => _deaths.Value;

        /// <summary>本局助攻数（服务器权威 SyncVar；战绩面板读）。</summary>
        public int Assists => _assists.Value;

        internal void ServerAddKill()
        {
            if (!MatchLifecycle.AllowsCombat(true)) return;
            if (NetworkObject != null && NetworkObject.IsServerInitialized) _kills.Value++;
        }

        internal void ServerAddDeath()
        {
            if (!MatchLifecycle.AllowsCombat(true)) return;
            if (NetworkObject != null && NetworkObject.IsServerInitialized) _deaths.Value++;
        }

        internal void ServerAddAssist()
        {
            if (!MatchLifecycle.AllowsCombat(true)) return;
            if (NetworkObject != null && NetworkObject.IsServerInitialized) _assists.Value++;
        }

        /// <summary>服务器：新对局倒计时开始时清零比分（MatchLifecycle 调用）。</summary>
        internal void ServerResetScore()
        {
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
            {
                _kills.Value = 0;
                _deaths.Value = 0;
                _assists.Value = 0;
            }
        }

        // ---- ⑤b 自报 ping（战绩面板）：客户端周期上报本端 RTT；服务器缓存供战绩快照下发。
        // FishNet 4.7.2 无逐连接 RTT 公开 API（TimeManager.RoundTripTime 仅客户端本端语义）。 ----

        private int _lastReportedPingMs;

        /// <summary>该玩家最新自报 ping（ms；0=未知）。仅服务器侧有意义。</summary>
        public int LastPingMs => _lastReportedPingMs;

        /// <summary>客户端（Owner）上报本端 RTT（低频，战绩面板 2s 周期驱动）。</summary>
        public void SubmitPingReport(long pingMs)
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerReportPing(pingMs);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerReportPing(long pingMs)
        {
            if (pingMs < 0 || pingMs > 5000) return; // 越界值丢弃（异常/防篡改）
            _lastReportedPingMs = (int)pingMs;
        }

        // ---- ⑥ 比赛事件中继（Docs/23 P1-4）：MatchLifecycle（纯 MonoBehaviour）经此广播 ----

        /// <summary>比赛事件（客户端镜像 + HUD 消费）。铁律自检：只传 Gameplay 语义（枚举 + JSON 字符串）。</summary>
        public static event System.Action<MatchEventKind, string> OnMatchEvent;

        private double _nextMatchSnapshotRequest;

        [ServerRpc(RequireOwnership = true)]
        private void ServerRequestMatchSnapshot()
        {
            if (Time.unscaledTimeAsDouble < _nextMatchSnapshotRequest) return;
            _nextMatchSnapshotRequest = Time.unscaledTimeAsDouble + 1;
            TargetMatchSnapshot(Owner, MatchLifecycle.BuildCurrentSnapshotJson());
        }

        [TargetRpc]
        private void TargetMatchSnapshot(FishNet.Connection.NetworkConnection connection, string payload)
            => OnMatchEvent?.Invoke(MatchEventKind.ScoreboardSnapshot, payload);

        /// <summary>服务器中继入口（MatchLifecycle 调用；非服务器调用安全忽略）。</summary>
        public void ServerRelayMatchEvent(MatchEventKind kind, string payload)
        {
            if (NetworkObject != null && NetworkObject.IsServerInitialized)
                ObserversMatchEvent(kind, payload);
        }

        [ObserversRpc(ExcludeOwner = false, ExcludeServer = false, RunLocally = true)]
        private void ObserversMatchEvent(MatchEventKind kind, string payload)
        {
            OnMatchEvent?.Invoke(kind, payload);
        }

        // ---- ⑧ 局内聊天 Owner RPC（C4/I2，Docs/27 §8.4 + 复审 R06）：----
        // 上行 RequireOwnership=true（默认严格所有权校验，身份从连接/认证档案取，不从请求取）；
        // 下行 TargetRpc 按收件人逐连接定向（Team 投递层过滤，禁止全员广播后客户端隐藏）；
        // 通道校验/限频/去重/seq 全部走 ChatRelayCore（每局 epoch=matchId 独立 seq 空间）。

        /// <summary>聊天接收事件（服务器与客户端统一镜像；UI ChatController 消费）。</summary>
        public static event System.Action<ChatRelayCore.ChatMessage> OnChatMessageReceived;

        /// <summary>Owner 客户端上行（菜单/输入态由 UI 侧把关；服务器仍做全量校验）。</summary>
        public void SubmitChatRequest(string channel, string body, string clientMessageId)
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            NetworkObject networkObject = NetworkObject;
            if (networkObject.IsOwner && !networkObject.IsServerInitialized)
                ServerSendChatRequest(channel, body, clientMessageId);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerSendChatRequest(string channel, string body, string clientMessageId)
        {
            if (NetworkObject == null || !NetworkObject.IsServerInitialized) return;
            var serverStarted = InstanceFinder.NetworkManager != null && InstanceFinder.NetworkManager.IsServerStarted;
            if (!serverStarted) return;

            // 身份注入：认证档案是唯一事实源（伪造频道/身份直接丢弃并记日志）
            var nob = NetworkObject;
            var authenticator = InstanceFinder.NetworkManager != null
                ? InstanceFinder.NetworkManager.GetComponent<JoinTicketAuthenticator>() : null;
            if (authenticator == null
                || !authenticator.AcceptedUsers.TryGetValue(nob.Owner.ClientId, out var profile)
                || profile == null || !profile.Accepted)
            {
                Debug.LogWarning($"[ChatRelay] 未认证连接的聊天请求被丢弃 conn={nob.Owner.ClientId}");
                return;
            }
            if (!long.TryParse(profile.UserId, out long senderUserId))
                senderUserId = 0; // 调试旁路（debug-*）：仅本局回显，不进后端结算链

            var epoch = MatchLifecycle.ClientMatchId;
            var result = ChatRelayCore.TryAccept(epoch, senderUserId, profile.Username,
                string.IsNullOrEmpty(TeamId) ? MatchRules.TeamNone : TeamId,
                channel, body, clientMessageId, DateTime.UtcNow);
            if (result.Outcome == ChatRelayCore.AcceptOutcome.Rejected)
            {
                Debug.LogWarning($"[ChatRelay] 聊天被拒 user={profile.UserId}: {result.RejectReason}");
                return;
            }
            if (result.Outcome == ChatRelayCore.AcceptOutcome.RateLimited)
            {
                Debug.LogWarning($"[ChatRelay] 聊天限频 user={profile.UserId} retryAfter={result.RetryAfterSeconds}s");
                return; // 限频静默丢弃（Owner RPC 无带内错误通道；客户端令牌桶预检为主防线）
            }

            DeliverChat(result.Message, nob.Owner);
        }

        /// <summary>服务器权威投递：按收件人集合逐连接定向（Team 投递层过滤；资格普查同 MatchLifecycle 口径）。</summary>
        public static void DeliverChat(ChatRelayCore.ChatMessage message, FishNet.Connection.NetworkConnection exclude = null)
        {
            if (message == null) return;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (!MatchLifecycle.IsEligibleNetworkPlayer(player)) continue;
                var nob = player.NetworkObject;
                if (nob?.Owner == null || nob.Owner.ClientId < 0) continue;
                if (exclude != null && nob.Owner == exclude && !nob.IsServerInitialized) continue;
                if (!ChatRelayCore.IsDeliverable(message, player.TeamId)) continue;
                if (nob.Owner.IsLocalClient)
                {
                    OnChatMessageReceived?.Invoke(message); // 服务器本机 client（host/F1 调试）直接镜像
                    continue;
                }
                player.TargetChatMessage(nob.Owner, message.Epoch, message.Seq, message.Channel,
                    message.SenderUserId, message.SenderUsername, message.TeamId, message.Body, message.ClientMessageId);
            }
        }

        /// <summary>服务器构造系统消息并全员投递（进出/准备/设置/开始失败/倒计时/队伍获胜/返房/补人；无客户端发送路径）。</summary>
        public static void ServerBroadcastChatSystem(string body)
        {
            var epoch = MatchLifecycle.ClientMatchId;
            var message = ChatRelayCore.BuildSystem(epoch, body);
            if (message == null) return;
            DeliverChat(message);
        }

        [TargetRpc]
        private void TargetChatMessage(FishNet.Connection.NetworkConnection connection, string epoch, ulong seq,
            string channel, long senderUserId, string senderUsername, string teamId, string body, string clientMessageId)
        {
            OnChatMessageReceived?.Invoke(new ChatRelayCore.ChatMessage
            {
                Epoch = epoch,
                Seq = seq,
                Channel = channel,
                SenderUserId = senderUserId,
                SenderUsername = senderUsername,
                TeamId = teamId,
                Body = body,
                ClientMessageId = clientMessageId,
            });
        }

        // ---- ⑦ 死亡/重生简式表现（Docs/23 P1-6：TP 根前倾 85° + 禁受击碰撞；代码完成零资产改动） ----

        private Transform _tpModelCached;
        private Vector3 _tpModelSavedPosition;
        private Quaternion _tpModelSavedRotation;
        /// <summary>死亡存档位姿有效性（2026-09-19 第四轮）：TryGetAuthoredTpLocalPose 的闸。</summary>
        private bool _tpModelSavedPoseValid;
        /// <summary>死亡期间被停用的姿态写者及其**原始 enable 状态**（审计 2026-09-16 D2：
        /// 只按名字停 Animator/TPAnim/Animancer 漏掉了 TPAimDriver/TPLeftHandIK——它们在 Animator
        /// 停止后仍会每帧把同一 pitch 叠加到已冻结的骨骼上，持续扭曲并让贴地包围盒失效）。</summary>
        private readonly System.Collections.Generic.List<(Behaviour behaviour, bool wasEnabled)> _deathPoseWriters = new();
        private readonly List<(Collider collider, bool enabled)> _deathColliders = new();
        /// <summary>死亡表现幂等闸：Apply/Reset 重入不得覆盖保存的姿态与写者状态。</summary>
        private bool _deathVisualActive;
        private bool _tpModelMissingLogged;
        private float _nextTpModelLookupTime;

        /// <summary>姿态写者类型名标记（显式清单，审计 D2）：命中即死亡期间停用、复活精确还原。
        /// 新增骨骼写者（IK/Aim/动画驱动器）必须在此登记——这是"死亡生命周期统一管理姿态写者"的契约。</summary>
        private static readonly string[] PoseWriterNameMarkers =
        {
            "Animancer", "TPAnim", "TPAim", "TPLeftHand", "FPRightHand", "FPLeftHand", "TwoBoneIKSolver",
        };

        /// <summary>是否为骨骼/动画姿态写者（Animator 或显式清单命中）。</summary>
        private static bool IsPoseWriter(Behaviour behaviour)
        {
            if (behaviour is Animator) return true;
            string name = behaviour.GetType().Name;
            for (int i = 0; i < PoseWriterNameMarkers.Length; i++)
                if (name.Contains(PoseWriterNameMarkers[i])) return true;
            return false;
        }

        /// <summary>倒地姿态与地面之间的间隙（米）：避免包围盒下沿与地面 z-fighting/穿模。</summary>
        private const float DeathGroundClearance = 0.02f;
        /// <summary>地面探测起点在姿态包围盒上方的高度（米）。</summary>
        private const float DeathGroundProbeHeight = 2f;
        /// <summary>地面探测在姿态包围盒下方继续下探的深度（米；台阶/斜坡下探）。</summary>
        private const float DeathGroundProbeDepth = 6f;
        /// <summary>地面探测横向探针半径上限（米）：倒地姿态横躺，单点探针在斜坡/台阶上会误判。</summary>
        private const float DeathGroundProbeSpread = 1.2f;
        /// <summary>承重面最小法线仰角余弦（低于此值 = 墙面/陡坡，不承重；审计 §5）。</summary>
        private const float DeathSupportMinNormalY = 0.5f;
        /// <summary>同一支撑面带容差（米）：与中位面同带内的探针才计入。</summary>
        private const float DeathSupportBandMeters = 0.25f;
        /// <summary>贴地允许的单次上下修正上限（米）：超出视为探测异常，不动姿态（绝不猜）。</summary>
        private const float DeathGroundMaxAdjustMeters = 3f;
        /// <summary>死亡期间根位姿变化后重贴的节流（秒；网络插值把尸体推着走时收敛）。</summary>
        private const float DeathRealignIntervalSeconds = 0.25f;
        /// <summary>根位移超过该值才值得重贴（米）。</summary>
        private const float DeathRealignMinRootMove = 0.05f;

        private float _nextDeathRealignTime;
        private Vector3 _deathLastRootPosition;
        private readonly System.Collections.Generic.List<float> _probeHeights = new();
        private readonly System.Collections.Generic.List<string> _probeLabels = new();
        private static Mesh _bakeMesh;

        /// <summary>TP 模型节点：优先直查 TP_Model（重建先例），回退首个名字含 TP 的直接子节点；try-null 兜底。
        /// 查找失败按 1s 节流并只告警一次——SyncVar 收敛路径每帧调用，不能每帧遍历层级/刷屏。</summary>
        private Transform FindTpModel()
        {
            if (_tpModelCached != null) return _tpModelCached;
            if (Time.unscaledTime < _nextTpModelLookupTime) return null;
            _nextTpModelLookupTime = Time.unscaledTime + 1f;
            _tpModelCached = transform.Find("TP_Model");
            if (_tpModelCached == null)
            {
                foreach (Transform child in transform)
                {
                    if (child.name.Contains("TP")) { _tpModelCached = child; break; }
                }
            }
            if (_tpModelCached == null)
            {
                if (!_tpModelMissingLogged)
                {
                    _tpModelMissingLogged = true;
                    Debug.LogWarning("[NetworkCombatAuthority] 未找到 TP 模型节点，死亡倒地表现跳过", this);
                }
                return null;
            }
            _tpModelMissingLogged = false;
            return _tpModelCached;
        }

        private void ApplyDeathVisual()
        {
            if (_deathVisualActive) return; // 幂等：重复/乱序的死亡广播不再二次应用
            // 2026-09-16 审计 §4：Owner 的第一人称表现有自己的死亡边界（必须显式重建腰射 Idle，
            // 见 FPWeaponAnimator.ApplyRespawnState）——它不受本方法后面的 TP 姿态冻结影响。
            ApplyOwnerFpDeathState(true);
            if (_target != null && _deathColliders.Count == 0)
            {
                foreach (var hitCollider in _target.GetComponentsInChildren<Collider>(true))
                {
                    if (Game.Gameplay.Combat.CombatResolver.IsMovementBlocker(hitCollider)) continue;
                    _deathColliders.Add((hitCollider, hitCollider.enabled));
                    hitCollider.enabled = false;
                }
            }
            var tpModel = FindTpModel();
            if (tpModel == null) return;
            _deathVisualActive = true; // 只有真正记录了可还原状态才上闸（未找到模型时允许后续重试）
            // §4.1（2026-09-18 审计）顺序修正：**先**保存 TP 根位姿，再做任何可能触发
            // Animator.Rebind 的动作。原实现"先停写者、后保存"，保存到的可能是已被 Reset 改过的值。
            _tpModelSavedPosition = tpModel.localPosition;
            _tpModelSavedRotation = tpModel.localRotation;
            _tpModelSavedPoseValid = true; // 2026-09-19 第四轮：受击体钉扎的退用作者基准自此可用
            // §4.1 冻结方式：走 TP 生命周期契约（暂停图），不依赖组件停用的副作用——
            // prefab 里 AnimancerComponent `_ActionOnDisable=3`（Reset）会 Graph.Stop + Rebind，
            // OnEnable 只 UnpauseGraph，于是复活后图里没有任何状态在播（重生陷地的直接机制）。
            CollectTpPoseLifecycle(tpModel);
            var nowTick = CurrentServerTick();
            var elapsed = _deathStartedTick.Value > 0 && nowTick >= _deathStartedTick.Value
                ? (nowTick - _deathStartedTick.Value) / (float)ServerTickRate() : 0f;
            InvokeTpLifecycle(lifecycle => lifecycle.PlayDeathPose(elapsed));
            // 其余骨骼写者（TPAim / 双手 IK 等）仍逐个停用并记录原始 enable 状态（审计 D2 契约）；
            // Animancer/Animator 例外——它们的冻结由上面的图暂停承担，停用会触发 Rebind。
            _deathPoseWriters.Clear();
            foreach (var behaviour in tpModel.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null || behaviour == this) continue;
                if (!IsPoseWriter(behaviour)) continue;
                if (IsGraphFrozenWriter(behaviour) || behaviour is Game.Gameplay.Animation.IThirdPersonPoseLifecycle) continue;
                _deathPoseWriters.Add((behaviour, behaviour.enabled));
                behaviour.enabled = false;
            }
            _deathGroundSettled = false;
            // 2026-09-15 审计 §3：倒地支点在模型原点，前倾后包围盒下沿会穿入地面（实机"下半身埋地"）。
            // 贴地修正只作用于本视觉节点——不动模拟根、不硬编码地图高度、不抬高活人模型。
            _deathLastRootPosition = transform.position;
            _nextDeathRealignTime = Time.unscaledTime + DeathRealignIntervalSeconds;
            if (_deathTpLifecycle.Count == 0) AlignDeathPoseToGround(tpModel);
            Debug.Log($"[DeathVisual] 倒地表现 applied poseWriters={_deathPoseWriters.Count} "
                + $"tpFrozen={_deathTpLifecycle.Count} grounded={_tpModelSavedPosition} "
                + $"lifted={tpModel.localPosition.y - _tpModelSavedPosition.y:F3}", this);
        }

        private void ResetDeathVisual()
        {
            bool wasActive = _deathVisualActive;
            _deathVisualActive = false; // 复活后重新武装：下一次死亡照常应用
            ApplyOwnerFpDeathState(false); // 审计 §4：复活必须重建腰射 Idle（不只是恢复 enabled）
            foreach (var entry in _deathColliders)
                if (entry.collider != null) entry.collider.enabled = entry.enabled;
            _deathColliders.Clear();
            if (!wasActive) return; // 未记录过姿态/写者状态：不得用默认值覆盖（迟到复活广播）
            var tpModel = FindTpModel();
            if (tpModel != null)
            {
                // 完整还原：位置（贴地抬升可能改过）+ 旋转（原实现只还原旋转，位置一旦被改就永久错位）
                tpModel.localPosition = _tpModelSavedPosition;
                tpModel.localRotation = _tpModelSavedRotation;
            }
            // 精确还原每个写者**原本**的 enable 状态（死亡前本就关闭的不得被打开）
            foreach (var entry in _deathPoseWriters)
                if (entry.behaviour != null) entry.behaviour.enabled = entry.wasEnabled;
            _deathPoseWriters.Clear();
            // §4.2 复活重建：必须在写者还原**之后**——驱动器的缓存状态/动作层/步态相位在这里
            // 显式清理并强制重播当前有效 locomotion（复活基线 Idle），就地求值一帧；
            // 只把 enabled 设回 true 不够（图已 Stop，状态缓存仍是 Idle → 永不重播）。
            InvokeTpLifecycle(lifecycle => lifecycle.RecoverPoseAfterRespawn());
            _deathTpLifecycle.Clear();
            // §4.2 远端平滑：在"真正完成复位"之后失效/对位一次。ObserversRespawned 先到时的
            // 那次失效不能替代本处（RPC 早于 SyncVar 时它看到的还是倒地姿态）。
            GetComponent<PlayerNetworkAdapter>()?.InvalidateRemoteVisualSmoothing();
            // 2026-09-19 第四轮：死亡期间 EnsureBodyHitbox 若被重入（池化复用 OnStartNetwork），
            // 钉扎可能被倒地瞬态姿态污染——此刻模型已还原作者位姿、死亡门禁解除，显式重钉兜底。
            GetComponent<PlayerNetworkAdapter>()?.RepinBodyHitboxAfterRestore();
        }

        /// <summary>死亡时保存的 TP_Model 作者局部位姿（2026-09-19 第四轮）：受击体钉扎在
        /// baseline 不可用（如死亡期间被重入）时的退用基准——ApplyDeathVisual 在任何可能
        /// 触发 Rebind 的动作**之前**保存，就是未经瞬态污染的作者位姿。未发生过死亡时返回 false。</summary>
        internal bool TryGetAuthoredTpLocalPose(out Vector3 localPos, out Quaternion localRot)
        {
            localPos = _tpModelSavedPosition;
            localRot = _tpModelSavedRotation;
            return _tpModelSavedPoseValid;
        }

        /// <summary>死亡期间收集到的 TP 生命周期实现（Presentation 的 TP 驱动器；复活后清空）。</summary>
        private readonly System.Collections.Generic.List<UnityEngine.Component> _deathTpLifecycle = new();

        /// <summary>
        /// 收集 TP 子树内实现 <see cref="Game.Gameplay.Animation.IThirdPersonPoseLifecycle"/> 的组件。
        /// 遍历 MonoBehaviour 再用 is 匹配接口，不用 GetComponentsInChildren(Type)——后者按 Component
        /// 类型过滤，接口类型不在其文档保证范围内。范围限定 TP_Model 子树：Gameplay 不引用
        /// Animancer/第三方与 Presentation（三层单向依赖），低层定契约、高层实现——
        /// 替代 FPWeaponRig 那套反射桥（本票 §4.2 择一并记录）。
        /// </summary>
        private void CollectTpPoseLifecycle(Transform tpModel)
        {
            _deathTpLifecycle.Clear();
            if (tpModel == null) return;
            var found = tpModel.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] is Game.Gameplay.Animation.IThirdPersonPoseLifecycle)
                    _deathTpLifecycle.Add(found[i]);
            }
        }

        /// <summary>逐个调用 TP 生命周期入口（空子树/离线/纯 DS 无表现层时安全跳过）。
        /// 已解冻的实例（重复复活广播）不得再被调用第二次——入口自身幂等。</summary>
        private void InvokeTpLifecycle(System.Action<Game.Gameplay.Animation.IThirdPersonPoseLifecycle> invoke)
        {
            for (int i = 0; i < _deathTpLifecycle.Count; i++)
            {
                if (_deathTpLifecycle[i] is Game.Gameplay.Animation.IThirdPersonPoseLifecycle lifecycle)
                    invoke(lifecycle);
            }
        }

        /// <summary>该写者是否由"暂停图"冻结（不得停用）：Animator 与 Animancer 的图组件。
        /// 按类型名判定——Gameplay 不引用 Animancer 程序集（asmdef 只有 Core/InputSystem/FishNet）。</summary>
        private static bool IsGraphFrozenWriter(Behaviour behaviour)
        {
            if (behaviour is Animator) return true;
            var name = behaviour.GetType().Name;
            return name == "AnimancerComponent" || name == "AnimancerSamplerJobComponent";
        }

        // ---- 2026-09-16 审计 §4：Owner 第一人称死亡/复活表现入口（Gameplay → Presentation 反射调用）----

        private static System.Type _fpRigType;
        private static bool _fpRigTypeResolved;
        private static System.Reflection.MethodInfo _fpDeathMethod;
        private static System.Reflection.MethodInfo _fpRespawnMethod;

        /// <summary>
        /// 驱动本地玩家的 FP 表现死亡/复活边界。Gameplay 不允许引用 Presentation（三层单向依赖），
        /// 按本项目既有反射惯例（同 MatchHudView.TryMount / ChatBootstrapper.TryMount）调用表现层
        /// 公开入口；解析失败静默跳过（离线/无 FP 视图/自定义结构）。
        /// </summary>
        private void ApplyOwnerFpDeathState(bool dead)
        {
            if (!IsOwnerPlayer) return;
            if (!_fpRigTypeResolved)
            {
                _fpRigTypeResolved = true;
                _fpRigType = System.Type.GetType("Game.Presentation.Animation.FPWeaponRig, Game.Presentation");
                if (_fpRigType != null)
                {
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                    _fpDeathMethod = _fpRigType.GetMethod("ApplyDeathState", flags);
                    _fpRespawnMethod = _fpRigType.GetMethod("ApplyRespawnState", flags);
                }
            }
            if (_fpRigType == null) return;
            var method = dead ? _fpDeathMethod : _fpRespawnMethod;
            if (method == null) return;
            var rigs = GetComponentsInChildren(_fpRigType, true);
            for (int i = 0; i < rigs.Length; i++)
            {
                var rig = rigs[i];
                if (rig == null) continue;
                // 只对当前生效的 rig 生效（池化多武器视图里未启用的是非所有者）
                if (rig is Behaviour behaviour && !behaviour.enabled) continue;
                try { method.Invoke(rig, null); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[DeathVisual] FP {method.Name} 调用失败：{e.Message}", this);
                }
            }
        }

        /// <summary>
        /// 倒地姿态贴地（2026-09-15 审计 §3；2026-09-16 审计 §5 重做）：
        /// 按"承重面"把姿态包围盒下沿对齐到地面。三项修正：
        /// ① 支撑面必须过滤——排除自身层级、**所有玩家**（含其它玩家的 CC/hitbox/TP 网格）、
        ///    触发器、非承重效果体，并要求法线足够朝上（旧实现取"五探针里最高的任何面"，
        ///    会把旁边站着的玩家胶囊或箱顶当成尸体地面 → 实测浮空 1.47m）；
        /// ② 归并统计改为**带内中位面**（多数探针同带才采纳），不再取最大值；
        /// ③ 允许**有界上下修正**（旧实现只允许上修 → 已经浮空的尸体永远落不下来）。
        /// 探测不到明确支撑面时不猜测、不动姿态。
        /// </summary>
        private void AlignDeathPoseToGround(Transform tpModel)
        {
            if (!TryGetPoseBounds(tpModel, out var poseBounds, out string boundsSource)) return;
            if (!TryProbeGround(poseBounds, out float groundY, out string probeEvidence))
            {
                Debug.Log($"[DeathVisual] ground skip source={boundsSource} lowest={poseBounds.min.y:F3} "
                    + $"probes=[{probeEvidence}]（无明确承重面，不猜测）", this);
                return;
            }
            float lowest = poseBounds.min.y;
            float delta = groundY + DeathGroundClearance - lowest;
            if (Mathf.Abs(delta) <= 0.0005f) return;
            if (Mathf.Abs(delta) > DeathGroundMaxAdjustMeters)
            {
                Debug.LogWarning($"[DeathVisual] ground reject delta={delta:F3}m > {DeathGroundMaxAdjustMeters}m "
                    + $"source={boundsSource} probes=[{probeEvidence}]（疑似探测异常，保持原姿态）", this);
                return;
            }
            tpModel.position += Vector3.up * delta;
            Debug.Log($"[DeathVisual] ground align source={boundsSource} lowest={lowest:F3} groundY={groundY:F3} "
                + $"delta={delta:F3}m probes=[{probeEvidence}]", this);
        }

        /// <summary>
        /// 死亡期间根位姿变化后的重贴（审计 §5："只在死亡进入执行一次，后续根插值变化也不会重新贴合"）：
        /// 观察者先看到倒地广播、根随后才被 NetworkTransform 插值到位，尸体世界位姿会跟着根滑动。
        /// 节流 + 只在根确实移动过时重算（有界上下修正，收敛后 delta→0 不再动）。
        /// </summary>
        private bool _deathGroundSettled;
        private void MaintainDeathPoseOnMovingGround()
        {
            foreach (var component in _deathTpLifecycle)
                if (component is Game.Gameplay.Animation.IThirdPersonPoseLifecycle life && !life.DeathPoseComplete) return;

            if (Time.unscaledTime < _nextDeathRealignTime) return;
            _nextDeathRealignTime = Time.unscaledTime + DeathRealignIntervalSeconds;
            if (_deathGroundSettled && Vector3.Distance(transform.position, _deathLastRootPosition) < DeathRealignMinRootMove) return;
            _deathGroundSettled = true;
            _deathLastRootPosition = transform.position;
            var tpModel = _tpModelCached;
            if (tpModel == null) return;
            AlignDeathPoseToGround(tpModel);
        }

        /// <summary>
        /// 姿态世界包围盒：优先使用**当前已评估的蒙皮顶点**（死亡时一次 BakeMesh；Renderer.localBounds
        /// 是绑定姿态盒，躺着时并不等于实际姿态），失败回退到 localBounds 8 角点。
        /// 武器/配件 Renderer 一律排除（它们不属于躯干支撑面，且会显著放大包围盒）。
        /// </summary>
        private static bool TryGetPoseBounds(Transform tpModel, out Bounds worldBounds, out string source)
        {
            worldBounds = default;
            source = "none";
            bool any = false;
            bool usedBake = false;
            bool usedLocal = false;
            foreach (var renderer in tpModel.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || IsWeaponAttachmentRenderer(renderer, tpModel)) continue;
                if (renderer is SkinnedMeshRenderer skinned && TryBakeBounds(skinned, ref worldBounds, ref any))
                {
                    usedBake = true;
                    continue;
                }
                Bounds localBounds;
                if (renderer is SkinnedMeshRenderer skinnedFallback)
                {
                    localBounds = skinnedFallback.localBounds;
                }
                else
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    localBounds = filter.sharedMesh.bounds;
                }
                var matrix = renderer.transform.localToWorldMatrix;
                Vector3 center = localBounds.center;
                Vector3 extents = localBounds.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        center.x + ((i & 1) == 0 ? -extents.x : extents.x),
                        center.y + ((i & 2) == 0 ? -extents.y : extents.y),
                        center.z + ((i & 4) == 0 ? -extents.z : extents.z));
                    Vector3 world = matrix.MultiplyPoint3x4(corner);
                    if (!any) { worldBounds = new Bounds(world, Vector3.zero); any = true; }
                    else worldBounds.Encapsulate(world);
                }
                usedLocal = true;
            }
            source = usedBake && usedLocal ? "bake+local" : usedBake ? "bake" : usedLocal ? "local" : "none";
            return any;
        }

        /// <summary>把一具已评估姿态的蒙皮顶点并进包围盒；失败（无蒙皮/不可读）返回 false 由调用方回退。</summary>
        private static bool TryBakeBounds(SkinnedMeshRenderer skinned, ref Bounds bounds, ref bool any)
        {
            if (skinned.sharedMesh == null || !skinned.gameObject.activeInHierarchy) return false;
            try
            {
                if (_bakeMesh == null)
                {
                    _bakeMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }
                skinned.BakeMesh(_bakeMesh);
                var vertices = _bakeMesh.vertices;
                if (vertices == null || vertices.Length == 0) return false;
                var matrix = skinned.transform.localToWorldMatrix;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 world = matrix.MultiplyPoint3x4(vertices[i]);
                    if (!any) { bounds = new Bounds(world, Vector3.zero); any = true; }
                    else bounds.Encapsulate(world);
                }
                return true;
            }
            catch (System.Exception)
            {
                return false; // 回退到 localBounds 路径（不因诊断/几何失败影响死亡表现）
            }
        }

        /// <summary>武器/配件网格判定（按名字链，审计 §5"排除武器/附件"）。</summary>
        private static bool IsWeaponAttachmentRenderer(Renderer renderer, Transform stopAt)
        {
            for (var t = renderer.transform; t != null && t != stopAt; t = t.parent)
            {
                string name = t.name;
                for (int i = 0; i < WeaponNodeMarkers.Length; i++)
                    if (name.IndexOf(WeaponNodeMarkers[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            return false;
        }

        private static readonly string[] WeaponNodeMarkers =
        {
            "weapon", "gun", "rifle", "pistol", "mag", "sight", "optic", "attach", "muzzle", "scope", "knife", "ammo",
        };

        /// <summary>
        /// 承重面探测（中心 + 四向共 5 探针）：每个探针取该列**最上面**的有效承重面，
        /// 再用带内中位面归并（多数探针同带才采纳）——"任何上方最高面"归并会把旁边玩家/箱顶
        /// 当成尸体地面（实测浮空 1.47m 的直接原因）。
        /// </summary>
        private bool TryProbeGround(Bounds poseBounds, out float groundY, out string evidence)
        {
            groundY = 0f;
            _probeHeights.Clear();
            _probeLabels.Clear();
            float top = poseBounds.max.y + DeathGroundProbeHeight;
            float distance = top - poseBounds.min.y + DeathGroundProbeDepth;
            float spread = Mathf.Min(
                Mathf.Max(poseBounds.extents.x, poseBounds.extents.z), DeathGroundProbeSpread);
            Vector3 center = poseBounds.center;

            void Probe(Vector3 at, string label)
            {
                var origin = new Vector3(at.x, top, at.z);
                var hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
                int best = -1;
                for (int i = 0; i < hits.Length; i++)
                {
                    if (!IsDeathSupport(hits[i], out _)) continue;
                    if (best < 0 || hits[i].distance < hits[best].distance) best = i;
                }
                if (best < 0) return;
                var hit = hits[best];
                _probeHeights.Add(hit.point.y);
                _probeLabels.Add($"{label}:{hit.point.y:F3}/{hit.collider.name}/L{LayerMask.LayerToName(hit.collider.gameObject.layer)}");
            }

            Probe(center + new Vector3(spread, 0f, 0f), "+x");
            Probe(center - new Vector3(spread, 0f, 0f), "-x");
            Probe(center + new Vector3(0f, 0f, spread), "+z");
            Probe(center - new Vector3(0f, 0f, spread), "-z");
            Probe(center, "c");
            evidence = string.Join(" ", _probeLabels);

            int n = _probeHeights.Count;
            if (n < 2) return false; // 承重面判定至少需要两个探针
            _probeHeights.Sort();
            float median = _probeHeights[n / 2];
            int inBand = 0;
            for (int i = 0; i < n; i++)
                if (Mathf.Abs(_probeHeights[i] - median) <= DeathSupportBandMeters) inBand++;
            // 多数探针必须落在同一带上（否则"尸体跨两个面"，不猜）
            if (inBand < Mathf.Max(2, n / 2 + 1)) return false;
            groundY = median;
            return true;
        }

        /// <summary>该命中是否可作为尸体承重面（审计 §5：排除自身/所有玩家/触发器/非承重面）。</summary>
        private bool IsDeathSupport(in RaycastHit hit, out string reason)
        {
            reason = null;
            var collider = hit.collider;
            if (collider == null) { reason = "noCollider"; return false; }
            if (collider.isTrigger) { reason = "trigger"; return false; }
            var t = collider.transform;
            if (t == transform || t.IsChildOf(transform)) { reason = "self"; return false; }
            if (t.GetComponentInParent<NetworkCombatAuthority>() != null) { reason = "player"; return false; }
            if (hit.normal.y < DeathSupportMinNormalY) { reason = "slope"; return false; }
            return true;
        }

        /// <summary>
        /// 死亡表现按权威 SyncVar **唯一**驱动（审计 2026-09-16 D3）：状态 = f(_dead)，重复求值零副作用。
        /// RPC（ObserversDied/Respawned）只触发一次再求值，不再是独立状态源——因此 RPC 与 SyncVar 的
        /// 到达先后（RPC 通常先到、SyncVar 下一 tick 才刷）、丢失、重复都不会造成来回 Apply/Reset，
        /// 迟到观察者也自然收敛（SyncVar 初始同步即包含当前死亡态）。
        /// </summary>
        private void SyncDeathVisualWithState()
        {
            if (!DeathVisualStateAvailable) return;
            bool dead = _dead.Value;
            if (dead == _deathVisualActive) return;
            if (dead) ApplyDeathVisual();
            else ResetDeathVisual();
        }

        /// <summary>测试接缝（2026-09-16 审计 D3 定向用例）：EditMode 构造不出已生成的 NetworkObject，
        /// 用它跳过"已生成"前置以验证死亡表现状态机的先后次序矩阵。产品路径不设置它。</summary>
        public bool TestOverrideSpawnedForDeathVisual { get; set; }

        private bool DeathVisualStateAvailable =>
            TestOverrideSpawnedForDeathVisual || (NetworkObject != null && NetworkObject.IsSpawned);
    }
}
