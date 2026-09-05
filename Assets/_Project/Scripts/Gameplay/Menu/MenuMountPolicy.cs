namespace Game.Gameplay.Menu
{
    /// <summary>
    /// 菜单挂载策略（Phase A 纯函数）：只有「本地 Owner」才允许拥有/挂载游戏菜单；
    /// 远端玩家对象与已存在实例一律不挂（幂等）。EditMode 锁定语义。
    /// </summary>
    public static class MenuMountPolicy
    {
        public static bool ShouldMount(bool isLocalOwner, bool alreadyMounted)
            => isLocalOwner && !alreadyMounted;
    }
}
