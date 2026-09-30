namespace Game.UI
{
    public enum LobbyPage
    {
        Boot = 0,
        Login = 1,
        Register = 2,
        Identity = 3,
        Lobby = 4,

        Armory = 6,
        WeaponDetails = 7,
        Shop = 8,

        Settings = 10,
        Hud = 11,
        Pause = 12,
        Results = 13,
        Loading = 14,
        Error = 15,
        SessionExpired = 16,
        OnlineJoin = 17,
        WaitingRoom = 18,
        /// <summary>好友页（2026-09-20 需求2）：搜索 用户名#编码 + 申请收发箱 + 好友列表（在线状态）。</summary>
        Friends = 19,
        /// <summary>热更页（稳定 seam）：实际页面 id 在 LobbyPresenter.currentHotPageId，渲染委托来自 HotPageRegistry。</summary>
        Hot = 20,
    }

    // Compatibility surface for the existing EditMode contract test. New code uses LobbyPage.
    public enum LobbyFlowState
    {
        Login = 1,
        Main,
        Loadout,
    }
}

