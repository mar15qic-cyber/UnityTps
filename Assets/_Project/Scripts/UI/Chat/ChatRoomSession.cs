using System;
using System.Collections.Generic;

namespace Game.UI.Chat
{
    /// <summary>
    /// R9（审计修复）房间聊天会话（静态、房间作用域）：消息/去重/游标的持久持有者——
    /// UI（ChatController/ChatHudView）随场景销毁，返房重建后从此处补水；离开房间（roomCode 变化
    /// 或显式 Reset）才清空。局内 RPC 消息同样入会话（同一房间会话的可见消息流，HTTP 重拉无法恢复
    /// 局内消息的缺口由此闭合）。有界：历史 30 条、去重 256 键（与既有控制器容量一致）。
    /// </summary>
    public static class ChatRoomSession
    {
        public const int MaxHistory = 30;
        public const int DedupCapacity = 256;

        private static string _roomCode;
        private static readonly List<ChatClientMessage> History = new();
        private static readonly Queue<string> DedupKeys = new();
        private static readonly HashSet<string> DedupSet = new(StringComparer.Ordinal);
        private static ulong _httpCursor;

        /// <summary>当前会话房间码（null/空 = 无会话）。</summary>
        public static string RoomCode => _roomCode;

        /// <summary>当前会话可见历史（最多 30 条，旧→新）。</summary>
        public static IReadOnlyList<ChatClientMessage> Messages => History;

        /// <summary>HTTP 增量拉取游标（后端权威推进的客户端镜像；跨场景保留）。</summary>
        public static ulong HttpCursor
        {
            get => _httpCursor;
            set => _httpCursor = value;
        }

        /// <summary>是否为目标房间的会话。</summary>
        public static bool IsRoom(string roomCode)
            => string.Equals(_roomCode, roomCode ?? string.Empty, StringComparison.Ordinal);

        /// <summary>确保会话属于目标房间：房间不一致 = 新会话（清空旧数据）。</summary>
        public static void EnsureRoom(string roomCode)
        {
            if (IsRoom(roomCode)) return;
            Reset(roomCode);
        }

        /// <summary>重置会话（离开房间/换房）。</summary>
        public static void Reset(string roomCode)
        {
            _roomCode = roomCode ?? string.Empty;
            History.Clear();
            DedupKeys.Clear();
            DedupSet.Clear();
            _httpCursor = 0;
        }

        /// <summary>
        /// 端到端去重注册（clientMessageId 优先 + 服务端消息身份）+ 追加到有界历史。
        /// 返回 false = 重复消息（不追加）。键容量超限按 FIFO 淘汰（与既有控制器语义一致）。
        /// </summary>
        public static bool TryRegister(ChatClientMessage message, string serverKey)
        {
            if (message == null) return false;
            var keys = new List<string>(2);
            if (!string.IsNullOrEmpty(message.ClientMessageId)) keys.Add("id:" + message.ClientMessageId);
            if (!string.IsNullOrEmpty(serverKey)) keys.Add(serverKey);
            // 原子判定：先全量查重再登记（旧实现逐键 Add，命中后置会残留已加的前置键）
            foreach (var key in keys)
                if (DedupSet.Contains(key))
                {
                    // 2026-09-15 去重诊断（用户报告本机发送偶发两条）：命中重复键时留痕，
                    // 便于从玩家日志判定"服务端重复投递"还是"客户端重复显示"。
                    UnityEngine.Debug.Log($"[Chat] 去重过滤 key={key} transport={message.Transport} "
                        + $"seq={message.Seq} body={Short(message.Body)}");
                    return false;
                }
            foreach (var key in keys)
            {
                DedupSet.Add(key);
                DedupKeys.Enqueue(key);
                while (DedupKeys.Count > DedupCapacity)
                    DedupSet.Remove(DedupKeys.Dequeue());
            }
            History.Add(message);
            while (History.Count > MaxHistory) History.RemoveAt(0);
            UnityEngine.Debug.Log($"[Chat] 入列 transport={message.Transport} seq={message.Seq} ch={message.Channel} "
                + $"cid={Short(message.ClientMessageId)} body={Short(message.Body)}");
            return true;
        }

        /// <summary>日志截断（长度无关安全：测试用短 cid/正文，Substring 越界会抛）。</summary>
        private static string Short(string value, int max = 16)
            => string.IsNullOrEmpty(value) ? "-" : value.Substring(0, Math.Min(max, value.Length));
    }
}
