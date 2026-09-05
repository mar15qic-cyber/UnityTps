namespace Game.Gameplay.Menu
{
    /// <summary>游戏菜单页状态（ESC 状态机）。</summary>
    public enum GameplayMenuState
    {
        /// <summary>对局进行中，菜单关闭。</summary>
        Gameplay = 0,
        /// <summary>暂停菜单主页（返回游戏 / 设置 / 退出对局）。</summary>
        PauseMenu = 1,
        /// <summary>设置页。</summary>
        Settings = 2,
        /// <summary>键位重绑捕获中（ESC 只取消重绑，不关菜单）。</summary>
        RebindCapture = 3,
        /// <summary>退出对局确认弹窗。</summary>
        LeaveConfirm = 4,
        /// <summary>硬锁：终局/场景切换等，禁止再打开菜单。</summary>
        Locked = 5,
    }

    /// <summary>硬锁原因。</summary>
    public enum GameplayMenuLockReason
    {
        None = 0,
        /// <summary>比赛已结束（Ended 载荷到达）。</summary>
        MatchEnded = 1,
        /// <summary>场景正在切换/卸载。</summary>
        SceneTransition = 2,
    }

    /// <summary>
    /// 游戏菜单 ESC 状态机（Phase A，纯逻辑无 Unity 依赖——EditMode 可直测）。
    /// 转移规则：
    ///   Gameplay → ESC → PauseMenu；PauseMenu → ESC/返回游戏 → Gameplay；
    ///   Settings → ESC/返回 → PauseMenu；
    ///   键位重绑中 ESC：只取消重绑（RebindCapture → Settings），不关闭整个菜单；
    ///   退出确认弹窗 ESC → PauseMenu；
    ///   Locked（终局/场景切换）下一切打开请求无效。
    /// 打开/关闭的副作用（光标、输入门控、草稿回滚）由 GameplayMenuController 按状态变化执行。
    /// </summary>
    public sealed class GameplayMenuStateMachine
    {
        public GameplayMenuState State { get; private set; } = GameplayMenuState.Gameplay;
        public GameplayMenuLockReason LockReason { get; private set; } = GameplayMenuLockReason.None;

        /// <summary>菜单 UI 是否应可见（含重绑/确认弹窗；Locked 与 Gameplay 均为关）。</summary>
        public bool MenuVisible => State != GameplayMenuState.Gameplay && State != GameplayMenuState.Locked;

        /// <summary>是否处于硬锁（终局/场景切换，禁止再打开）。</summary>
        public bool IsLocked => State == GameplayMenuState.Locked;

        /// <summary>是否允许 ESC 触发菜单开合转移（重绑中 ESC 被状态机优先消费为「取消重绑」）。</summary>
        public bool EscConsumed => State != GameplayMenuState.Locked;

        /// <summary>
        /// ESC 键路由（唯一入口）。返回是否消费了该键（消费后调用方不得再把它当游戏输入）。
        /// 优先级：重绑取消 &gt; 弹窗返回 &gt; 页面返回 &gt; 菜单开合。
        /// </summary>
        public bool TryConsumeEscape()
        {
            switch (State)
            {
                case GameplayMenuState.RebindCapture:
                    State = GameplayMenuState.Settings; // 只取消重绑，菜单保持
                    return true;
                case GameplayMenuState.LeaveConfirm:
                case GameplayMenuState.Settings:
                    State = GameplayMenuState.PauseMenu;
                    return true;
                case GameplayMenuState.PauseMenu:
                    State = GameplayMenuState.Gameplay; // 关闭菜单
                    return true;
                case GameplayMenuState.Gameplay:
                    State = GameplayMenuState.PauseMenu; // 打开菜单
                    return true;
                case GameplayMenuState.Locked:
                default:
                    return false; // 硬锁：ESC 不做任何事
            }
        }

        /// <summary>「返回游戏」按钮：PauseMenu/Settings → Gameplay（关闭菜单）。</summary>
        public bool TryResume()
        {
            if (State != GameplayMenuState.PauseMenu && State != GameplayMenuState.Settings) return false;
            State = GameplayMenuState.Gameplay;
            return true;
        }

        /// <summary>「设置」按钮：PauseMenu → Settings。</summary>
        public bool TryOpenSettings()
        {
            if (State != GameplayMenuState.PauseMenu) return false;
            State = GameplayMenuState.Settings;
            return true;
        }

        /// <summary>设置页「返回」按钮：Settings → PauseMenu。</summary>
        public bool TryBackToPause()
        {
            if (State != GameplayMenuState.Settings) return false;
            State = GameplayMenuState.PauseMenu;
            return true;
        }

        /// <summary>开始键位重绑捕获（仅设置页内允许）。</summary>
        public bool BeginRebind()
        {
            if (State != GameplayMenuState.Settings) return false;
            State = GameplayMenuState.RebindCapture;
            return true;
        }

        /// <summary>重绑完成/冲突取消：回到设置页。</summary>
        public bool CompleteRebind()
        {
            if (State != GameplayMenuState.RebindCapture) return false;
            State = GameplayMenuState.Settings;
            return true;
        }

        /// <summary>「退出对局」：PauseMenu → 确认弹窗。</summary>
        public bool BeginLeaveConfirm()
        {
            if (State != GameplayMenuState.PauseMenu) return false;
            State = GameplayMenuState.LeaveConfirm;
            return true;
        }

        /// <summary>确认弹窗取消 → PauseMenu。</summary>
        public bool CancelLeaveConfirm()
        {
            if (State != GameplayMenuState.LeaveConfirm) return false;
            State = GameplayMenuState.PauseMenu;
            return true;
        }

        /// <summary>强制关闭并回到 Gameplay（死亡/掉线等；之后允许再次打开）。</summary>
        public bool ForceClose()
        {
            if (State == GameplayMenuState.Locked) return false;
            State = GameplayMenuState.Gameplay;
            return true;
        }

        /// <summary>硬锁（终局/场景切换）：菜单立即关闭且禁止再打开。幂等。</summary>
        public void ForceCloseAndLock(GameplayMenuLockReason reason)
        {
            State = GameplayMenuState.Locked;
            LockReason = reason;
        }
    }
}
