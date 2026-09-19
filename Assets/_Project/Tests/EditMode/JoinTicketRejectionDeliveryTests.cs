using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Codex 终审 F1 修复（2026-09-06，OnPostTick 冲刷信号方案）：拒绝结果投递窗口语义锁定——纯逻辑全同步可测。
    /// 查证+判别实验结论（见 PendingRejectionTracker 头注）：广播无发送完成回调；tick 循环内 OnPostTick
    /// 紧邻 TryIterateData(false)（出队冲刷）；仅靠 timer 窗口不保证送达（合并缓冲区滞留被断开截断）。
    /// 收口二元条件：冲刷计数推进（广播已过冲刷点）或 250ms 上限到（fail closed）。
    /// 覆盖：① CollectDue 一次性收口（冲刷推进即收口、二次 Collect 拿不到同一项）；
    /// ② 未冲刷未超时不收口；③ Cancel 断开竞态（客户端主动断开→待定作废，不触发结果回调）；
    /// ④ 重复 Mark 保持最早；⑤ 多连接只收满足条件者；⑥ 上限路径与常量锁定。
    /// MonoBehaviour 胶水（OnPostTick 计数接线/Stopped Cancel/Update 收口）按项目既有约定由真实
    /// Development Client 联测覆盖（三码接收证据，见 Day1 报告 §11）。
    /// </summary>
    public sealed class JoinTicketRejectionDeliveryTests
    {
        // ---- ① 冲刷推进即收口 + 一次性收口 ----

        [Test]
        public void CollectDue_FlushAdvanced_ReturnsConnectionExactlyOnce()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(7, 100.5, flushCountAtMark: 10);

            CollectionAssert.IsEmpty(tracker.CollectDue(currentFlushCount: 10, nowSeconds: 1.0), "未冲刷未超限不得收口");
            CollectionAssert.AreEquivalent(new[] { 7 }, tracker.CollectDue(currentFlushCount: 11, nowSeconds: 1.2), "冲刷推进立即收口");
            Assert.That(tracker.Contains(7), Is.False, "收口即移除");
            CollectionAssert.IsEmpty(tracker.CollectDue(currentFlushCount: 99, nowSeconds: 999.0), "一次性收口：同一连接绝不出现第二次");
        }

        [Test]
        public void CollectDue_MultipleConnections_ReturnsOnlySatisfiedEntries()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(1, 100.0, flushCountAtMark: 5);
            tracker.Mark(2, 200.0, flushCountAtMark: 50);

            CollectionAssert.AreEquivalent(new[] { 1 }, tracker.CollectDue(currentFlushCount: 5, nowSeconds: 150.0), "conn1 走上限路径");
            Assert.That(tracker.Count, Is.EqualTo(1));
            CollectionAssert.AreEquivalent(new[] { 2 }, tracker.CollectDue(currentFlushCount: 51, nowSeconds: 150.0), "conn2 走冲刷路径");
            Assert.That(tracker.Count, Is.EqualTo(0));
        }

        // ---- ② 断开竞态：客户端主动断开 → 待定作废，绝不收口 ----

        [Test]
        public void Cancel_DiscardsPending_NeverFinalizes()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(9, 50.0, flushCountAtMark: 1);
            tracker.Cancel(9);

            CollectionAssert.IsEmpty(tracker.CollectDue(currentFlushCount: 99, nowSeconds: 100.0), "断开竞态：待定已作废，不触发结果回调");
            tracker.Cancel(9); // 幂等
            Assert.That(tracker.Count, Is.EqualTo(0));
        }

        [Test]
        public void MarkAfterCancel_RegistersFreshPending()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(9, 50.0, flushCountAtMark: 1);
            tracker.Cancel(9);
            tracker.Mark(9, 80.0, flushCountAtMark: 2);

            CollectionAssert.IsEmpty(tracker.CollectDue(currentFlushCount: 2, nowSeconds: 70.0), "新登记未满足前不收口");
            CollectionAssert.AreEquivalent(new[] { 9 }, tracker.CollectDue(currentFlushCount: 3, nowSeconds: 80.5), "新登记按新条件收口");
        }

        // ---- ③ 重复登记：保持最早 ----

        [Test]
        public void DuplicateMark_KeepsEarliestDeadline()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(3, 105.0, flushCountAtMark: 7);
            tracker.Mark(3, 101.0, flushCountAtMark: 7); // 更早的截止必须胜出

            CollectionAssert.AreEquivalent(new[] { 3 }, tracker.CollectDue(currentFlushCount: 7, nowSeconds: 101.0), "最早截止生效——更快 fail closed");
            Assert.That(tracker.Count, Is.EqualTo(0));
        }

        // ---- ④ 上限路径与常量锁定 ----

        [Test]
        public void DeliveryWindowCap_MatchesCodexCeiling()
        {
            Assert.That(JoinTicketAuthenticator.RejectionDeliveryCapSeconds, Is.LessThanOrEqualTo(0.250),
                "上限常量不得放宽 Codex 终审的 250ms");
        }

        [Test]
        public void CollectDue_CapPath_FiresExactlyAtDeadline()
        {
            var tracker = new PendingRejectionTracker();
            tracker.Mark(4, 12.5, flushCountAtMark: 100); // 计数冻结（模拟 Update 停摆）

            CollectionAssert.IsEmpty(tracker.CollectDue(currentFlushCount: 100, nowSeconds: 12.4), "上限前不收口");
            CollectionAssert.AreEquivalent(new[] { 4 }, tracker.CollectDue(currentFlushCount: 100, nowSeconds: 12.5), "上限到点 fail closed 收口（即使从未冲刷）");
        }
    }
}
