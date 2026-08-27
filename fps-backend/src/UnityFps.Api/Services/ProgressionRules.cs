using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public interface IProgressionRules
{
    int GetXpToNextLevel(int level);
    int GetMatchXp(int kills, int deaths, int score);
    int GetUpgradeCost(string statId, int currentLevel);
    (int Kills, int Deaths, int Score) ClampMatch(int kills, int deaths, int score);
}

public sealed class DemoProgressionRules : IProgressionRules
{
    public int GetXpToNextLevel(int level) => Math.Max(1, level) * 100;

    public (int Kills, int Deaths, int Score) ClampMatch(int kills, int deaths, int score) =>
        (Math.Clamp(kills, 0, 50), Math.Clamp(deaths, 0, 50), Math.Clamp(score, 0, 50_000));

    public int GetMatchXp(int kills, int deaths, int score)
    {
        var clamped = ClampMatch(kills, deaths, score);
        return Math.Min(1_500, clamped.Kills * 25 + clamped.Score / 100);
    }

    public int GetUpgradeCost(string statId, int currentLevel) => currentLevel + 1;
}
