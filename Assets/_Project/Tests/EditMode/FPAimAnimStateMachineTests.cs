using Game.Presentation.Animation;
using NUnit.Framework;

namespace Game.Presentation.Tests
{
    /// <summary>
    /// Docs/18 §6 T1~T10：动画 ADS 状态机转移语义锁定。
    /// 纯 C# 决策核心——输入为"本帧事实"，输出唯一指令；执行侧（Animancer）不在测试范围。
    /// </summary>
    public sealed class FPAimAnimStateMachineTests
    {
        private static FPAimAnimInput In(
            bool aimHeld = false, float ads01 = 0f, bool busy = false,
            bool shot = false, bool dry = false,
            bool aimInFin = false, bool aimOutFin = false, bool aimFireFin = false,
            bool holster = false)
            => new FPAimAnimInput(aimHeld, ads01, busy, shot, dry, aimInFin, aimOutFin, aimFireFin, holster);

        private static FPAimAnimStateMachine Armed()
        {
            var fsm = new FPAimAnimStateMachine();
            fsm.SetHasAimClips(true);
            return fsm;
        }

        [Test]
        public void T01_HoldAim_PlaysAimInThenIdle()
        {
            var fsm = Armed();
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.1f)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
            // 过渡完成（ads01 到高位）→ 保持姿势
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true)), Is.EqualTo(FPAimAnimCommand.PlayAimIdle));
            Assert.That(fsm.IsOnAimTrack, Is.True);
        }

        [Test]
        public void T02_ReleaseAim_PlaysAimOutThenReturnsToHip()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            // 松开 → 收镜
            Assert.That(fsm.Tick(In(aimHeld: false, ads01: 0.9f)), Is.EqualTo(FPAimAnimCommand.PlayAimOut));
            // 收镜完成 → 回 Hip（无指令，Idle 由 aim_out OnEnd 恢复）
            Assert.That(fsm.Tick(In(aimHeld: false, ads01: 0f, aimOutFin: true)), Is.EqualTo(FPAimAnimCommand.None));
            Assert.That(fsm.IsOnAimTrack, Is.False);
        }

        [Test]
        public void T03_AdsFire_PlaysAimFireThenBackToIdle()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            // 满 ADS 开火
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayAimFire));
            // 开火 clip 播完 → 回保持姿势
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, aimFireFin: true)), Is.EqualTo(FPAimAnimCommand.PlayAimIdle));
        }

        [Test]
        public void T04_FireDuringAimInTransition_UsesAimFire()
        {
            // 过渡中开火（无论 ads01 阈值高低）：一律 ADS 开火——
            // 腰射 Fire 会被紧随的 PlayAimIdle 截断产生姿势跳变（Docs/18 §4.2）
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.3f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayAimFire));
        }

        [Test]
        public void T05_ReloadInterrupts_YieldsAndReAimsClean()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            // 换弹占用动作槽 → 让位
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, busy: true)), Is.EqualTo(FPAimAnimCommand.Yield));
            Assert.That(fsm.IsOnAimTrack, Is.False);
            // 换弹期间保持让位
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0f, busy: true)), Is.EqualTo(FPAimAnimCommand.Yield));
            // 换弹完成重新按住 → 从 Hip 干净重入（T10）
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.2f)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
        }

        [Test]
        public void T06_HolsterInterrupts_Yields()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, holster: true)), Is.EqualTo(FPAimAnimCommand.Yield));
            Assert.That(fsm.IsOnAimTrack, Is.False);
        }

        [Test]
        public void T07_QuickTap_SkipsAimIdle()
        {
            var fsm = Armed();
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.1f)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
            // aim_in 未完成即松开 → 直接收镜，不经过保持姿势
            Assert.That(fsm.Tick(In(aimHeld: false, ads01: 0.2f)), Is.EqualTo(FPAimAnimCommand.PlayAimOut));
            Assert.That(fsm.Tick(In(aimHeld: false, ads01: 0f, aimOutFin: true)), Is.EqualTo(FPAimAnimCommand.None));
        }

        [Test]
        public void T08_NoAimClips_AllCommandsNoneExceptHipFire()
        {
            var fsm = new FPAimAnimStateMachine();
            fsm.SetHasAimClips(false);
            // 开镜意图不产生任何 aim 指令（程序化 ADS 轨道接管）
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f)), Is.EqualTo(FPAimAnimCommand.None));
            Assert.That(fsm.IsOnAimTrack, Is.False);
            // 开火仍分流到腰射通道
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayHipFire));
        }

        [Test]
        public void T09_AdsDryFire_HoldsPose_NoCommand()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            // ADS 空仓：无 aim_dry 素材，保持贴腮姿势（无指令）
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, dry: true)), Is.EqualTo(FPAimAnimCommand.None));
            Assert.That(fsm.IsOnAimTrack, Is.True);
        }

        [Test]
        public void T10_HipFireWhilePressingAim_SameFrame()
        {
            // 同帧先开后瞄（Hip 态 shot）：腰射开火优先，下一帧再入镜
            var fsm = Armed();
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayHipFire));
            Assert.That(fsm.IsOnAimTrack, Is.False);
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.1f)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
        }

        [Test]
        public void AutoFireInAimFireState_RestartsAimFire()
        {
            // 连射：AimFire 态每发重启（指令重复下发，执行侧 FromStart）
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            fsm.Tick(In(aimHeld: true, ads01: 1f, shot: true));
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayAimFire));
            // 播完回到保持
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 1f, aimFireFin: true)), Is.EqualTo(FPAimAnimCommand.PlayAimIdle));
        }

        [Test]
        public void FireWhileAimingOut_FallsBackToHipFire()
        {
            // 收镜中开火：视同腰射
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            fsm.Tick(In(aimHeld: false, ads01: 0.6f));
            Assert.That(fsm.Tick(In(aimHeld: false, ads01: 0.3f, shot: true)), Is.EqualTo(FPAimAnimCommand.PlayHipFire));
            Assert.That(fsm.IsOnAimTrack, Is.False);
        }

        [Test]
        public void RePressDuringAimOut_ReEntersAimIn()
        {
            // 收镜途中又按住：收镜完成即重新入镜（点按抖动场景）
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.Tick(In(aimHeld: true, ads01: 1f, aimInFin: true));
            fsm.Tick(In(aimHeld: false, ads01: 0.6f));
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.4f, aimOutFin: true)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
        }

        [Test]
        public void ResetToHip_ClearsAimState()
        {
            var fsm = Armed();
            fsm.Tick(In(aimHeld: true, ads01: 0.1f));
            fsm.ResetToHip();
            Assert.That(fsm.IsOnAimTrack, Is.False);
            // 重置后按住 → 重新入镜
            Assert.That(fsm.Tick(In(aimHeld: true, ads01: 0.5f)), Is.EqualTo(FPAimAnimCommand.PlayAimIn));
        }
    }
}
