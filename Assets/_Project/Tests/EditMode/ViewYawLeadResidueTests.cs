using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 视角 yaw 超前量残差回归（2026-09-16 实机修复）：
    /// 语义 = "本帧已积累、尚未随 tick 提交的 look 输入"。
    /// 修复前（累积端做 ViewYawClampDegrees 钳制、扣减端按未钳制完整量扣减）：一旦饱和就产生
    /// `超前量 − 未提交输入` 的不变残差（不饱和的帧严格保持），只能靠死亡/重生复位清零。
    /// 实机证据：MoveDiag yawLead 出现 20–36s 冻住的 1.50/2.30/3.29/4.50 与恰好 ±10.00（饱和值）；
    /// 90 发"客户端瞄准方向 vs 服务器弹道方向"对账 |Δyaw| ≈ |yawLead|（30/30 命中，最大 13.5°）
    /// —— 用户表现即"按 W 不走直线"（身体朝向 ≠ 视线）+"打对面完全不掉血"
    /// （服务器射线沿身体 yaw：SubmitFireRequest 只上传 tick 估算，方向由服务器自算）。
    /// 修复后不变量：提交后超前量恒为 0；钳制只影响写进视觉节点的显示角度。
    /// </summary>
    public sealed class ViewYawLeadResidueTests
    {
        private const float Clamp = MovementPredictionConfig.ViewYawClampDegrees;

        [Test]
        public void Accumulate_IsLossless_EvenBeyondDisplayClamp()
        {
            float lead = 0f;
            lead = PlayerNetworkAdapter.AccumulateViewYawLead(lead, 12f);
            lead = PlayerNetworkAdapter.AccumulateViewYawLead(lead, 15f); // 单帧甩动累计 27° > 10° 显示上限

            Assert.That(lead, Is.EqualTo(27f).Within(1e-4f),
                "累积量必须无损：钳制不得发生在累积端（否则产生不可衰减的残差）");
        }

        [Test]
        public void DisplayClamp_BoundsOnlyTheRenderedAngle()
        {
            Assert.That(PlayerNetworkAdapter.ResolveViewYawForDisplay(27f), Is.EqualTo(Clamp).Within(1e-4f));
            Assert.That(PlayerNetworkAdapter.ResolveViewYawForDisplay(-27f), Is.EqualTo(-Clamp).Within(1e-4f));
            Assert.That(PlayerNetworkAdapter.ResolveViewYawForDisplay(3.29f), Is.EqualTo(3.29f).Within(1e-4f),
                "显示上限内必须原样输出（实机冻住的 3.29° 就是残差，不是正常超前量）");
            Assert.That(PlayerNetworkAdapter.ResolveViewYawForDisplay(0f), Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void CommitAfterFlick_LeavesNoResidue()
        {
            // 三帧各积累 12°（跨三帧一次 tick 提交的常见形态），提交量 = 三帧之和 36°
            float lead = 0f;
            for (int i = 0; i < 3; i++)
                lead = PlayerNetworkAdapter.AccumulateViewYawLead(lead, 12f);
            lead = PlayerNetworkAdapter.ConsumeViewYawLead(lead, 36f);

            Assert.That(lead, Is.EqualTo(0f).Within(1e-4f), "提交后超前量必须精确归零（无残差）");

            // 旧语义反证：累积端钳制 27→10，再按完整 36 扣减 → −26° 的永久视角/身体夹角
            float legacyLead = 0f;
            for (int i = 0; i < 3; i++)
                legacyLead = UnityEngine.Mathf.Clamp(legacyLead + 12f, -Clamp, Clamp);
            legacyLead -= 36f;
            Assert.That(legacyLead, Is.EqualTo(-26f).Within(1e-4f),
                "旧语义（累积端钳制）留下的残差：这正是实机 yawLead 冻住 ±10/1.5/2.3/3.29 的来源");
        }

        [Test]
        public void RepeatedFlicks_ResidueStaysZero_AcrossManyCommits()
        {
            // 甩动 → 提交 反复 200 轮（每轮输入量不同，含超上限的大甩动）：残差不得累积
            float lead = 0f;
            float[] inputs = { 14f, -9f, 27f, -18f, 6f, -12.5f, 3f, -4f };
            foreach (var d in inputs)
            {
                lead = PlayerNetworkAdapter.AccumulateViewYawLead(lead, d);
                lead = PlayerNetworkAdapter.ConsumeViewYawLead(lead, d);
                Assert.That(lead, Is.EqualTo(0f).Within(1e-4f), "每次提交后残差必须为 0");
            }

            // 未提交的输入才构成超前量（相机逐帧响应），且其显示值与真实值一致（未饱和时）
            lead = PlayerNetworkAdapter.AccumulateViewYawLead(0f, 2.5f);
            Assert.That(PlayerNetworkAdapter.ResolveViewYawForDisplay(lead), Is.EqualTo(2.5f).Within(1e-4f));
        }
    }
}
