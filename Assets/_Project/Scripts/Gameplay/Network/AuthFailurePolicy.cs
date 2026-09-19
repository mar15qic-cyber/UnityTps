using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>认证失败降级结论（Day2 认证失败 UI 降级）。</summary>
    public enum ClientAuthFailureKind
    {
        /// <summary>无失败（未断线 / 已接受 / 未尝试认证的普通断线）。</summary>
        None = 0,
        /// <summary>收到服务器拒绝结果广播——可展示具体 TICKET_* 原因。</summary>
        SpecificRejection = 1,
        /// <summary>断线时未收到任何结果广播（F8：Release 服务器拒绝原因 0/4 送达）——通用降级文案。</summary>
        GenericDegraded = 2,
    }

    /// <summary>
    /// 客户端认证失败判定（纯逻辑，可测）——三张信号卡：
    /// ① 结果广播（接受/拒绝+错误码）；② 认证流程尝试（票据已发或本地 fail-closed）；③ 连接尝试。
    /// 规则（用户定案，Day2 补缺后完整口径）：
    ///   收到拒绝 → SpecificRejection（立即显示具体原因）；
    ///   断线时从未收到任何结果广播，且【已尝试连接】（含从未进入认证流程——服务器不可达/端口关闭，
    ///   LocalConnectionState 从未 Started、票据从未发送）→ GenericDegraded（通用
    ///   "连接被拒绝或票据已失效，请返回大厅重试"）；
    ///   断线前收到过接受 → None（对局内断线归 MatchConnectionWatcher，不在此重复处理）；
    ///   从未尝试连接的断线（无 StartConnection 的普通停止）→ None。
    /// 决不自动重试或复用旧票据——票据会话一次性（JoinTicketClientSession），旧票据重用必触发 TICKET_REPLAYED。
    /// </summary>
    public sealed class AuthFailurePolicy
    {
        private readonly HashSet<string> _seenCodes = new();
        private bool _resultSeen;
        private bool _accepted;
        private string _rejectionCode = string.Empty;

        /// <summary>已看过的拒绝错误码（幂等：同一错误码重复广播只报一次）。</summary>
        public IReadOnlyCollection<string> SeenRejectionCodes => _seenCodes;

        /// <summary>是否已收到任一结果广播。</summary>
        public bool HasResult => _resultSeen;

        /// <summary>收到服务器认证结果。返回非 None 表示应立即显示对应失败（拒绝路径）。</summary>
        public ClientAuthFailureKind OnAuthResult(bool accepted, string errorCode)
        {
            _resultSeen = true;
            if (accepted)
            {
                _accepted = true;
                return ClientAuthFailureKind.None;
            }
            string code = string.IsNullOrEmpty(errorCode) ? "TICKET_INVALID" : errorCode;
            if (_seenCodes.Add(code))
            {
                _rejectionCode = code;
                return ClientAuthFailureKind.SpecificRejection;
            }
            return ClientAuthFailureKind.None; // 重复副本（F1 修复会补发不可靠副本）幂等
        }

        /// <summary>
        /// 本端断线。connectionAttempted=曾调用 StartConnection（NetworkHud 在启动前登记）；
        /// authAttempted=曾以客户端身份进入认证流程（票据已发或本地 fail-closed）。
        /// 已认证成功（收到接受）→ None：对局断线归 MatchConnectionWatcher，绝不重复弹窗。
        /// </summary>
        public ClientAuthFailureKind OnDisconnected(bool connectionAttempted, bool authAttempted)
        {
            if (_accepted)
                return ClientAuthFailureKind.None;
            if (_resultSeen)
                return ClientAuthFailureKind.None; // 拒绝已展示（SpecificRejection 幂等收口）
            // Day2 补缺：连接从未建立（未 Started/未发票据）与"发了票据但断线无结果"同走通用降级——
            // 服务器不可达/端口关闭不再静默失败
            if (connectionAttempted || authAttempted)
                return ClientAuthFailureKind.GenericDegraded;
            return ClientAuthFailureKind.None;
        }

        /// <summary>SpecificRejection 结论对应的错误码（其余结论为空串）。</summary>
        public string CurrentRejectionCode => _rejectionCode;

        /// <summary>新客户端会话复位（2026-09-15 P0-B）：常驻 NetworkManager 跨局复用同一判定策略实例，
        /// 上局的接受结果/已看错误码不得泄漏进新局（否则第一次成功后的失败会被 _accepted 吞掉）。</summary>
        public void ResetSession()
        {
            _seenCodes.Clear();
            _resultSeen = false;
            _accepted = false;
            _rejectionCode = string.Empty;
        }
    }
}
