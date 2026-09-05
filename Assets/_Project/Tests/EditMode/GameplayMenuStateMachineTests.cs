using Game.Gameplay.Menu;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase A 游戏菜单——ESC 状态机全分支锁定（纯逻辑，无 Unity 运行时依赖）：
    /// 开合往返、设置返回、重绑中 ESC 只取消重绑不关菜单、确认弹窗返回、硬锁禁开。
    /// MenuMountPolicy：只有本地 Owner 挂菜单、幂等。
    /// </summary>
    public sealed class GameplayMenuStateMachineTests
    {
        // ---- Gameplay ⇄ PauseMenu ----

        [Test]
        public void Escape_GameplayOpensPauseMenu()
        {
            var m = new GameplayMenuStateMachine();
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Gameplay));
            Assert.That(m.TryConsumeEscape(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.PauseMenu));
            Assert.That(m.MenuVisible, Is.True);
        }

        [Test]
        public void Escape_PauseMenuClosesToGameplay()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            Assert.That(m.TryConsumeEscape(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Gameplay));
            Assert.That(m.MenuVisible, Is.False);
        }

        [Test]
        public void ResumeButton_FromPauseMenuAndSettings()
        {
            var m = new GameplayMenuStateMachine();
            Assert.That(m.TryResume(), Is.False, "Gameplay 态无「返回游戏」");
            m.TryConsumeEscape();
            Assert.That(m.TryResume(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Gameplay));
        }

        // ---- Settings 链 ----

        [Test]
        public void Settings_RoundTripViaEscapeAndBack()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            Assert.That(m.TryOpenSettings(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Settings));
            Assert.That(m.TryConsumeEscape(), Is.True, "Settings → ESC → PauseMenu");
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.PauseMenu));
            Assert.That(m.TryBackToPause(), Is.False, "已不在 Settings，返回按钮无效");
        }

        [Test]
        public void OpenSettings_OnlyFromPauseMenu()
        {
            var m = new GameplayMenuStateMachine();
            Assert.That(m.TryOpenSettings(), Is.False, "Gameplay 直接进设置非法");
            m.TryConsumeEscape(); // PauseMenu
            m.TryOpenSettings();
            Assert.That(m.TryOpenSettings(), Is.False, "Settings 内重复打开无效");
        }

        // ---- 重绑：ESC 只取消重绑（核心优先级） ----

        [Test]
        public void Escape_DuringRebindOnlyCancelsRebind()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            m.TryOpenSettings();
            Assert.That(m.BeginRebind(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.RebindCapture));

            Assert.That(m.TryConsumeEscape(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Settings), "ESC 只取消重绑，菜单保持打开");
            Assert.That(m.MenuVisible, Is.True);
        }

        [Test]
        public void BeginRebind_OnlyInSettings()
        {
            var m = new GameplayMenuStateMachine();
            Assert.That(m.BeginRebind(), Is.False);
            m.TryConsumeEscape(); // PauseMenu
            Assert.That(m.BeginRebind(), Is.False);
        }

        [Test]
        public void CompleteRebind_ReturnsToSettings()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            m.TryOpenSettings();
            m.BeginRebind();
            Assert.That(m.CompleteRebind(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Settings));
        }

        // ---- 退出确认弹窗 ----

        [Test]
        public void LeaveConfirm_RoundTrip()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            Assert.That(m.BeginLeaveConfirm(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.LeaveConfirm));
            Assert.That(m.TryConsumeEscape(), Is.True, "弹窗 ESC → PauseMenu");
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.PauseMenu));

            m.BeginLeaveConfirm();
            Assert.That(m.CancelLeaveConfirm(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.PauseMenu));
        }

        [Test]
        public void LeaveConfirm_CannotOpenSettingsDirectly()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            m.BeginLeaveConfirm();
            Assert.That(m.TryOpenSettings(), Is.False);
            Assert.That(m.TryResume(), Is.False);
        }

        // ---- 硬锁（终局/场景切换） ----

        [Test]
        public void Lock_BlocksAllOpenPathsAndEscape()
        {
            var m = new GameplayMenuStateMachine();
            m.ForceCloseAndLock(GameplayMenuLockReason.MatchEnded);
            Assert.That(m.IsLocked, Is.True);
            Assert.That(m.MenuVisible, Is.False);
            Assert.That(m.TryConsumeEscape(), Is.False, "硬锁下 ESC 不做任何事");
            Assert.That(m.TryOpenSettings(), Is.False);
            Assert.That(m.TryResume(), Is.False);
            Assert.That(m.BeginLeaveConfirm(), Is.False);
            Assert.That(m.ForceClose(), Is.False, "硬锁不可被 ForceClose 解除");
        }

        [Test]
        public void ForceClose_FromAnyOpenState()
        {
            var m = new GameplayMenuStateMachine();
            m.TryConsumeEscape();
            m.TryOpenSettings();
            Assert.That(m.ForceClose(), Is.True);
            Assert.That(m.State, Is.EqualTo(GameplayMenuState.Gameplay));
            Assert.That(m.IsLocked, Is.False, "普通 ForceClose 允许再次打开");
            Assert.That(m.TryConsumeEscape(), Is.True);
        }

        // ---- 挂载策略：只有本地 Owner 拥有菜单 ----

        [Test]
        public void MountPolicy_OwnerOnlyAndIdempotent()
        {
            Assert.That(MenuMountPolicy.ShouldMount(isLocalOwner: true, alreadyMounted: false), Is.True);
            Assert.That(MenuMountPolicy.ShouldMount(isLocalOwner: false, alreadyMounted: false), Is.False,
                "远端玩家对象不能生成菜单");
            Assert.That(MenuMountPolicy.ShouldMount(isLocalOwner: true, alreadyMounted: true), Is.False,
                "重复挂载必须被幂等闸拦下");
            Assert.That(MenuMountPolicy.ShouldMount(isLocalOwner: false, alreadyMounted: true), Is.False);
        }
    }
}
