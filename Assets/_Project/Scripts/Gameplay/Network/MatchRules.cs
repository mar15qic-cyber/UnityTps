namespace Game.Gameplay.Network
{
    /// <summary>
    /// 击杀竞赛规则（Docs/23 P1-1，G4）：纯静态函数 + 常量，无 Unity 依赖——
    /// 可测性设计先例同 FPAimAnimStateMachine；判定唯一真相，MatchLifecycle 只做推进。
    /// </summary>
    public static class MatchRules
    {
        /// <summary>击杀目标数：任一玩家达到即终局（Docs/17 §1.4）。</summary>
        public const int TargetKills = 20;

        /// <summary>对局长度上限（秒），超时按击杀数判定。</summary>
        public const int MatchTimeLimitSeconds = 600;

        /// <summary>开局倒计时（秒），倒计时期间输入冻结。</summary>
        public const float CountdownSeconds = 3f;

        /// <summary>重生点与对手的排除半径（米）：过近的出生点不选。</summary>
        public const float SpawnExclusionRadiusMeters = 8f;

        /// <summary>胜负判定（返回 1=A 胜，-1=B 胜，0=平局）：
        /// 未超时时 20 杀者即时胜（杀数相同再走超时逻辑）；
        /// 超时先比击杀多者，再比死亡少者，仍相同判平局（平局双方 isWin=false，Docs/17 §1.4 奖励按败方档）。</summary>
        public static int EvaluateWinner(int killsA, int deathsA, int killsB, int deathsB, bool timedOut)
        {
            if (!timedOut)
            {
                if (killsA >= TargetKills && killsA > killsB) return 1;
                if (killsB >= TargetKills && killsB > killsA) return -1;
            }
            if (killsA != killsB) return killsA > killsB ? 1 : -1;
            if (deathsA != deathsB) return deathsA < deathsB ? 1 : -1;
            return 0;
        }

        /// <summary>
        /// 多人胜负判定（Phase C 修正：&gt;2 人局不再只取前两名，全部玩家参与排名）：
        /// ① 未超时且最高击杀 ≥ TargetKills 且该击杀数唯一 → 该玩家即时胜；
        /// ② 否则按「击杀最多 → 死亡最少」排名；(击杀, 死亡) 完全并列 → 平局（返回 -1，全部 isWin=false）。
        /// 返回胜者下标；-1 = 平局或入参无效。纯函数，EditMode 可测。
        /// </summary>
        public static int EvaluateWinnerMulti(int[] kills, int[] deaths, bool timedOut)
        {
            if (kills == null || deaths == null) return -1;
            if (kills.Length == 0 || kills.Length != deaths.Length) return -1;

            int maxKills = int.MinValue;
            for (int i = 0; i < kills.Length; i++)
                if (kills[i] > maxKills) maxKills = kills[i];

            if (!timedOut && maxKills >= TargetKills)
            {
                int holder = -1;
                bool tied = false;
                for (int i = 0; i < kills.Length; i++)
                {
                    if (kills[i] != maxKills) continue;
                    if (holder == -1) holder = i;
                    else { tied = true; break; }
                }
                if (!tied) return holder; // 唯一达标者即时胜
            }

            // 排名比较：击杀多者优先；击杀同则死亡少者优先
            int best = 0;
            for (int i = 1; i < kills.Length; i++)
            {
                if (kills[i] > kills[best] ||
                    (kills[i] == kills[best] && deaths[i] < deaths[best]))
                    best = i;
            }
            // (击杀, 死亡) 并列 → 平局
            for (int i = 0; i < kills.Length; i++)
            {
                if (i != best && kills[i] == kills[best] && deaths[i] == deaths[best])
                    return -1;
            }
            return best;
        }
    }
}
