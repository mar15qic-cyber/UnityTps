namespace Game.Gameplay.Network
{
    /// <summary>比赛事件类型（Docs/23 P1-1/P1-4）。网络铁律自检：只传 Gameplay 语义
    /// （枚举 + JSON 字符串载荷），禁传动画参数/动画名（Docs/04 §8）。</summary>
    public enum MatchEventKind
    {
        CountdownStarted,
        CountdownEnded,
        Kill,
        Ended,
        MatchIdAssigned
    }

    /// <summary>比赛阶段：服务器权威推进（MatchLifecycle），客户端经事件镜像。</summary>
    public enum MatchPhase
    {
        Idle,
        Countdown,
        InProgress,
        Ended
    }
}
