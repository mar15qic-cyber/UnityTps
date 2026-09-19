using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 击杀竞赛规则（Docs/23 P1-1，G4）：纯静态函数 + 常量，无 Unity 依赖——
    /// 可测性设计先例同 FPAimAnimStateMachine；判定唯一真相，MatchLifecycle 只做推进。
    /// C3/Q04 增补 TDM 团队规则（Docs/26 §2.4）：纯函数团队胜负/友伤/重生点/队伍容量。
    /// </summary>
    public static class MatchRules
    {
        /// <summary>击杀目标数：任一玩家达到即终局（Docs/17 §1.4）。CF 房间对局由设置注入覆盖（本值为离线默认）。</summary>
        public const int TargetKills = 20;

        /// <summary>对局长度上限（秒），超时按击杀数判定。CF 房间对局由设置注入覆盖（本值为离线默认）。</summary>
        public const int MatchTimeLimitSeconds = 600;

        /// <summary>开局倒计时（秒），倒计时期间输入冻结。</summary>
        public const float CountdownSeconds = 3f;

        /// <summary>助攻判定窗口（秒）：目标死亡前窗口内对其造成过伤害的射手计入助攻（战绩面板）。</summary>
        public const float AssistWindowSeconds = 10f;

        /// <summary>重生点与对手的排除半径（米）：过近的出生点不选。</summary>
        public const float SpawnExclusionRadiusMeters = 8f;

        /// <summary>死亡到服务器执行重生的固定延迟（秒）：替代历史硬编码 Invoke(3f)，
        /// 服务器以 tick 驱动到点执行；deadline 经 SyncVar 下发供 HUD 倒计时。</summary>
        public const float RespawnDelaySeconds = 3f;

        /// <summary>重生后的出生保护时长（秒）：服务器伤害结算识别该窗口并拒绝有效伤害
        /// （含 LagComp 回溯命中——按"射击对应历史 tick 的无敌快照"判定，见 ServerLagCompensation）。</summary>
        public const float SpawnProtectionSeconds = 2.5f;

        /// <summary>tick 换算回退节拍：FishNet TimeManager 默认 30Hz；换算函数遇非法 tickRate 时使用。</summary>
        public const int DefaultTickRate = 30;

        /// <summary>秒 → tick 数（向上取整）。非法 tickRate（&lt;=0）回退 DefaultTickRate；负秒返回 0。纯函数，EditMode 可测。</summary>
        public static uint SecondsToTicks(float seconds, int tickRate)
        {
            if (seconds <= 0f) return 0u;
            int rate = tickRate > 0 ? tickRate : DefaultTickRate;
            return (uint)System.Math.Ceiling(seconds * rate);
        }

        /// <summary>tick 数 → 秒。非法 tickRate（&lt;=0）回退 DefaultTickRate。纯函数，EditMode 可测。</summary>
        public static float TicksToSeconds(uint ticks, int tickRate)
        {
            int rate = tickRate > 0 ? tickRate : DefaultTickRate;
            return ticks / (float)rate;
        }

        // ---- 队伍标识（Docs/27 §1 冻结值的 Game.Gameplay 本地镜像；asmdef 不引用 Game.Account）----

        public const string TeamNone = "None";
        public const string TeamRed = "Red";
        public const string TeamBlue = "Blue";

        /// <summary>对局模式字面值（Docs/27 §1；本地镜像）。</summary>
        public const string ModeTdm = "TDM";
        public const string ModeKillRace = "KillRace";

        /// <summary>TDM 每队上限（Docs/27 §1：maxPlayers / 2；下限 1）。</summary>
        public static int PerTeamCapacity(int maxPlayers) => System.Math.Max(1, maxPlayers / 2);

        /// <summary>
        /// TDM 团队胜负（Docs/26 §2.4，纯函数）：达到目标立即结束（达标题材方胜）；
        /// 超时按团队击杀比较，相同判平局（null = 平局/无胜者——双方同时归零同语义）。
        /// 返回 "Red"/"Blue"，null = 平局。
        /// </summary>
        public static string EvaluateTeamWinner(int redKills, int blueKills, int targetKills, bool timedOut)
        {
            if (targetKills <= 0) targetKills = TargetKills;
            if (redKills == blueKills) return null;
            if (!timedOut)
            {
                if (redKills >= targetKills) return TeamRed;
                if (blueKills >= targetKills) return TeamBlue;
            }
            return redKills > blueKills ? TeamRed : TeamBlue;
        }

        /// <summary>
        /// 团队伤害过滤（Docs/26 §2.4：关闭友伤，友军身体仍可阻挡子弹）：同队非 None → 禁止伤害。
        /// 返回 true = 允许伤害。None 队（KillRace/离线）恒允许。
        /// </summary>
        public static bool IsDamageAllowed(string shooterTeam, string victimTeam)
        {
            if (string.IsNullOrEmpty(shooterTeam) || string.IsNullOrEmpty(victimTeam)) return true;
            if (shooterTeam == TeamNone || victimTeam == TeamNone) return true;
            return shooterTeam != victimTeam;
        }

        /// <summary>
        /// 重生点选择（Docs/26 §2.4：优先远离敌人的安全点，无安全点选最远点；纯函数）：
        /// ① 排除与任一敌人距离 &lt; SpawnExclusionRadiusMeters 的候选；
        /// ② 幸存候选中选「到最近敌人的距离」最大者（索引稳定：并列取最小下标）；
        /// ③ 全部被排除 → 全集中选最远者；无敌人 → 取候选[0]（确定性）。返回选中下标（-1 = 无候选）。
        /// </summary>
        public static int SelectRespawnPoint(Vector3[] candidates, Vector3[] enemyPositions)
        {
            if (candidates == null || candidates.Length == 0) return -1;
            if (enemyPositions == null || enemyPositions.Length == 0) return 0;

            int safest = -1;
            float safestDistance = float.MinValue;
            int farthest = -1;
            float farthestDistance = float.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                float nearestEnemy = float.MaxValue;
                for (int j = 0; j < enemyPositions.Length; j++)
                {
                    float distance = Vector3.Distance(candidates[i], enemyPositions[j]);
                    if (distance < nearestEnemy) nearestEnemy = distance;
                }
                if (nearestEnemy > farthestDistance)
                {
                    farthestDistance = nearestEnemy;
                    farthest = i;
                }
                if (nearestEnemy >= SpawnExclusionRadiusMeters && nearestEnemy > safestDistance)
                {
                    safestDistance = nearestEnemy;
                    safest = i;
                }
            }
            return safest >= 0 ? safest : farthest;
        }

        /// <summary>TDM 补人/重连的队伍容量终验（纯函数，I3/Docs/26 §2.4）：占用（已生成玩家 + Pending
        /// 认证连接）+ 本次接入 ≤ 每队上限。KillRace/旧票（mode 空）不设限；TDM 无队伍 = 异常拒绝。</summary>
        public static bool CanJoinTeam(string mode, int maxPlayers, string teamId, int occupiedSlots)
        {
            if (mode != ModeTdm) return true;
            if (teamId != TeamRed && teamId != TeamBlue) return false;
            return occupiedSlots + 1 <= PerTeamCapacity(maxPlayers);
        }

        /// <summary>
        /// TDM 队伍出生点选择（纯函数，I3）：Arena 无显式出生组时按几何中轴切分
        /// （x &lt; 全体质心 → 红半场，其余 → 蓝半场），优先己方半场内远离敌人的安全点；
        /// 己方半场为空/满场退化 → 全集规则。KillRace/None → 全集选点。返回 allPoints 下标（-1 = 无候选）。
        /// </summary>
        public static int SelectTeamRespawnPoint(Vector3[] allPoints, string team, string mode, Vector3[] enemyPositions)
        {
            if (allPoints == null || allPoints.Length == 0) return -1;
            if (mode != ModeTdm || (team != TeamRed && team != TeamBlue))
                return SelectRespawnPoint(allPoints, enemyPositions);

            Vector3 centroid = Vector3.zero;
            foreach (var point in allPoints) centroid += point;
            centroid /= allPoints.Length;

            var ownIndices = new System.Collections.Generic.List<int>();
            for (int i = 0; i < allPoints.Length; i++)
            {
                bool redSide = allPoints[i].x < centroid.x;
                if ((team == TeamRed) == redSide) ownIndices.Add(i);
            }
            if (ownIndices.Count == 0 || ownIndices.Count == allPoints.Length)
                return SelectRespawnPoint(allPoints, enemyPositions);

            var ownPositions = new Vector3[ownIndices.Count];
            for (int i = 0; i < ownIndices.Count; i++) ownPositions[i] = allPoints[ownIndices[i]];
            int ownPick = SelectRespawnPoint(ownPositions, enemyPositions);
            return ownPick >= 0 ? ownIndices[ownPick] : SelectRespawnPoint(allPoints, enemyPositions);
        }

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
