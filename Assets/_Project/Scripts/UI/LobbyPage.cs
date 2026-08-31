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
        SessionExpired
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

