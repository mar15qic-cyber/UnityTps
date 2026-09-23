using System.Collections;
using FishNet;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Action;
using Game.Gameplay.Health;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Combat
{
    public enum ThrowablePosePhase : byte { None, Selected, Started, Released, Canceled }

    public struct ThrowablePoseState
    {
        public ThrowablePosePhase Phase;
        public ThrowableType Type;
        public uint StartTick;
        public uint LifeEpoch;
        public uint Sequence;
    }

    /// <summary>投掷请求、动作时钟与每条生命的服务器权威库存。</summary>
    [DefaultExecutionOrder(-90)]
    public sealed class ThrowableController : NetworkBehaviour
    {
        private readonly SyncVar<int> _frag = new();
        private readonly SyncVar<int> _flash = new();
        private readonly SyncVar<int> _smoke = new();
        private readonly SyncVar<ThrowablePoseState> _pose = new();
        private ThrowablePoseState _offlinePose;
        private uint _poseSequence;
        public ThrowablePoseState Presentation => FishNetLifecycleGuard.IsNetworkActive() ? _pose.Value : _offlinePose;
        private InputReader _input;
        private ActionSystem _actions;
        private NetworkCombatAuthority _combat;
        private WeaponController _weapon;
        private Locomotor _locomotor;
        private DamageableTarget _health;
        private bool _offlineDead;
        private ThrowableCatalog _catalog;
        private uint _nextRequest;
        private uint _lastRequest;
        private int[] _offlineCounts;
        private uint _throwGeneration;
        private bool _holdRequested;
        public bool IsHolding => IsEquipped && _holdRequested;
        public bool IsEquipped { get; private set; }
        public ThrowableType SelectedType { get; private set; }
        public event System.Action OnSelectionChanged;
        public ThrowableDefinition SelectedDefinition => _catalog != null ? _catalog.Get(SelectedType) : null;
        public float ReleaseDelaySeconds => _catalog != null ? _catalog.ReleaseDelaySeconds : 0.35f;
        public bool IsThrowing => _actions != null && _actions.CurrentAction == PlayerActionType.GrenadeThrow;

        public int Count(ThrowableType type) => !FishNetLifecycleGuard.IsNetworkActive() && _offlineCounts != null
            ? _offlineCounts[(int)type]
            : type switch { ThrowableType.Frag => _frag.Value, ThrowableType.Flash => _flash.Value,
                ThrowableType.Smoke => _smoke.Value, _ => 0 };
        public float ThrowActionSeconds => _catalog != null ? _catalog.ThrowActionSeconds : 0f;

        private void Awake()
        {
            _input = GetComponent<InputReader>();
            _actions = GetComponent<ActionSystem>();
            _combat = GetComponent<NetworkCombatAuthority>();
            _weapon = GetComponent<WeaponController>();
            _locomotor = GetComponent<Locomotor>();
            _health = GetComponentInChildren<DamageableTarget>(true);
            _catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            string reason = _catalog == null ? "catalog asset missing" : null;
            if (_catalog == null || !_catalog.IsValid(out reason))
            {
                Debug.LogError($"[ThrowableController] formal catalog invalid: {reason}", this);
                enabled = false;
                return;
            }
            _offlineCounts = new[] { _catalog.Frag.InitialCount, _catalog.Flash.InitialCount,
                _catalog.Smoke.InitialCount };
        }

        public override void OnStartServer()
        {
            _lastRequest = 0;
            ServerResetInventory();
        }

        private void SetPose(ThrowablePosePhase phase, ThrowableType type, uint startTick)
        {
            var state = new ThrowablePoseState
            {
                Phase = phase, Type = type, StartTick = startTick,
                LifeEpoch = _combat != null ? _combat.CurrentLifeEpoch : 0u,
                Sequence = ++_poseSequence,
            };
            if (FishNetLifecycleGuard.IsNetworkActive())
            {
                if (NetworkObject != null && NetworkObject.IsServerInitialized) _pose.Value = state;
            }
            else _offlinePose = state;
        }

        private uint CurrentTick => InstanceFinder.TimeManager != null ? InstanceFinder.TimeManager.Tick : 0u;

        private void OnEnable()
        {
            if (_health == null) _health = GetComponentInChildren<DamageableTarget>(true);
            if (_actions != null) { _actions.OnActionCompleted += HandleActionCompleted; _actions.OnActionInterrupted += HandleActionInterrupted; }
            if (_health == null) return;
            _health.OnDied += HandleDied;
            _health.OnHealthChanged += HandleHealthChanged;
        }

        private void OnDisable()
        {
            ++_throwGeneration;
            Unequip();
            if (_actions != null) { _actions.OnActionCompleted -= HandleActionCompleted; _actions.OnActionInterrupted -= HandleActionInterrupted; }
            if (_health == null) return;
            _health.OnDied -= HandleDied;
            _health.OnHealthChanged -= HandleHealthChanged;
        }

        private void HandleDied()
        {
            _offlineDead = true;
            SetPose(ThrowablePosePhase.Canceled, SelectedType, CurrentTick);
            Unequip(true);
            if (_actions != null && _actions.CurrentAction == PlayerActionType.GrenadeThrow)
                _actions.Interrupt(ActionInterruptReason.Death);
        }

        private void HandleHealthChanged(int current, int maximum)
        {
            if (!_offlineDead || current != maximum || FishNetLifecycleGuard.IsNetworkActive()) return;
            _offlineDead = false;
            ResetOfflineInventory();
        }

        public void ResetOfflineInventory()
        {
            if (_catalog == null || FishNetLifecycleGuard.IsNetworkActive()) return;
            _offlineCounts = new[] { _catalog.Frag.InitialCount, _catalog.Flash.InitialCount,
                _catalog.Smoke.InitialCount };
        }

        public void ServerResetInventory()
        {
            if (_catalog == null) return;
            SetPose(ThrowablePosePhase.None, ThrowableType.Frag, CurrentTick);
            _frag.Value = _catalog.Frag.InitialCount;
            _flash.Value = _catalog.Flash.InitialCount;
            _smoke.Value = _catalog.Smoke.InitialCount;
        }

        private void Update()
        {
            if (_catalog == null || _input == null || !FishNetLifecycleGuard.IsLocalOwner(this)) return;
            if (Game.Gameplay.Menu.GameplayInputGate.InputBlocked || MatchLifecycle.InputFrozen || !Application.isFocused)
            { _holdRequested = false; return; }
            if (_input.SlotPressed >= 0 || _input.SwapAxis != 0f) { Unequip(); return; }
            if (_input.SelectThrowablePressed) SelectNext();
            else if (IsEquipped && !IsThrowing)
            {
                if (_input.FirePressed) _holdRequested = true;
                if (_holdRequested && !_input.FireHeld)
                { _holdRequested = false; TryThrow(SelectedType); }
            }
        }

        public bool SelectNext()
        {
            if (_actions == null || _actions.IsBusy || _combat != null && _combat.IsDead
                || _health != null && !_health.IsAlive || MatchLifecycle.InputFrozen) return false;
            int start = IsEquipped ? (int)SelectedType + 1 : 0;
            for (int i = 0; i < 3; i++)
            {
                var candidate = (ThrowableType)((start + i) % 3);
                if (Count(candidate) <= 0) continue;
                SelectedType = candidate; IsEquipped = true;
                _holdRequested = false;
                if (!FishNetLifecycleGuard.IsNetworkActive())
                    SetPose(ThrowablePosePhase.Selected, candidate, CurrentTick);
                else if (NetworkObject != null && NetworkObject.IsServerInitialized)
                    SetPose(ThrowablePosePhase.Selected, candidate, CurrentTick);
                else if (FishNetLifecycleGuard.CanSubmitRpc(this)) ServerSelectThrowable(true, candidate);
                _input?.ResetAimToggle();
                OnSelectionChanged?.Invoke(); return true;
            }
            Unequip(); return false;
        }

        public void Unequip(bool preserveCancel = false)
        {
            _holdRequested = false;
            if (!IsEquipped) return;
            IsEquipped = false;
            if (preserveCancel) { OnSelectionChanged?.Invoke(); return; }
            if (!FishNetLifecycleGuard.IsNetworkActive())
                SetPose(ThrowablePosePhase.None, SelectedType, CurrentTick);
            else if (NetworkObject != null && NetworkObject.IsServerInitialized)
                SetPose(ThrowablePosePhase.None, SelectedType, CurrentTick);
            else if (FishNetLifecycleGuard.CanSubmitRpc(this)) ServerSelectThrowable(false, SelectedType);
            OnSelectionChanged?.Invoke();
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerSelectThrowable(bool selected, ThrowableType type)
        {
            if ((uint)type > 2u || _combat != null && _combat.IsDead) return;
            if (selected && Count(type) <= 0) return;
            SetPose(selected ? ThrowablePosePhase.Selected : ThrowablePosePhase.None, type, CurrentTick);
        }

        private void HandleActionCompleted(PlayerActionType action)
        { if (action == PlayerActionType.GrenadeThrow) Unequip(); }
        private void HandleActionInterrupted(PlayerActionType action, ActionInterruptReason reason)
        { if (action == PlayerActionType.GrenadeThrow) { ++_throwGeneration;
            SetPose(ThrowablePosePhase.Canceled, SelectedType, CurrentTick); Unequip(true); } }

        public void TryThrow(ThrowableType type)
        {
            if ((uint)type > 2u || _catalog == null || _actions == null || _actions.IsBusy || _combat != null && _combat.IsDead
                || _health != null && !_health.IsAlive
                || MatchLifecycle.InputFrozen || !IsPlayablePhase(FishNetLifecycleGuard.IsNetworkActive(), MatchLifecycle.Phase)
                || Count(type) <= 0) return;
            if (!_actions.TryStart(PlayerActionType.GrenadeThrow, _catalog.ThrowActionSeconds)) return;
            if (!FishNetLifecycleGuard.IsNetworkActive() || NetworkObject != null && NetworkObject.IsServerInitialized)
                SetPose(ThrowablePosePhase.Started, type, CurrentTick);
            OnLocalThrowStarted?.Invoke(type);
            if (!FishNetLifecycleGuard.IsNetworkActive())
            {
                StartCoroutine(ReleaseAfterDelay(type, false, ++_throwGeneration));
                return;
            }
            if (NetworkObject == null || !NetworkObject.IsSpawned) return;
            if (NetworkObject.IsServerInitialized)
                StartCoroutine(ReleaseAfterDelay(type, true, ++_throwGeneration));
            else
                ServerRequestThrow(type, ++_nextRequest);
        }

        public event System.Action<ThrowableType> OnLocalThrowStarted;

        [ServerRpc(RequireOwnership = true)]
        private void ServerRequestThrow(ThrowableType type, uint requestId)
        {
            if (!IsNextRequest(_lastRequest, requestId)) return;
            _lastRequest = requestId;
            if ((int)type < 0 || (int)type > 2 || Count(type) <= 0 || _combat == null
                || _combat.IsDead || _health != null && !_health.IsAlive
                || MatchLifecycle.InputFrozen || !IsPlayablePhase(true, MatchLifecycle.Phase)
                || _actions == null || !_actions.TryStart(PlayerActionType.GrenadeThrow, _catalog.ThrowActionSeconds))
                return;
            SetPose(ThrowablePosePhase.Started, type, CurrentTick);
            StartCoroutine(ReleaseAfterDelay(type, true, ++_throwGeneration));
        }

        public static bool IsNextRequest(uint lastRequest, uint requestId)
            => requestId != 0 && requestId == lastRequest + 1;

        public static bool IsPlayablePhase(bool networkActive, MatchPhase phase)
            => networkActive ? phase == MatchPhase.InProgress : phase != MatchPhase.Ended;

        private IEnumerator ReleaseAfterDelay(ThrowableType type, bool networked, uint generation)
        {
            yield return new WaitForSeconds(_catalog.ReleaseDelaySeconds);
            if (generation != _throwGeneration || _actions == null || _actions.CurrentAction != PlayerActionType.GrenadeThrow
                || _combat != null && _combat.IsDead || _health != null && !_health.IsAlive
                || !IsPlayablePhase(networked, MatchLifecycle.Phase)
                || Count(type) <= 0) yield break;
            var definition = _catalog.Get(type);
            Vector3 direction = _weapon != null ? _weapon.AimDirection.normalized : transform.forward;
            Vector3 origin = _weapon != null ? _weapon.AimOrigin : transform.position + Vector3.up * 1.5f;
            var locomotor = GetComponent<Locomotor>();
            float lean = locomotor != null ? locomotor.Lean.Amount : 0f;
            Vector3 anchor = LeanProfile.BodyAnchor(transform.position, transform.rotation, lean);
            if (PathBlocked(anchor, origin, .06f))
            {
                SetPose(ThrowablePosePhase.Canceled, type, CurrentTick);
                yield break;
            }
            Vector3 release = origin + direction * 0.55f + Vector3.down * 0.13f;
            // 释放点若贴墙，沿射线退回表面；无需信任客户端传来的坐标。
            var hits = Physics.SphereCastAll(origin, 0.1f, direction, 0.65f, ~0,
                QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider.transform.IsChildOf(transform)) continue;
                release = hit.point + hit.normal * 0.14f;
                break;
            }
            foreach (var overlap in Physics.OverlapSphere(release, 0.1f, ~0,
                QueryTriggerInteraction.Ignore))
            {
                if (overlap.transform.IsChildOf(transform)) continue;
                Debug.LogWarning("[ThrowableController] release point blocked; throw cancelled", this);
                SetPose(ThrowablePosePhase.Canceled, type, CurrentTick);
                yield break;
            }
            var horizontal = _locomotor != null ? _locomotor.CaptureSnapshot().HorizontalVelocity : Vector3.zero;
            Vector3 velocity = direction * definition.ForwardSpeed + Vector3.up * definition.UpwardSpeed
                + horizontal * definition.InheritedHorizontalVelocity;
            if (networked)
            {
                SetCount(type, Count(type) - 1);
                var instance = Instantiate(_catalog.NetworkProjectilePrefab, release, Quaternion.identity);
                var projectile = instance.GetComponent<ThrowableProjectile>();
                if (projectile == null)
                {
                    Debug.LogError("[ThrowableController] network projectile component missing", instance);
                    Destroy(instance);
                    SetCount(type, Count(type) + 1);
                    yield break;
                }
                projectile.ServerInitialize(type, velocity, _combat);
                InstanceFinder.NetworkManager.ServerManager.Spawn(instance);
                SetPose(ThrowablePosePhase.Released, type, CurrentTick);
            }
            else
            {
                _offlineCounts[(int)type]--;
                var instance = Instantiate(_catalog.NetworkProjectilePrefab, release, Quaternion.identity);
                instance.GetComponent<NetworkObject>()?.SetIsNetworked(false);
                instance.GetComponent<ThrowableProjectile>()?.OfflineInitialize(type, velocity, _combat);
                SetPose(ThrowablePosePhase.Released, type, CurrentTick);
            }
        }

        private bool PathBlocked(Vector3 from, Vector3 to, float radius)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < .0001f) return false;
            foreach (var hit in Physics.SphereCastAll(from, radius, delta / distance,
                         distance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider != null && !hit.collider.transform.IsChildOf(transform)) return true;
            }
            return false;
        }

        private void SetCount(ThrowableType type, int count)
        {
            switch (type)
            {
                case ThrowableType.Frag: _frag.Value = count; break;
                case ThrowableType.Flash: _flash.Value = count; break;
                case ThrowableType.Smoke: _smoke.Value = count; break;
            }
        }
    }
}
