using FishNet.Broadcast;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// join-ticket 认证广播 DTO（Docs/27 Day1 §4.1 C2）。
    /// 两个 struct 各自独立成组（同一文件只放 DTO，不含 MonoBehaviour——
    /// 项目约定：MonoBehaviour 一类一文件，避免 MonoScript 反查失败）。
    /// 序列化由 FishNet 代码生成针对 IBroadcast 自动处理（同 Demo Broadcasts.cs 模式）。
    /// </summary>

    /// <summary>客户端 → 服务器：一次性 join ticket（或 Editor/Development 显式开启的无票据本地调试请求）。
    /// 2026-09-15 P0-A：末位追加 ProtocolId（认证前应用协议握手）——新服务器据此在消费后端票据之前
    /// 拒绝协议不匹配的旧客户端（PROTOCOL_MISMATCH）；旧服务器读不到该字段时，可靠通道的长度帧内
    /// 残留字节会在认证阶段即触发流错位断开（fail fast，绝不用第一发开火 RPC 做协议探针）。
    /// 追加字段必须放最后（FishNet 按声明序读写）。</summary>
    public struct JoinTicketBroadcast : IBroadcast
    {
        /// <summary>后端签发的 opaque join ticket（Base64Url，32 字节随机数）。</summary>
        public string Ticket;

        /// <summary>仅本地调试：无票据直连请求。服务器仅在自身 unsafe debug 通道开启时接受，Release 恒拒绝。</summary>
        public bool UnsafeDebugRequest;

        /// <summary>客户端应用协议代际（GameProtocolIdentity.ProtocolId；空 = 旧客户端）。</summary>
        public string ProtocolId;
        public string MapContentHash;
    }

    /// <summary>服务器 → 客户端：认证结果。失败携带冻结错误码（TICKET_INVALID/TICKET_EXPIRED/TICKET_REPLAYED/TICKET_INSTANCE_MISMATCH 或本地 backend-timeout）。
    /// Day2：接受路径附带稳定 userId/username（后端权威身份）——客户端据此以稳定 userId 而非 FishNet ClientId
    /// 参与终局载荷匹配（重连/换连接不改变身份）；拒绝路径两字段恒空串（拒绝语义不携带任何身份信息）。</summary>
    public struct JoinTicketResultBroadcast : IBroadcast
    {
        public bool Accepted;
        public string ErrorCode;
        public string UserId;
        public string Username;
    }
}
