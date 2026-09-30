using UnityFps.Api.Common;
using UnityFps.Api.Data;

namespace UnityFps.Api.Features;

/// <summary>
/// 聊天纯策略（Docs/27 §8.2，HTTP 与 RPC 双传输共用同一套规则）：
/// 正文 ≤100 个 Unicode 标量字符（按 code point 计，非 UTF-16 长度）、拒绝换行/控制字符/空白正文、
/// 频道白名单；令牌桶容量 3、每 2s 回 1（按账号+房间）。纯静态便于单测。
/// </summary>
public static class ChatPolicy
{
    public const int MaxBodyCodePoints = 100;

    public static readonly string[] PlayerChannels = ["All", "Team"];

    /// <summary>正文校验：非空白、≤100 code point、无换行与控制字符。</summary>
    public static bool IsValidBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        foreach (var ch in body)
            if (char.IsControl(ch)) return false; // 含 \n \r 与其他 C0/C1
        return CountCodePoints(body) <= MaxBodyCodePoints;
    }

    /// <summary>Unicode 标量字符计数（代理对算 1 个，不用 UTF-16 长度冒充——Docs/27 §8.2）。</summary>
    public static int CountCodePoints(string body)
    {
        var count = 0;
        for (var i = 0; i < body.Length; i++, count++)
            if (i + 1 < body.Length && char.IsSurrogatePair(body[i], body[i + 1]))
                i++;
        return count;
    }

    public static bool IsValidChannel(string? channel) => channel is not null && PlayerChannels.Contains(channel);

    /// <summary>Team 频道要求发送者已有队伍（TDM 选边后）；KillRace(None) 拒绝。</summary>
    public static bool CanUseTeamChannel(string teamId) => teamId is Teams.Red or Teams.Blue;

    /// <summary>令牌桶：按上次补充时间回填并尝试消费 1 个额度（容量/回填速率参数化，生产默认 3/2s）。
    /// 标准写法：先按 elapsed 回填并截断到容量再消费；last=now（部分额度保留在 tokens 中，无精度损失）。</summary>
    public static (bool Allowed, double Tokens, DateTime RefilledAt, int RetryAfterSeconds) TryConsumeToken(
        double tokens, DateTime refilledAt, DateTime now, int capacity, int refillSeconds)
    {
        var elapsed = Math.Max(0, (now - refilledAt).TotalSeconds);
        var refilled = Math.Min(capacity, tokens + elapsed / refillSeconds);
        if (refilled >= 1)
            return (true, refilled - 1, now, 0);
        var deficit = 1 - refilled;
        return (false, refilled, now, Math.Max(1, (int)Math.Ceiling(deficit * refillSeconds)));
    }
}
