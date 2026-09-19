using System;
using Game.Account;
using Game.Gameplay.Network;

namespace Game.UI
{
    /// <summary>
    /// 战斗连接严格校验与票据卫生（Day3 Phase 0，Docs/27 v1.2 CF 修订，纯逻辑可离线测试）：
    /// 大厅在「写 NetworkLaunchContext → 加载 Arena」前必须过本门；校验失败绝不写上下文、
    /// 绝不加载 Arena；票据明文校验完成后立即 Sanitize，全链路（状态栏/日志/落盘）禁止出现 ticket 明文。
    /// CF 语义：connection 只来自 start ack / InMatch 重连/补人的快照连接字段；Waiting 轮询没有票据。
    /// </summary>
    public static class RoomConnectionGate
    {
        /// <summary>严格校验 Docs/27 连接契约：地址/端口/票据/有效期/比赛身份全部合格才放行。</summary>
        public static bool TryValidate(RoomConnectionInfoDto connection, DateTime utcNow, out string error)
        {
            if (connection == null) { error = "服务器未返回战斗连接数据"; return false; }
            if (string.IsNullOrWhiteSpace(connection.serverAddress))
            { error = "服务器连接数据缺少地址"; return false; }
            if (connection.serverPort < 1 || connection.serverPort > 65535)
            { error = "服务器端口非法（1-65535）"; return false; }
            if (string.IsNullOrWhiteSpace(connection.joinTicket))
            { error = "服务器未下发加入票据"; return false; }
            if (!TryParseUtc(connection.ticketExpiresAtUtc, out var expiresUtc) || expiresUtc <= utcNow)
            { error = "加入票据已过期或有效期非法"; return false; }
            if (string.IsNullOrWhiteSpace(connection.matchId))
            { error = "服务器连接数据缺少比赛身份"; return false; }
            error = null;
            return true;
        }

        /// <summary>校验通过后调用：把一次性票据写入启动上下文，随后立即清除 DTO 中的明文引用。
        /// sceneName=目标战斗场景（协调器 sceneLoaded 过滤用）；matchId=权威比赛 id（日志归属）。
        /// 2026-09-15 P0-B：代际由 ConfigureClient 内部递增分配（同一代际至多消费一次）。</summary>
        public static void WriteLaunchContext(RoomConnectionInfoDto connection, string sceneName)
        {
            NetworkLaunchContext.ConfigureClient(
                connection.serverAddress.Trim(), (ushort)connection.serverPort, connection.joinTicket,
                sceneName, connection.matchId);
            Sanitize(connection);
        }

        /// <summary>清除 DTO 中的票据明文（地址/端口/比赛身份保留供 UI 展示）。</summary>
        public static void Sanitize(RoomConnectionInfoDto connection)
        {
            if (connection == null) return;
            connection.joinTicket = null;
            connection.ticketExpiresAtUtc = null;
        }

        private static bool TryParseUtc(string value, out DateTime utc)
        {
            return DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out utc);
        }
    }
}
