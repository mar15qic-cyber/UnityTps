using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-15 §4/§2：平滑校正台账语义。
    /// 核心回归（§2）：扣减是"逐 ACK 增量"而非"一次性消费"——同一条在途步进会被连续多张
    /// 早于它的 ACK 快照重复看见，必须保留到 ACK 追过它为止；否则同一段过时误差被重复施加，
    /// 客户端在权威位置两侧振荡（实机"前进被拽回/移动晃动"）。
    /// </summary>
    public sealed class SmoothCorrectionLedgerTests
    {
        [Test]
        public void Record_ThenConsume_SumsOnlyStepsAfterAckedTick_AndKeepsInFlight()
        {
            var ledger = new SmoothCorrectionLedger();
            Assert.That(ledger.Record(1, new Vector3(1f, 0f, 0f)), Is.True);
            Assert.That(ledger.Record(2, new Vector3(1f, 0f, 0f)), Is.True);
            Assert.That(ledger.Record(3, new Vector3(0f, 1f, 0f)), Is.True);

            // ACK 确认到 tick 2：tick 3 的步进尚未进入任何配对快照，必须由调用方扣除
            Vector3 inFlight = ledger.ConsumeInFlightAfter(2);

            Assert.That(inFlight, Is.EqualTo(new Vector3(0f, 1f, 0f)));
            Assert.That(ledger.Count, Is.EqualTo(1), "tick>ack 的在途条目必须保留（后续更老的 ACK 还要扣）");
        }

        [Test]
        public void Consume_AckedBeyondAllRecords_ReturnsZero_AndPrunesAll()
        {
            var ledger = new SmoothCorrectionLedger();
            ledger.Record(5, Vector3.right);

            Assert.That(ledger.ConsumeInFlightAfter(5), Is.EqualTo(Vector3.zero));
            Assert.That(ledger.Count, Is.EqualTo(0), "ACK 已覆盖的条目剪枝（误差已天然扣除）");
        }

        /// <summary>
        /// 审计 §2 最小反例（旧实现两次都返回 0，导致同一段误差被施加两次）：
        /// tick12 施加 +0.1m；ACK10 与 ACK11 的配对快照都早于 tick12，原始误差都是 +0.1m——
        /// 两次查账都必须返回 +0.1m 供调用方扣除，直到 ACK 追上 tick12 才可移除该条目。
        /// </summary>
        [Test]
        public void ConsecutiveAcks_OlderThanInFlightStep_KeepDeductingSameInFlight()
        {
            var ledger = new SmoothCorrectionLedger();
            ledger.Record(12, new Vector3(0.1f, 0f, 0f));

            const float rawError = 0.1f; // S_ack - R_ack：两张 ACK 的配对快照都不含 tick12 的校正
            float remaining10 = rawError - ledger.ConsumeInFlightAfter(10).x;
            float remaining11 = rawError - ledger.ConsumeInFlightAfter(11).x;

            Assert.That(remaining10, Is.EqualTo(0f).Within(1e-6f), "ACK10：在途已扣，不得再施加");
            Assert.That(remaining11, Is.EqualTo(0f).Within(1e-6f),
                "ACK11：同一条在途必须继续扣除（旧实现清账后返回 0 → 又施加 +0.1m = 拽回）");
            Assert.That(ledger.Count, Is.EqualTo(1), "ACK 追过 tick12 之前账目不得消失");
        }

        [Test]
        public void Consume_AfterAckPassesInFlightTick_PrunesAndStopsDeducting()
        {
            var ledger = new SmoothCorrectionLedger();
            ledger.Record(12, new Vector3(0.1f, 0f, 0f));

            // ACK 追上 tick12：该步进已包含进配对快照，误差天然扣除 → 不再扣、条目剪枝
            Assert.That(ledger.ConsumeInFlightAfter(12), Is.EqualTo(Vector3.zero));
            Assert.That(ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public void Record_Overflow_InvalidateAndRaiseStickyFlag()
        {
            var ledger = new SmoothCorrectionLedger();
            bool stopped = false;
            for (int i = 0; i < SmoothCorrectionLedger.MaxEntries + 1; i++)
                stopped |= !ledger.Record((uint)i + 1, Vector3.one * 0.01f);

            Assert.That(stopped, Is.True, "超限必须通知调用方硬重基（ACK 长期未推进=语义不可信）");
            Assert.That(ledger.Count, Is.EqualTo(0), "超限整体作废，不得保留半截账目");
            Assert.That(ledger.Overflowed, Is.True, "溢出必须是粘滞标记：后续 ACK 不得自行重新启用平滑");
            Assert.That(ledger.ConsumeInFlightAfter(0), Is.EqualTo(Vector3.zero));
            Assert.That(ledger.Overflowed, Is.True, "查账不清除溢出标记（只有硬重基 Invalidate 才清除）");
        }

        [Test]
        public void Invalidate_ClearsAll_AndResetsOverflowFlag()
        {
            var ledger = new SmoothCorrectionLedger();
            ledger.Record(1, Vector3.right);
            ledger.Record(2, Vector3.up);

            ledger.Invalidate();

            Assert.That(ledger.ConsumeInFlightAfter(0), Is.EqualTo(Vector3.zero), "硬校正/死亡边界作废后无在途步进");
            Assert.That(ledger.Overflowed, Is.False, "硬重基（绝对对位）后溢出标记解除");
        }

        [Test]
        public void RepeatedErrorScenario_DeductedInFlightPreventsReApplication()
        {
            // 场景还原（审计 §4）：权威偏差 E=0.2m；两个模拟步各施加 0.1m 后 ACK 到达
            // （配对快照在两步之前）——新误差 0.2m 扣除在途 0.2m 后为 0，不得再施加。
            var ledger = new SmoothCorrectionLedger();
            ledger.Record(10, new Vector3(0.1f, 0f, 0f));
            ledger.Record(11, new Vector3(0.1f, 0f, 0f));

            Vector3 rawError = new Vector3(0.2f, 0f, 0f); // S_ack - R_ack（快照不含两步）
            Vector3 remaining = rawError - ledger.ConsumeInFlightAfter(9);

            Assert.That(remaining.magnitude, Is.LessThan(1e-6f), "同段误差不得重复施加");
        }
    }
}
