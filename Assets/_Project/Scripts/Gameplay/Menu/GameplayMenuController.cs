using System.Collections;
using FishNet;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Menu
{
    /// <summary>
    /// 游戏菜单控制器（Phase A）：ESC 所有权唯一持有者 + 光标唯一写者 + 输入门控驱动。
    /// 挂载：场景级单例（GameplayMenuRoot），由网络 Owner 生成（NetworkCombatAuthority.OnStartClient
    /// 的 IsOwnerPlayer 分支）与 Arena 场景引导（离线 authored player）共同保证——远端玩家对象
    /// 永远不生成菜单（MenuMountPolicy 幂等闸）。
    /// 职责边界：
    /// ① ESC 状态机（GameplayMenuStateMachine 纯逻辑）唯一驱动；InputReader 不再消费 Escape；
    /// ② 光标：菜单可见=解锁显示，关闭=锁定隐藏（InputReader 侧零光标代码）；
    /// ③ 输入门控：GameplayInputGate（菜单/硬锁/死亡/恢复宽限）；绝不写 Time.timeScale（网络不暂停）；
    /// ④ 设置草稿生命周期：进入设置捕获，关闭未应用即回滚（取消语义），应用才持久化；
    /// ⑤ 退出对局：确认弹窗文案按人数分流；纯客户端走服务器权威 LeaveMatchRequest；
    ///    房主诚实告知「服务器随客户端关闭」（Phase D 边界，见执行报告）。
    /// 执行顺序 -400：早于 InputReader(-300)，保证 ESC 本帧即被消费、不漏进游戏输入。
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class GameplayMenuController : MonoBehaviour
    {
        /// <summary>菜单视图接口（Game.UI 侧实现并经 AttachView 挂入；Gameplay 不引用 UI 程序集）。</summary>
        public interface IGameplayMenuView
        {
            /// <summary>状态变化（含 MenuVisible 语义；视图按状态切页/显隐）。</summary>
            void OnMenuStateChanged(GameplayMenuState state);

            /// <summary>退出确认上下文变化（有效人数/是否房主）。</summary>
            void OnLeaveContextChanged();
        }

        public const string ArenaSceneName = "Arena";
        public const string LobbySceneName = "Lobby";
        private const float LeaveDisconnectTimeoutSeconds = 6f;

        public static GameplayMenuController Instance { get; private set; }

        public GameplayMenuStateMachine Machine { get; } = new();

        /// <summary>退出确认上下文：当前有效玩家数（服务器与客户端同构统计 NetworkCombatAuthority）。</summary>
        public int EffectivePlayerCount { get; private set; } = 1;

        /// <summary>本地端是否为服务器（client-hosted 房主）。</summary>
        public bool IsLocalServer { get; private set; }

        /// <summary>当前网络是否活动（离线时「退出对局」语义变为返回大厅）。</summary>
        public static bool IsNetworkActive => FishNetLifecycleGuard.IsNetworkActive();

        /// <summary>设置草稿（菜单内设置页共用；null=无未决草稿）。</summary>
        public SettingsDraft Draft => _draft;

        /// <summary>重绑冲突上下文（RebindCapture 态下由视图读取渲染弹窗）。</summary>
        public SettingsKeyMap.Action? PendingRebindConflict => _pendingConflict;

        public SettingsKeyMap.Action? RebindAction => _rebindAction;

        private SettingsDraft _draft;
        private SettingsKeyMap.Action? _rebindAction;
        private Key _rebindOriginalKey;
        private SettingsKeyMap.Action? _pendingConflict;
        private Key _rebindCandidateKey;
        private IGameplayMenuView _view;
        private NetworkCombatAuthority _localCombat;
        private InputReader _localInput;
        private bool _leaveRequested;
        private bool _matchEndLockedApplied;

        // ---- 挂载与场景引导 ----

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BootstrapSceneHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == ArenaSceneName) EnsureMounted();
            else TeardownIfMounted();
        }

        /// <summary>挂载入口（幂等）：网络 Owner 生成（反射调用点）与场景引导共同调用。</summary>
        public static void EnsureMounted()
        {
            if (Instance != null) return;
            if (SceneManager.GetActiveScene().name != ArenaSceneName) return;
            var root = new GameObject("GameplayMenuRoot");
            Instance = root.AddComponent<GameplayMenuController>();
            // 视图经反射创建（Gameplay 程序集无 UnityEngine.UI 引用——EventSystem 由视图侧保证；
            // 反射按名为本项目既有惯例）。视图缺失时菜单无 UI 但游戏可玩（诚实降级）
            var viewType = System.Type.GetType("Game.UI.Menu.GameplayMenuView, Game.UI");
            var mount = viewType?.GetMethod("TryMount",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            mount?.Invoke(null, new object[] { Instance });
        }

        private static void TeardownIfMounted()
        {
            if (Instance == null) return;
            Destroy(Instance.gameObject);
        }

        private void OnEnable()
        {
            // 新场景挂载（含从大厅重回 Arena）：门控全复位（离线/新对局必须可采样）
            GameplayInputGate.ResetAll();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                // 场景切换/销毁：门控全复位 + 光标交还系统（进大厅需要光标）；
                // 场景切换窗口期硬锁防再打开（新场景挂载时 OnEnable 复位）
                GameplayInputGate.ResetAll();
                GameplayInputGate.SetHardLocked(true);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        /// <summary>视图回接（Game.UI 视图在 TryMount 中调用）。</summary>
        public void AttachView(IGameplayMenuView view)
        {
            _view = view;
            view?.OnMenuStateChanged(Machine.State);
            view?.OnLeaveContextChanged();
        }

        // ---- 每帧驱动 ----

        private void Update()
        {
            ResolveLocalPlayer();

            // 本地死亡镜像（死亡强制关菜单 + 输入屏蔽；重生后自动解除）
            bool dead = _localCombat != null && _localCombat.IsDead;
            GameplayInputGate.SetDead(dead);
            if (dead && Machine.MenuVisible && Machine.ForceClose()) ApplyState();

            // 终局硬锁（Ended 事件或服务器镜像 Phase；一次生效，禁止再打开）
            if (!_matchEndLockedApplied && MatchLifecycle.Phase == MatchPhase.Ended)
            {
                _matchEndLockedApplied = true;
                Machine.ForceCloseAndLock(GameplayMenuLockReason.MatchEnded);
                GameplayInputGate.SetHardLocked(true);
                RollbackDraftIfAny();
                ApplyState();
            }

            GameplayInputGate.TickFrame();

            // 退出确认上下文（视图弹窗文案依赖）
            IsLocalServer = InstanceFinder.NetworkManager != null && InstanceFinder.NetworkManager.IsServerStarted;
            EffectivePlayerCount = Mathf.Max(1, FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None).Length);

            // 键位重绑捕获（优先于 ESC 菜单路由：状态机内 ESC=取消重绑）
            if (Machine.State == GameplayMenuState.RebindCapture) PollRebindKey();

            // ESC 路由（-400 早于 InputReader：本帧消费，游戏输入侧看不到该帧 ESC）；
            // 退出流程进行中 ESC 不再开菜单（防止断线等待期把菜单又拉起来）
            var kb = Keyboard.current;
            if (!_leaveRequested && kb != null && kb.escapeKey.wasPressedThisFrame && Machine.TryConsumeEscape())
                OnMenuToggled();

            UpdateLeaveContextView();
        }

        private void ResolveLocalPlayer()
        {
            if (_localInput == null)
            {
                foreach (var reader in FindObjectsByType<InputReader>(FindObjectsSortMode.None))
                {
                    // 远端玩家 InputReader 被禁用（PlayerNetworkAdapter），激活态即本地
                    if (reader.enabled && reader.isActiveAndEnabled) { _localInput = reader; break; }
                }
            }
            if (_localCombat == null)
            {
                foreach (var combat in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                    if (combat.IsOwnerPlayer) { _localCombat = combat; break; }
            }
        }

        // ---- 状态应用（光标 + 门控 + 视图） ----

        private void OnMenuToggled()
        {
            if (Machine.State == GameplayMenuState.Gameplay)
            {
                // 关闭菜单：未应用草稿一律回滚（取消语义）
                RollbackDraftIfAny();
            }
            else if (Machine.State == GameplayMenuState.Settings || Machine.State == GameplayMenuState.RebindCapture)
            {
                EnsureDraft();
            }
            ApplyState();
        }

        private void ApplyState()
        {
            bool menuVisible = Machine.MenuVisible;
            GameplayInputGate.SetMenuOpen(menuVisible);
            if (menuVisible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            _view?.OnMenuStateChanged(Machine.State);
        }

        private void UpdateLeaveContextView()
        {
            // 人数/房主每帧可能变化（Others 加入/离开）；仅粗粒度通知视图刷新弹窗文案
            _lastCount = EffectivePlayerCount;
            _lastServer = IsLocalServer;
        }

        private int _lastCount = -1;
        private bool _lastServer;
        private bool _contextDirty;

        private void LateUpdate()
        {
            // 与上一帧上下文不同才通知视图（避免每帧 UI 刷新）
            if (_lastCount != _reportedCount || _lastServer != _reportedServer)
            {
                _reportedCount = _lastCount;
                _reportedServer = _lastServer;
                _view?.OnLeaveContextChanged();
            }
        }

        private int _reportedCount = -1;
        private bool _reportedServer;

        // ---- 设置页（草稿生命周期） ----

        public void RequestOpenSettings()
        {
            if (!Machine.TryOpenSettings()) return;
            EnsureDraft();
            ApplyState();
        }

        public void RequestBackToPause()
        {
            if (!Machine.TryBackToPause()) return;
            ApplyState();
        }

        /// <summary>「返回游戏」/关闭菜单：未应用草稿回滚 + 恢复宽限生效（防点击误开火）。</summary>
        public void RequestResume()
        {
            if (!Machine.TryResume()) return;
            RollbackDraftIfAny();
            ApplyState();
        }

        /// <summary>应用设置：持久化并清草稿。</summary>
        public void ApplySettings()
        {
            if (_draft == null) return;
            _draft.ApplyAndPersist();
            _draft = null;
        }

        /// <summary>取消设置：回滚到进入设置页前（含音量/灵敏度即时预览回滚）。</summary>
        public void CancelSettings()
        {
            RollbackDraftIfAny();
            if (Machine.TryBackToPause()) ApplyState();
        }

        /// <summary>恢复默认：草稿重置为出厂值并即时预览（应用前不落盘）。</summary>
        public void ResetSettingsToDefaults()
        {
            EnsureDraft();
            _draft.ResetToDefaults();
            _draft.PreviewAllLive();
        }

        private SettingsDraft EnsureDraft()
        {
            _draft ??= SettingsDraft.CaptureFromCurrent();
            return _draft;
        }

        private void RollbackDraftIfAny()
        {
            _draft?.RestoreLive();
            _draft = null;
            _pendingConflict = null;
            _rebindAction = null;
        }

        // ---- 键位重绑 ----

        public void RequestBeginRebind(SettingsKeyMap.Action action)
        {
            if (!Machine.BeginRebind()) return;
            _rebindAction = action;
            _rebindOriginalKey = SettingsKeyMap.Get(action);
            _pendingConflict = null;
            _rebindCandidateKey = Key.None;
            ApplyState();
        }

        private void PollRebindKey()
        {
            if (_rebindAction == null || _pendingConflict != null) return; // 冲突弹窗打开期间不捕获
            var kb = Keyboard.current;
            if (kb == null) return;
            foreach (Key key in System.Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None || key == Key.Escape) continue; // Escape 保留为系统菜单键
                var control = kb[key];
                if (control == null || !control.wasPressedThisFrame) continue;

                var outcome = KeybindRules.Evaluate(_rebindAction.Value, key, out var conflicted);
                if (outcome == RebindOutcome.Conflict)
                {
                    _pendingConflict = conflicted;
                    _rebindCandidateKey = key;
                    _view?.OnMenuStateChanged(Machine.State); // 通知视图弹冲突窗
                    return;
                }
                if (outcome == RebindOutcome.Available)
                {
                    EnsureDraft().PreviewKey(_rebindAction.Value, key);
                    FinishRebind();
                    return;
                }
                // Reserved：理论上仅 Escape（已跳过），忽略
                return;
            }
        }

        /// <summary>冲突弹窗抉择：swap=true 交换两键，false 取消本次重绑（恢复原键）。</summary>
        public void ResolveRebindConflict(bool swap)
        {
            if (_pendingConflict == null || _rebindAction == null) return;
            if (swap)
            {
                EnsureDraft();
                var swapped = KeybindRules.ApplyWithSwap(_rebindAction.Value, _rebindCandidateKey, persist: false);
                _draft.PreviewKey(_rebindAction.Value, _rebindCandidateKey);
                if (swapped.HasValue)
                    _draft.PreviewKey(swapped.Value, _rebindOriginalKey);
            }
            else
            {
                EnsureDraft().PreviewKey(_rebindAction.Value, _rebindOriginalKey);
            }
            _pendingConflict = null;
            _rebindCandidateKey = Key.None;
            FinishRebind();
        }

        private void FinishRebind()
        {
            _rebindAction = null;
            _rebindOriginalKey = Key.None;
            if (Machine.CompleteRebind()) ApplyState();
        }

        // ---- 退出对局 ----

        /// <summary>「退出对局」按钮：打开确认弹窗（文案由视图按 EffectivePlayerCount/IsLocalServer 渲染）。</summary>
        public void RequestLeave()
        {
            if (!Machine.BeginLeaveConfirm()) return;
            ApplyState();
        }

        public void CancelLeave()
        {
            if (!Machine.CancelLeaveConfirm()) return;
            ApplyState();
        }

        /// <summary>确认退出。busy 由视图侧按钮禁用承担（防双击/重复 RPC）。</summary>
        public void ConfirmLeave()
        {
            if (_leaveRequested || Machine.State != GameplayMenuState.LeaveConfirm) return;
            _leaveRequested = true;
            Machine.ForceClose(); // 立即关菜单（后续是流程页/断线，不再有菜单）
            GameplayInputGate.SetMenuOpen(false);
            ApplyState();

            if (!FishNetLifecycleGuard.IsNetworkActive())
            {
                // 离线：无网络语义，直接回大厅
                ReturnToLobbyLocally();
                return;
            }
            if (IsLocalServer)
            {
                // 房主（client-hosted）：服务器随客户端关闭——诚实边界（Phase D blocker，
                // 不做假成功）。先停连接（远端会走 HostLost 流程），再回大厅。
                StopAllConnections();
                ReturnToLobbyLocally();
                return;
            }
            // 纯客户端：服务器权威退出（客户端不上报人数）；断线后由本协程兜底回大厅，
            // 2 人局的 Ended 结算导航由 MatchSettlementFlow 负责（MatchExitState 协调不重复加载）
            if (_localCombat != null) _localCombat.SubmitLeaveMatchRequest();
            StartCoroutine(LeaveAndWaitForDisconnect());
        }

        private IEnumerator LeaveAndWaitForDisconnect()
        {
            float deadline = Time.realtimeSinceStartup + LeaveDisconnectTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                var nm = InstanceFinder.NetworkManager;
                bool connected = nm != null && (nm.IsClientStarted || nm.IsServerStarted);
                if (!connected) break;
                yield return null;
            }
            StopAllConnections();
            if (!MatchExitState.SettlementNavigationPending)
                ReturnToLobbyLocally();
        }

        private static void StopAllConnections()
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null) return;
            if (nm.IsServerStarted) nm.ServerManager.StopConnection(true);
            if (nm.IsClientStarted) nm.ClientManager.StopConnection();
        }

        /// <summary>本地安全清理并回大厅（防重复加载；已在大厅则跳过）。</summary>
        public static void ReturnToLobbyLocally()
        {
            if (SceneManager.GetActiveScene().name == LobbySceneName) return;
            GameplayInputGate.ResetAll();
            SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
        }
    }
}
