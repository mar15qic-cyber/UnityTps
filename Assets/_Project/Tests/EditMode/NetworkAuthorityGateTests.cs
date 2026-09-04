using Game.Gameplay.Health;
using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/23 P0 权威战斗收口——纯逻辑判定锁定（G3 伤害权威门 + G2a 俯仰命令字段）。
    /// 说明：弹药 SyncVar 语义（服务器写/Owner 读）依赖 FishNet 运行时网络模拟，
    /// EditMode 无法真实构建（测试程序集亦无 FishNet 引用），按票据约定跳过，
    /// 由 Host 单实例实机冒烟覆盖（见 P0 用户手动清单）。
    /// </summary>
    public sealed class NetworkAuthorityGateTests
    {
        // ---- G3：DamageableTarget.ShouldApplyDamage 三分支 ----

        [Test]
        public void G3_Offline_AppliesDamageLocally()
        {
            // 网络未启动（离线）：本地结算照旧
            Assert.That(DamageableTarget.ShouldApplyDamage(networkActive: false, isServer: false), Is.True);
        }

        [Test]
        public void G3_OnlineServer_AppliesDamage()
        {
            // 在线且是服务器（Host 自身）：服务器结算真相
            Assert.That(DamageableTarget.ShouldApplyDamage(networkActive: true, isServer: true), Is.True);
        }

        [Test]
        public void G3_OnlinePureClient_DoesNotApplyDamage()
        {
            // 在线非服务器（纯客户端）：本地预测射线只表现，不扣血
            Assert.That(DamageableTarget.ShouldApplyDamage(networkActive: true, isServer: false), Is.False);
        }

        // ---- G2a：MovementCommand 俯仰字段 ----

        [Test]
        public void G2a_PitchConstructor_CarriesAllFields()
        {
            // Move 取幅值恰为 1 的输入（构造器会 ClampMagnitude，夹紧语义由下一条用例单独锁定）
            var cmd = new MovementCommand(
                new Vector2(0.6f, 0.8f), sprint: true, jump: true,
                yawDelta: 0.3f, pitchDelta: 0.2f, tick: 42u);

            Assert.That(cmd.Move, Is.EqualTo(new Vector2(0.6f, 0.8f)));
            Assert.That(cmd.Sprint, Is.True);
            Assert.That(cmd.Jump, Is.True);
            Assert.That(cmd.YawDelta, Is.EqualTo(0.3f));
            Assert.That(cmd.PitchDelta, Is.EqualTo(0.2f));
            Assert.That(cmd.Tick, Is.EqualTo(42u));
        }

        [Test]
        public void G2a_PitchConstructor_ClampsMoveMagnitude()
        {
            // 既有语义保持：Move 归一化夹紧到幅值 1
            var cmd = new MovementCommand(
                new Vector2(10f, 0f), sprint: false, jump: false,
                yawDelta: 0f, pitchDelta: -1.5f, tick: 0u);

            Assert.That(cmd.Move.magnitude, Is.EqualTo(1f).Within(0.0001f));
            // 俯仰为带符号增量（抬头为正、低头为负），不做夹紧（服务器侧统一夹紧 ±89°）
            Assert.That(cmd.PitchDelta, Is.EqualTo(-1.5f));
        }

        [Test]
        public void G2a_LegacyConstructor_DefaultsPitchToZero()
        {
            // 兼容构造器（Locomotor 离线路径零改动）：PitchDelta 默认 0
            var cmd = new MovementCommand(new Vector2(0f, 1f), sprint: false, jump: false, yawDelta: 0.1f, tick: 7u);

            Assert.That(cmd.PitchDelta, Is.EqualTo(0f));
            Assert.That(cmd.YawDelta, Is.EqualTo(0.1f));
            Assert.That(cmd.Tick, Is.EqualTo(7u));
        }
    }
}
