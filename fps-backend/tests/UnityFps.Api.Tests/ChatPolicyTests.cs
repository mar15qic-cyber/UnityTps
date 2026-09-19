using System.Globalization;
using UnityFps.Api.Features;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// ChatPolicy 纯策略单测（Docs/27 §8.2）：code point 计数（emoji 计 1、不用 UTF-16 长度冒充）、
/// 换行/控制字符/空白拒绝、频道白名单、队聊队伍前置、令牌桶数学（回填/耗尽/重试间隔）。
/// </summary>
public sealed class ChatPolicyTests
{
    [Theory]
    [InlineData(100, true)]   // 100 个 ASCII = 100 code point
    [InlineData(101, false)]
    public void BodyLimit_AsciiCountsCodePoints(int length, bool valid)
        => Assert.Equal(valid, ChatPolicy.IsValidBody(new string('a', length)));

    [Fact]
    public void BodyLimit_EmojiCountsOneCodePoint()
    {
        // 🎮 = UTF-16 代理对（2 单元 / 1 code point）：100 个 emoji = 200 单元，按 code point 合法
        Assert.True(ChatPolicy.IsValidBody(string.Join("", Enumerable.Repeat("🎮", 100))));
        Assert.False(ChatPolicy.IsValidBody(string.Join("", Enumerable.Repeat("🎮", 101))));
        Assert.Equal(100, ChatPolicy.CountCodePoints(string.Join("", Enumerable.Repeat("🎮", 100))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("x\u0001y")]
    [InlineData("x\u007Fy")]
    public void BodyRejects_Blank_Newline_ControlChars(string body)
        => Assert.False(ChatPolicy.IsValidBody(body));

    [Theory]
    [InlineData("All", true)]
    [InlineData("Team", true)]
    [InlineData("System", false)]
    [InlineData("all", false)]
    [InlineData(null, false)]
    public void ChannelWhitelist_IsExactCaseSensitive(string? channel, bool valid)
        => Assert.Equal(valid, ChatPolicy.IsValidChannel(channel));

    [Theory]
    [InlineData("Red", true)]
    [InlineData("Blue", true)]
    [InlineData("None", false)]
    public void TeamChannelRequiresTeam(string teamId, bool valid)
        => Assert.Equal(valid, ChatPolicy.CanUseTeamChannel(teamId));

    [Fact]
    public void TokenBucket_ExhaustsThenRefillsByElapsed()
    {
        var t0 = DateTime.UtcNow;
        // 满桶 3：连发 3 次成功，第 4 次（无回填）拒绝并给出 1 个额度的重试间隔
        var state = (tokens: 3.0, at: t0);
        for (var i = 0; i < 3; i++)
        {
            var r = ChatPolicy.TryConsumeToken(state.tokens, state.at, t0, capacity: 3, refillSeconds: 2);
            Assert.True(r.Allowed);
            state = (r.Tokens, r.RefilledAt);
        }
        var denied = ChatPolicy.TryConsumeToken(state.tokens, state.at, t0.AddSeconds(1), capacity: 3, refillSeconds: 2);
        Assert.False(denied.Allowed);
        Assert.Equal(1, denied.RetryAfterSeconds); // 还差 0.5 个额度 ≈ 1s

        // 回填：60s 足以恢复 3 个额度（截断到容量）
        var refilled = ChatPolicy.TryConsumeToken(state.tokens, state.at, t0.AddSeconds(60), capacity: 3, refillSeconds: 2);
        Assert.True(refilled.Allowed);
        Assert.Equal(2, refilled.Tokens); // 3 → 消费 1

        // 部分回填：仅回填 0.5 个额度 → 拒绝且重试间隔按 deficit 计算
        var almost = ChatPolicy.TryConsumeToken(0.0, t0, t0.AddSeconds(1), capacity: 3, refillSeconds: 2);
        Assert.False(almost.Allowed);
        Assert.Equal(1, almost.RetryAfterSeconds);
    }
}
