using Game.Gameplay.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase A 输入门控锁定：菜单打开=屏蔽；关闭=1 帧恢复宽限（防「返回游戏」点击误开火）；
    /// 硬锁/死亡原因独立叠加；全程 Time.timeScale 恒为 1（网络不暂停红线——本套件锁定
    /// 「门控任何操作都不触碰时间缩放」这一不变量）。
    /// </summary>
    public sealed class GameplayInputGateTests
    {
        [SetUp]
        public void SetUp() => GameplayInputGate.ResetAll();

        [TearDown]
        public void TearDown() => GameplayInputGate.ResetAll();

        [Test]
        public void MenuOpen_BlocksInput()
        {
            Assert.That(GameplayInputGate.InputBlocked, Is.False);
            GameplayInputGate.SetMenuOpen(true);
            Assert.That(GameplayInputGate.InputBlocked, Is.True);
            Assert.That(GameplayInputGate.MenuOpen, Is.True);
        }

        [Test]
        public void MenuClose_GrantsOneFrameGraceThenResumes()
        {
            GameplayInputGate.SetMenuOpen(true);
            GameplayInputGate.SetMenuOpen(false);
            Assert.That(GameplayInputGate.InputBlocked, Is.True, "关闭当帧仍屏蔽（点击帧不进游戏输入）");
            Assert.That(GameplayInputGate.ResumeGraceFrames, Is.EqualTo(1));

            GameplayInputGate.TickFrame();
            Assert.That(GameplayInputGate.InputBlocked, Is.False, "下一帧恢复采样");
            Assert.That(GameplayInputGate.ResumeGraceFrames, Is.EqualTo(0));
        }

        [Test]
        public void HardLock_BlocksAndSuppressesMenuFlag()
        {
            GameplayInputGate.SetMenuOpen(true);
            GameplayInputGate.SetHardLocked(true);
            Assert.That(GameplayInputGate.InputBlocked, Is.True);
            Assert.That(GameplayInputGate.MenuOpen, Is.False, "硬锁同时收掉菜单开标志");
            Assert.That(GameplayInputGate.HardLocked, Is.True);
        }

        [Test]
        public void Dead_BlocksIndependently()
        {
            GameplayInputGate.SetDead(true);
            Assert.That(GameplayInputGate.InputBlocked, Is.True);
            GameplayInputGate.SetDead(false);
            Assert.That(GameplayInputGate.InputBlocked, Is.False);
        }

        [Test]
        public void Grace_CannotExceedGrantedMax()
        {
            GameplayInputGate.GrantResumeGrace(5);
            Assert.That(GameplayInputGate.ResumeGraceFrames, Is.EqualTo(5));
            for (var i = 0; i < 10; i++) GameplayInputGate.TickFrame();
            Assert.That(GameplayInputGate.ResumeGraceFrames, Is.EqualTo(0));
            Assert.That(GameplayInputGate.InputBlocked, Is.False);
        }

        [Test]
        public void TimeScale_NeverTouchedByGateOperations()
        {
            // 红线自检：菜单开/关、硬锁、死亡、宽限全流程后 timeScale 仍是 1
            GameplayInputGate.SetMenuOpen(true);
            GameplayInputGate.SetMenuOpen(false);
            GameplayInputGate.SetHardLocked(true);
            GameplayInputGate.SetDead(true);
            GameplayInputGate.GrantResumeGrace(3);
            GameplayInputGate.ResetAll();
            Assert.That(Time.timeScale, Is.EqualTo(1f), "菜单系统绝不写 Time.timeScale（网络游戏不暂停）");
        }

        [Test]
        public void ResetAll_ClearsEverything()
        {
            GameplayInputGate.SetMenuOpen(true);
            GameplayInputGate.SetDead(true);
            GameplayInputGate.GrantResumeGrace(4);
            GameplayInputGate.ResetAll();
            Assert.That(GameplayInputGate.InputBlocked, Is.False);
            Assert.That(GameplayInputGate.MenuOpen, Is.False);
            Assert.That(GameplayInputGate.Dead, Is.False);
        }
    }
}
