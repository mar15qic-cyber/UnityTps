using FishNet;
using FishNet.Transporting;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 客户端认证失败 UI 降级（Day2 入口任务 2）。挂载：NetworkHud.Awake 运行时 AddComponent（零资产改动）；
    /// 仅客户端会话路径经 Bind 接线（Host 调试/Dedicated/离线不 Bind 则完全惰性）。
    /// 职责（用户定案）：
    /// ① 收到 TICKET_* 拒绝广播 → 显示具体原因（Development 服务器 3/3 送达）；
    /// ② 断线时从未收到结果广播（F8：Release 服务器拒绝原因 0/4 送达）→ 通用降级
    ///    "连接被拒绝或票据已失效，请返回大厅重试"（不深挖 Release 投递底层）；
    /// ③ Day2 补缺：连接从未建立（StartConnection 后从未 Started、票据从未发送——服务器不可达/
    ///    端口关闭）同样走通用降级——判定输入 ClientConnectionAttempted 在 NetworkHud 启动前登记，
    ///    与 ClientAuthAttempted（已进入认证流程）区分；
    /// ④ 已认证成功后的断线不在此处理（对局断线归 MatchConnectionWatcher，不重复弹窗）；
    /// ⑤ 失败即清空 NetworkLaunchContext（旧 endpoint/票据不留存）并返回大厅——大厅重新取票，
    ///    本地绝不重发旧票据（会话一次性，避免 TICKET_REPLAYED）。
    /// 表现层说明：Game.Gameplay 不引用 TMP/uGUI asmdef（共享 asmdef 归 Codex 审批），故本失败提示用
    /// IMGUI 覆盖层临时承载；正式连接错误 UI 归 zcode 大厅接线（Docs/26 §3 所有权），届时消费
    /// AuthFailurePolicy/本组件事件替换即可。
    /// </summary>
    public sealed class ClientAuthFailureHandler : MonoBehaviour
    {
        /// <summary>显示失败提示后自动返回大厅的延迟（unscaled 秒）。</summary>
        private const float ReturnDelaySeconds = 3f;

        private readonly AuthFailurePolicy _policy = new();
        private JoinTicketAuthenticator _authenticator;
        private bool _boundAsClientSession;
        private bool _failureActive;
        private ClientAuthFailureKind _failureKind = ClientAuthFailureKind.None;
        private string _failureText = string.Empty;
        private float _returnDueRealtime;
        private FishNet.Managing.Client.ClientManager _clientManager;

        /// <summary>NetworkHud 在创建客户端侧认证器后调用（isClientSession：本进程是否以纯客户端会话连网）。</summary>
        public void Bind(JoinTicketAuthenticator authenticator, bool isClientSession)
        {
            if (_authenticator == authenticator && _boundAsClientSession == isClientSession)
                return;
            Unbind();
            _authenticator = authenticator;
            _boundAsClientSession = isClientSession;
            if (_authenticator == null || !_boundAsClientSession)
                return;
            _authenticator.ClientResultReceived += OnAuthResult;
            _clientManager = InstanceFinder.ClientManager;
            if (_clientManager != null)
                _clientManager.OnClientConnectionState += OnClientConnectionState;
        }

        /// <summary>
        /// 新客户端会话准备（2026-09-15 P0-B：审计 §3.2——Bind 同实例直接 return、
        /// AuthFailurePolicy._accepted/_resultSeen 无新会话重置入口，第一次成功后的失败会被吞）。
        /// ClientMatchSessionCoordinator 每局连接前调用：清策略卡 + 撤失败覆盖层 + 解绑，
        /// 随后的 Bind 因 _authenticator==null 必然重新接线（事件订阅净变化为零）。
        /// </summary>
        public void PrepareForNewSession()
        {
            _failureActive = false;
            _failureKind = ClientAuthFailureKind.None;
            _failureText = string.Empty;
            _policy.ResetSession();
            Unbind();
        }

        private void Unbind()
        {
            if (_authenticator != null)
                _authenticator.ClientResultReceived -= OnAuthResult;
            if (_clientManager != null)
                _clientManager.OnClientConnectionState -= OnClientConnectionState;
            _authenticator = null;
            _clientManager = null;
            _boundAsClientSession = false;
        }

        private void OnDestroy() => Unbind();

        private void OnAuthResult(JoinTicketResultBroadcast message)
        {
            var verdict = _policy.OnAuthResult(message.Accepted, message.ErrorCode);
            if (verdict == ClientAuthFailureKind.SpecificRejection)
                TriggerFailure(ClientAuthFailureKind.SpecificRejection, _policy.CurrentRejectionCode);
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;
            if (MatchExitState.VoluntaryLeaveRequested || MatchExitState.SettlementNavigationPending)
                return;
            // Day2 补缺：两张尝试卡都传入——连接从未建立（未 Started/未发票据）也走通用降级
            bool connectionAttempted = _authenticator != null && _authenticator.ClientConnectionAttempted;
            bool authAttempted = _authenticator != null && _authenticator.ClientAuthAttempted;
            var verdict = _policy.OnDisconnected(connectionAttempted, authAttempted);
            if (verdict == ClientAuthFailureKind.GenericDegraded)
                TriggerFailure(ClientAuthFailureKind.GenericDegraded, string.Empty);
        }

        /// <summary>失败收口（幂等）：清上下文 → 断残留连接 → 显示提示 → 延迟回大厅。</summary>
        private void TriggerFailure(ClientAuthFailureKind kind, string errorCode)
        {
            if (_failureActive)
                return;
            _failureActive = true;
            _failureKind = kind;
            _failureText = BuildFailureText(kind, errorCode);

            if (kind == ClientAuthFailureKind.SpecificRejection)
                Debug.LogWarning($"[ClientAuthFailure] SPECIFIC code={errorCode}——显示具体原因并返回大厅");
            else
                Debug.LogWarning("[ClientAuthFailure] GENERIC_DEGRADED——断线未收到认证结果广播，显示通用降级文案并返回大厅");

            // 清空旧 endpoint/ticket：返回大厅后由大厅重新取票；本地绝不复用旧票据（TICKET_REPLAYED 防线）
            NetworkLaunchContext.Clear();
            var nm = InstanceFinder.NetworkManager;
            if (nm != null && nm.ClientManager != null && nm.IsClientStarted)
                nm.ClientManager.StopConnection();

            _returnDueRealtime = Time.unscaledTime + ReturnDelaySeconds;
        }

        /// <summary>
        /// 会话级失败上报（2026-09-15 P1：ClientMatchSessionCoordinator 分段超时的展示入口——
        /// 认证结果超时/Owner 生成超时等非认证器路径的失败）。收口与 TriggerFailure 同一套：
        /// 清上下文 → 断连接 → 覆盖层提示 → 延迟回大厅（Lobby 引导按 session.Room 自动恢复等待房间页）。
        /// </summary>
        public void ReportSessionFailure(string message)
        {
            if (_failureActive)
                return;
            _failureActive = true;
            _failureKind = ClientAuthFailureKind.GenericDegraded;
            _failureText = string.IsNullOrEmpty(message) ? "进入战场失败，请返回大厅重试" : message;
            Debug.LogWarning($"[ClientAuthFailure] SESSION_FAILURE text={_failureText}");
            NetworkLaunchContext.Clear();
            var nm = InstanceFinder.NetworkManager;
            if (nm != null && nm.ClientManager != null && nm.IsClientStarted)
                nm.ClientManager.StopConnection();
            _returnDueRealtime = Time.unscaledTime + ReturnDelaySeconds;
        }

        private void Update()
        {
            if (!_failureActive)
                return;
            if (Time.unscaledTime >= _returnDueRealtime)
            {
                _failureActive = false;
                Debug.Log("[ClientAuthFailure] returning to lobby（允许重新获取新票据）");
                Gameplay.Menu.GameplayMenuController.ReturnToLobbyLocally();
            }
        }

        private void OnGUI()
        {
            if (!_failureActive)
                return;
            const float width = 460f, height = 140f;
            var rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            GUI.ModalWindow(GetInstanceID(), rect, _ =>
            {
                var style = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                GUILayout.Label(_failureText, style);
                if (GUILayout.Button("返回大厅"))
                {
                    _failureActive = false;
                    Debug.Log("[ClientAuthFailure] returning to lobby（用户确认）");
                    Gameplay.Menu.GameplayMenuController.ReturnToLobbyLocally();
                }
            }, "连接失败");
        }

        private static string BuildFailureText(ClientAuthFailureKind kind, string errorCode)
        {
            if (kind == ClientAuthFailureKind.GenericDegraded)
                return "连接被拒绝或票据已失效，请返回大厅重试";
            return $"连接被服务器拒绝：{DescribeErrorCode(errorCode)}\n请返回大厅重新获取票据";
        }

        /// <summary>冻结错误码 → 玩家可读文案（未知码原样展示，不臆造语义）。</summary>
        private static string DescribeErrorCode(string errorCode)
        {
            switch (errorCode)
            {
                case "TICKET_REPLAYED": return "票据已被使用";
                case "TICKET_EXPIRED": return "票据已过期";
                case "TICKET_INVALID": return "票据无效";
                case "TICKET_INSTANCE_MISMATCH": return "票据与目标服务器不匹配";
                case "TEAM_FULL": return "所选队伍已满，请换队后重试";
                case "PROTOCOL_MISMATCH":
                    return "客户端与服务器版本不匹配，请更新客户端或让房主重启对战服务器";
                case "AUTH_BACKEND_UNREACHABLE":
                case "AUTH_BACKEND_TIMEOUT": return "认证服务暂时不可用，请稍后重试";
                default: return string.IsNullOrEmpty(errorCode) ? "票据无效" : errorCode;
            }
        }
    }
}
