using UnityFps.Api.Common;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class ProgressionRulesTests
{
    private readonly DemoProgressionRules rules = new();

    [Fact]
    public void MatchPayloadBeyondCapsIsRejectedNotClamped()
    {
        Assert.Throws<ApiException>(() => rules.ValidateMatchPayload(31, 300));
        Assert.Throws<ApiException>(() => rules.ValidateMatchPayload(10, 901));
        Assert.Throws<ApiException>(() => rules.ValidateMatchPayload(-1, 300));
        Assert.Equal((10, 300), rules.ValidateMatchPayload(10, 300));
    }

    [Fact]
    public void ThreeCurrencyFormulasMatchDocs17()
    {
        Assert.Equal(700, rules.GetMatchXp(20, true));     // 100 + 20×20 + 200
        Assert.Equal(900, rules.GetMatchXp(30, true));     // 公式上限
        Assert.Equal(100, rules.GetMatchXp(0, false));
        Assert.Equal(350, rules.GetMatchCoins(20, true)); // 50 + 10×20 + 100
        Assert.Equal(400, rules.GetMatchCoins(30, true));  // 450 → 单局上限 400
        Assert.Equal(50, rules.GetMatchCoins(0, false));
        Assert.Equal(200, rules.GetMatchPassXp());
    }

    [Fact]
    public void PassCurveIsDeterministic()
    {
        Assert.Equal(300, rules.GetPassXpToNextLevel(1));
        Assert.Equal(350, rules.GetPassXpToNextLevel(2));
        Assert.Equal(400, rules.GetPassXpToNextLevel(3));
        Assert.Equal(950, rules.GetPassXpToNextLevel(14));
        Assert.Equal(15, rules.MaxPassLevel);
    }

    [Fact]
    public void FullPassTakesAboutThirtyMatchesPlusAchievements()
    {
        // Lv1→15 累计 = Σ(300 + 50×(N-1)), N=1..14 = 8750；
        // 每局 200 + 全成就 3100 → ≈29 局可满（Docs/17 §4.6 节奏目标）
        var total = Enumerable.Range(1, 14).Sum(rules.GetPassXpToNextLevel);
        Assert.Equal(8750, total);
        Assert.InRange((total - 3100) / rules.GetMatchPassXp(), 26, 30);
    }

    [Fact]
    public void LevelThresholdAndUpgradeCostAreDeterministic()
    {
        Assert.Equal(100, rules.GetXpToNextLevel(1));
        Assert.Equal(300, rules.GetXpToNextLevel(3));
        Assert.Equal(1, rules.GetUpgradeCost("damage", 0));
        Assert.Equal(5, rules.GetUpgradeCost("damage", 4));
    }
}
