namespace Game.Gameplay.Network
{
    /// <summary>
    /// 联网比赛击杀归因策略（Day2 任务 B，2026-09-07，纯逻辑，EditMode 锁定）：
    /// 在服务器死亡/击杀归因路径统一检查 killer 与 victim 的联网比赛资格
    ///（MatchLifecycle.IsEligibleNetworkPlayer——NetworkObject 已生成 + 有效 Owner + 连接已认证）。
    /// 用户定案：
    /// ① 双方均有资格（真实联网玩家互杀）→ 完整归因：击杀者 kills+1、victim deaths+1、
    ///    广播正式 Kill 事件（kill feed）、可参与 20 杀终局——原行为不变；
    /// ② victim 有资格但 killer 无资格/无归因（环境伤害、假人"击杀"、无击杀者）→ 仅计 victim deaths；
    /// ③ victim 无资格（authored/server-owned/未认证目标，如 Arena 作者假人）→ 零成绩：
    ///    不给击杀者加 kills、不增联网比赛 deaths、不广播 Kill、不可能触达 20 杀终局；
    ///    受击/死亡/重生表现照常保留（世界物体语义，非比赛成员）。
    /// </summary>
    public static class MatchKillAttributionPolicy
    {
        public enum Outcome
        {
            /// <summary>双方有资格：kills+deaths+Kill 广播+可终局（原行为）。</summary>
            FullAttribution,
            /// <summary>仅 victim 有资格（killer 无资格或无归因）：只计 victim deaths。</summary>
            DeathOnly,
            /// <summary>victim 无资格（authored/未认证目标）：零成绩零广播（表现保留）。</summary>
            NoScore,
        }

        /// <summary>
        /// 归因判定。killerEligible 在无击杀者时传 false（无归因不构成击杀）。
        /// </summary>
        public static Outcome Evaluate(bool killerEligible, bool victimEligible)
        {
            if (victimEligible)
                return killerEligible ? Outcome.FullAttribution : Outcome.DeathOnly;
            return Outcome.NoScore;
        }
    }
}
