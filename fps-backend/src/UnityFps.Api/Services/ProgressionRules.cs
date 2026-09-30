using UnityFps.Api.Common;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public interface IProgressionRules
{
    int GetXpToNextLevel(int level);

    /// <summary>结算数值校验（旧路径）：kills ≤ 30、duration ≤ 15min；超限抛异常由调用方转 422.</summary>
    (int Kills, int DurationSeconds) ValidateMatchPayload(int kills, int durationSeconds);

    /// <summary>结算数值校验（Docs/27 §7.2 模式感知）：带 matchId 的房间对局放宽为 kills ≤ 200、duration ≤ 20min；旧路径维持原上限.</summary>
    (int Kills, int DurationSeconds) ValidateMatchPayload(int kills, int durationSeconds, bool matchScoped);

    /// <summary>账号 XP = 100 + 20×kills + 200×(win?1:0)，上限 1000.</summary>
    int GetMatchXp(int kills, bool isWin);

    /// <summary>金币 = 50 + 10×kills + 100×(win?1:0)，上限 400.</summary>
    int GetMatchCoins(int kills, bool isWin);

    /// <summary>通行证经验 = 200 固定.</summary>
    int GetMatchPassXp();

    /// <summary>通行证升级曲线：Lv N→N+1 = 300 + 50×(N-1).</summary>
    int GetPassXpToNextLevel(int currentLevel);

    /// <summary>S1 通行证上限等级.</summary>
    int MaxPassLevel { get; }
}

public sealed class DemoProgressionRules : IProgressionRules
{
    public int MaxPassLevel => 15;

    public int GetXpToNextLevel(int level) => Math.Max(1, level) * 100;


    public (int Kills, int DurationSeconds) ValidateMatchPayload(int kills, int durationSeconds)
        => ValidateMatchPayload(kills, durationSeconds, matchScoped: false);

    public (int Kills, int DurationSeconds) ValidateMatchPayload(int kills, int durationSeconds, bool matchScoped)
    {
        // Docs/27 §7.2：TDM/新版对局（带 matchId）合法个人战绩可超旧 30 杀上限；伪造防线由比赛绑定校验承担
        var maxKills = matchScoped ? 200 : 30;
        var maxDuration = matchScoped ? 1200 : 900;
        if (kills < 0 || kills > maxKills)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.MatchPayloadRejected, $"击杀数超出有效范围 (0–{maxKills})");
        if (durationSeconds < 0 || durationSeconds > maxDuration)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.MatchPayloadRejected, $"时长超出有效范围 (0–{maxDuration}s)");
        return (kills, durationSeconds);
    }

    public int GetMatchXp(int kills, bool isWin) =>
        Math.Min(1_000, 100 + 20 * kills + (isWin ? 200 : 0));

    public int GetMatchCoins(int kills, bool isWin) =>
        Math.Min(400, 50 + 10 * kills + (isWin ? 100 : 0));

    public int GetMatchPassXp() => 200;

    public int GetPassXpToNextLevel(int currentLevel) =>
        300 + 50 * Math.Max(0, currentLevel - 1);
}
