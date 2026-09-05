using Game.Gameplay.Menu;
using Game.UI.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Arena 游戏菜单视图结构测试（EditMode，零资产）：视图经 TryMount 构建、
    /// 随状态显隐、控制器回接完整。回归锁定一次真实事故——TryMount 曾漏把 controller
    /// 回接给视图（_controller 恒 null），导致 ESC 后状态机在开、光标解锁、但菜单画布
    /// 永不激活（Arena 暂停 UI 隐形）。本套件确保「开菜单=画布必须可见」。
    /// </summary>
    public sealed class GameplayMenuViewTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            // 画布初始即隐藏——查找必须含未激活对象
            var view = Object.FindFirstObjectByType<GameplayMenuView>(FindObjectsInactive.Include);
            if (view != null) Object.DestroyImmediate(view.gameObject);
            var controller = Object.FindFirstObjectByType<GameplayMenuController>(FindObjectsInactive.Include);
            if (controller != null) Object.DestroyImmediate(controller.gameObject);
            GameplayInputGate.ResetAll();
        }

        private (GameplayMenuController controller, GameplayMenuView view) Mount()
        {
            _root = new GameObject("MenuRoot");
            var controller = _root.AddComponent<GameplayMenuController>();
            GameplayMenuView.TryMount(controller);
            return (controller, Object.FindFirstObjectByType<GameplayMenuView>(FindObjectsInactive.Include));
        }

        /// <summary>控制器 ApplyState 为私有（由 Update 的 ESC 路由调用）——EditMode 经反射驱动同一入口。</summary>
        private static void ApplyState(GameplayMenuController controller)
        {
            var method = typeof(GameplayMenuController).GetMethod("ApplyState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "ApplyState missing");
            method.Invoke(controller, null);
        }

        [Test]
        public void TryMount_CreatesCanvas_StartsHidden()
        {
            var (controller, view) = Mount();
            Assert.That(view, Is.Not.Null, "TryMount 必须创建菜单画布");
            Assert.That(view.gameObject.activeSelf, Is.False, "初始 Gameplay 态菜单必须隐藏");
            Assert.That(controller.Machine.State, Is.EqualTo(GameplayMenuState.Gameplay));
            Assert.That(view.transform.Find("Veil"), Is.Not.Null, "暗色半透明纱缺失");
            Assert.That(view.transform.Find("NavRail"), Is.Not.Null, "左侧导航缺失");
        }

        [Test]
        public void EscapeOpen_MustActivateCanvas_ControllerWiredRegression()
        {
            // 回归锁定：_controller 未回接时此断言失败（菜单隐形事故）
            var (controller, view) = Mount();
            Assert.That(controller.Machine.TryConsumeEscape(), Is.True);
            ApplyState(controller);
            Assert.That(view.gameObject.activeSelf, Is.True,
                "ESC 打开菜单后画布必须激活（若失败=视图未拿到 controller 引用）");
            Assert.That(view.transform.Find("PauseHome").gameObject.activeSelf, Is.True);
        }

        [Test]
        public void Resume_DeactivatesCanvas()
        {
            var (controller, view) = Mount();
            controller.Machine.TryConsumeEscape();
            ApplyState(controller);
            controller.RequestResume(); // 公共按钮路径（内部调 ApplyState）
            Assert.That(view.gameObject.activeSelf, Is.False, "返回游戏后画布必须隐藏");
        }

        [Test]
        public void Settings_PageSwitchesWithinMenu()
        {
            var (controller, view) = Mount();
            controller.Machine.TryConsumeEscape();
            ApplyState(controller);
            controller.RequestOpenSettings();
            Assert.That(view.gameObject.activeSelf, Is.True);
            Assert.That(view.transform.Find("SettingsHost").gameObject.activeSelf, Is.True, "设置页应显示");
            Assert.That(view.transform.Find("PauseHome").gameObject.activeSelf, Is.False, "暂停主页应隐藏");
        }

        [Test]
        public void LeaveConfirm_DialogShowsAndCancels()
        {
            var (controller, view) = Mount();
            controller.Machine.TryConsumeEscape();
            ApplyState(controller);
            controller.RequestLeave();
            Assert.That(view.transform.Find("LeaveDialog").gameObject.activeSelf, Is.True, "退出确认弹窗应显示");
            controller.CancelLeave();
            Assert.That(view.transform.Find("LeaveDialog").gameObject.activeSelf, Is.False);
            Assert.That(view.transform.Find("PauseHome").gameObject.activeSelf, Is.True);
        }

        [Test]
        public void TryMount_IsIdempotent()
        {
            var (controller, _) = Mount();
            GameplayMenuView.TryMount(controller); // 二次挂载不得新建画布（含隐藏画布）
            Assert.That(Object.FindObjectsByType<GameplayMenuView>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(1));
        }
    }
}
