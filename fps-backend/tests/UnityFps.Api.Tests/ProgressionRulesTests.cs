using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class ProgressionRulesTests
{
    private readonly DemoProgressionRules rules = new();

    [Fact]
    public void MatchInputIsClampedBeforeXpIsCalculated()
    {
        Assert.Equal((50, 0, 50_000), rules.ClampMatch(999, -4, 999_999));
        Assert.Equal(1_500, rules.GetMatchXp(999, 999, 999_999));
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
