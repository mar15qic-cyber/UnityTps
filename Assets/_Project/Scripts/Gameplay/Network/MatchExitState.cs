namespace Game.Gameplay.Network
{
    /// <summary>
    /// 对局退出/终局的跨端协调标记（Phase C）：Gameplay 与 Game.UI（结算流）之间的
    /// 导航权仲裁。2 人退出终局时，Ended 载荷先于断线到达，结算流（MatchSettlementFlow）
    /// 接管「提交 → 回大厅 → Results」导航并置位本标记；退出流程（菜单协程/断线观测）
    /// 看到置位即不再重复加载大厅，避免双 LoadScene 互相踩踏。
    /// 服务器一次比赛只广播一次 Ended（MatchLifecycle._endedBroadcast），本标记同源幂等。
    /// </summary>
    public static class MatchExitState
    {
        /// <summary>结算流已接管本次终局的回大厅导航（客户端本地标记）。</summary>
        public static bool SettlementNavigationPending { get; set; }

        /// <summary>本端已按 HostLost/断线完成安全清理并回大厅（防重入）。</summary>
        public static bool DisconnectHandled { get; set; }

        public static void Reset()
        {
            SettlementNavigationPending = false;
            DisconnectHandled = false;
        }
    }
}
