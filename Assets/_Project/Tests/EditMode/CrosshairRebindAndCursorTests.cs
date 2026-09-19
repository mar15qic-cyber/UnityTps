using System.Reflection;
using Game.Gameplay.Menu;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-18 实机问题4/3 回归：
    /// ① CrosshairPresenter.Rebind——联网 Owner 重绑（旧实现 Awake 一次性绑定场景作者玩家，
    ///    在线模式作者玩家被模式门禁用 → IsInitialized 恒 false → 准星恒隐藏）；
    ///    换绑必须精确迁移 OnShotFired 订阅（旧控制器退订、新控制器订阅）。
    /// ② GameplayMenuController.ShouldLockCursor——光标锁定三豁免（菜单/聊天/状态机锁定）。
    /// </summary>
    public sealed class CrosshairRebindAndCursorTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly System.Collections.Generic.List<Object> _spawned = new();

        private T Track<T>(T obj) where T : Object { _spawned.Add(obj); return obj; }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _spawned)
                if (obj != null) Object.DestroyImmediate(obj);
            _spawned.Clear();
        }

        private WeaponController NewController(string name)
        {
            var go = Track(new GameObject(name));
            return go.AddComponent<WeaponController>();
        }

        private static int ShotSubscriberCount(WeaponController controller)
        {
            var d = (System.Delegate)typeof(WeaponController).GetField("OnShotFired", NonPublic).GetValue(controller);
            return d?.GetInvocationList().Length ?? 0;
        }

        [Test]
        public void CrosshairRebind_SwapsDataSources_AndMigratesShotSubscription()
        {
            var presenterGo = Track(new GameObject("CrosshairPresenter_Test"));
            var presenter = presenterGo.AddComponent<CrosshairPresenter>();
            var oldController = NewController("OldController");
            var newController = NewController("NewController");
            var newAimGo = Track(new GameObject("NewAim"));
            var newAim = newAimGo.AddComponent<PlayerAimState>();
            var newStateGo = Track(new GameObject("NewState"));
            var newState = newStateGo.AddComponent<PlayerStateView>();

            // 初始绑定=场景作者对象（模拟 Awake FindObjectOfType 结果）+ 已订阅旧控制器
            typeof(CrosshairPresenter).GetField("controller", NonPublic).SetValue(presenter, oldController);
            typeof(CrosshairPresenter).GetMethod("OnEnable", NonPublic).Invoke(presenter, null);
            Assert.That(ShotSubscriberCount(oldController), Is.EqualTo(1), "前置：已订阅旧控制器");

            presenter.Rebind(newController, newAim, newState);

            var bound = (WeaponController)typeof(CrosshairPresenter).GetField("controller", NonPublic).GetValue(presenter);
            Assert.That(bound, Is.SameAs(newController), "控制器必须换绑到新 Owner 玩家");
            Assert.That(ShotSubscriberCount(oldController), Is.EqualTo(0), "旧控制器必须退订（防泄漏/双写）");
            Assert.That(ShotSubscriberCount(newController), Is.EqualTo(1), "新控制器必须订阅（命中标记数据源）");
            Assert.That(typeof(CrosshairPresenter).GetField("aimState", NonPublic).GetValue(presenter),
                Is.SameAs(newAim), "ADS 状态源必须换绑");
            Assert.That(typeof(CrosshairPresenter).GetField("playerState", NonPublic).GetValue(presenter),
                Is.SameAs(newState), "冲刺状态源必须换绑");

            // 幂等：再次以同一控制器重绑不得叠加订阅
            presenter.Rebind(newController, newAim, newState);
            Assert.That(ShotSubscriberCount(newController), Is.EqualTo(1), "幂等重绑不得叠加订阅");
        }

        [Test]
        public void CrosshairRebind_NullController_KeepsExistingBinding()
        {
            var presenterGo = Track(new GameObject("CrosshairPresenter_Null_Test"));
            var presenter = presenterGo.AddComponent<CrosshairPresenter>();
            var oldController = NewController("OldController2");
            typeof(CrosshairPresenter).GetField("controller", NonPublic).SetValue(presenter, oldController);

            presenter.Rebind(null, null, null); // 玩家对象未装配完成时的保护路径

            var bound = (WeaponController)typeof(CrosshairPresenter).GetField("controller", NonPublic).GetValue(presenter);
            Assert.That(bound, Is.SameAs(oldController), "空重绑不得清空既有绑定");
        }

        [Test]
        public void ShouldLockCursor_ThreeExemptions()
        {
            Assert.That(GameplayMenuController.ShouldLockCursor(false, false, false), Is.True,
                "对局中（无菜单/无聊天/无锁定）必须锁定光标");
            Assert.That(GameplayMenuController.ShouldLockCursor(true, false, false), Is.False,
                "菜单可见时不得锁（菜单按钮要可点）");
            Assert.That(GameplayMenuController.ShouldLockCursor(false, true, false), Is.False,
                "聊天聚焦时不得锁（ChatHudView 拥有光标）");
            Assert.That(GameplayMenuController.ShouldLockCursor(false, false, true), Is.False,
                "状态机锁定（SceneTransition/MatchEnded）时不得锁（结算页/切换窗口要自由光标）");
        }
    }
}
