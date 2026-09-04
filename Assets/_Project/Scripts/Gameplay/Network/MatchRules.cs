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
    }
}
