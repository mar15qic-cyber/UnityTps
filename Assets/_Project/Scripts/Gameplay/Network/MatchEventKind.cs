namespace Game.Gameplay.Network
{
    /// <summary>比赛事件类型（Docs/23 P1-1/P1-4 + 退出对局 Phase C 扩展）。网络铁律自检：
    /// 只传 Gameplay 语义（枚举 + JSON 字符串载荷），禁传动画参数/动画名（Docs/04 §8）。</summary>
    public enum MatchEventKind
    {
        CountdownStarted,
        CountdownEnded,
        Kill,
        Ended,
        MatchIdAssigned,
        /// <summary>玩家离开（&gt;2 人局仅移除不终局）：载荷含离开者 id 与比分快照（HUD/提示用）。</summary>
        PlayerLeft
    }

    /// <summary>比赛阶段：服务器权威推进（MatchLifecycle），客户端经事件镜像。</summary>
    public enum MatchPhase
    {
        Idle,
        Countdown,
        InProgress,
        Ended
    }

    /// <summary>终局原因（Phase C）：Normal=达标终局；TimeLimit=超时；PlayerLeft=2 人局任一离开；
    /// HostLost=主机消失（client-hosted 无法迁移时，客户端本地判定，服务器已不存在无法广播）。</summary>
    public enum MatchEndReason
    {
        Normal = 0,
        TimeLimit = 1,
        PlayerLeft = 2,
        HostLost = 3
    }

    /// <summary>玩家离开原因：主动（菜单退出）/ 非主动断线（网络中断、崩溃、强关）。服务器侧统一走同一策略。</summary>
    public enum PlayerLeaveReason
    {
        Voluntary = 0,
        Disconnected = 1
    }
}
