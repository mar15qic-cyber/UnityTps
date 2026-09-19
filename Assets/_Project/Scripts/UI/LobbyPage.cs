namespace Game.UI
{
    public enum LobbyPage
    {
        Boot,
        Login,
        Register,
        Identity,
        Lobby,
        Mission,
        Armory,
        WeaponDetails,
        Shop,
        Upgrades,
        Settings,
        Hud,
        Pause,
        Results,
        Loading,
        Error,
        SessionExpired,
        OnlineJoin,
        WaitingRoom,
        /// <summary>热更页（稳定 seam）：实际页面 id 在 LobbyPresenter.currentHotPageId，渲染委托来自 HotPageRegistry。</summary>
        Hot
    }

    // Compatibility surface for the existing EditMode contract test. New code uses LobbyPage.
    public enum LobbyFlowState
    {
        Login,
        Main,
        Loadout,
        Upgrade
    }
}

