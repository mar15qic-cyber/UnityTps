using System;
using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 聊天纯规则（Docs/27 §8.2/§8.3 + 复审 R06，Gameplay 侧镜像；与后端 ChatPolicy 同语义同默认值）：
    /// 正文 ≤100 Unicode 标量字符、拒绝换行/控制字符/空白、频道白名单（All/Team，System 无客户端发送路径）、
    /// 令牌桶 3/每 2s 回 1、(userId, clientMessageId) 近窗去重。服务器（DS）与客户端共用同一判定。
    /// </summary>
    public static class ChatRules
    {
        public const int MaxBodyCodePoints = 100;
        public const int TokenCapacity = 3;
        public const int TokenRefillSeconds = 2;
        public const string ChannelAll = "All";
        public const string ChannelTeam = "Team";
        public const string ChannelSystem = "System";
        /// <summary>DS 内每局（transportEpoch=matchId）的消息 seq 起点；0 = 尚无消息。</summary>
        public const ulong InitialSeq = 0;

        public static readonly string[] PlayerChannels = { ChannelAll, ChannelTeam };

        /// <summary>正文校验：非空白、≤100 code point、无换行与控制字符。</summary>
        public static bool IsValidBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            foreach (var ch in body)
                if (char.IsControl(ch)) return false;
            return CountCodePoints(body) <= MaxBodyCodePoints;
        }

        /// <summary>Unicode 标量字符计数（代理对算 1 个，不用 UTF-16 长度冒充）。</summary>
        public static int CountCodePoints(string body)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            var count = 0;
            for (var i = 0; i < body.Length; i++, count++)
                if (i + 1 < body.Length && char.IsSurrogatePair(body[i], body[i + 1]))
                    i++;
            return count;
        }

        public static bool IsValidChannel(string channel) =>
            channel == ChannelAll || channel == ChannelTeam;

        /// <summary>Team 频道要求发送者已有队伍；None（KillRace/未选边）拒绝。</summary>
        public static bool CanUseTeamChannel(string teamId) =>
            teamId == MatchRules.TeamRed || teamId == MatchRules.TeamBlue;

        /// <summary>令牌桶（与后端同式）：先回填并截断到容量再消费 1 个额度。</summary>
        public static (bool Allowed, double Tokens, DateTime RefilledAt, int RetryAfterSeconds) TryConsumeToken(
            double tokens, DateTime refilledAt, DateTime now, int capacity, int refillSeconds)
        {
            var elapsed = Math.Max(0, (now - refilledAt).TotalSeconds);
            var refilled = Math.Min((double)capacity, tokens + elapsed / refillSeconds);
            if (refilled >= 1)
                return (true, refilled - 1, now, 0);
            var deficit = 1 - refilled;
            return (false, refilled, now, Math.Max(1, (int)Math.Ceiling(deficit * refillSeconds)));
        }
    }
}
