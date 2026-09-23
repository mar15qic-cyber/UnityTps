using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// ADS 审计 S2 数据锁（2026-09-19）：Owner 预测→服务器确认闭环的消费语义。
    /// 票据要求：确认必须真正被 Owner 消费、按 shot id 去重、拒绝不得留下持久弹孔、
    /// 生命代际残留不得被纠偏。本文件锁定 PredictedShotRegistry 的 FIFO/去重/代际语义
    /// 与 RemoteShotPresentation 的载荷打包（含霰弹逐弹丸终点——旧 4 标量广播的信息缺口）。
    /// </summary>
    public sealed class OwnerShotConfirmationTests
    {
        // ---------------- PredictedShotRegistry ----------------

        [Test]
        public void ConsumeOldestPending_FifoOrder()
        {
            var registry = new PredictedShotRegistry();
            registry.Register(new Vector3(1, 0, 5), Vector3.forward, true, 1u, null);
            registry.Register(new Vector3(2, 0, 5), Vector3.forward, true, 1u, null);
            registry.Register(new Vector3(3, 0, 5), Vector3.forward, true, 1u, null);

            Assert.That(registry.PendingCount, Is.EqualTo(3));
            var first = registry.ConsumeOldestPending(1u);
            Assert.That(first.PredictedPoint.x, Is.EqualTo(1f), "确认按可靠通道有序到达，必须 FIFO 对齐本地开火顺序");
            var second = registry.ConsumeOldestPending(1u);
            Assert.That(second.PredictedPoint.x, Is.EqualTo(2f));
            Assert.That(registry.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void IsDuplicateConfirm_SecondDeliveryIgnored()
        {
            var registry = new PredictedShotRegistry();
            Assert.That(registry.IsDuplicateConfirm(42ul), Is.False, "首见确认不得判重");
            Assert.That(registry.IsDuplicateConfirm(42ul), Is.True, "同 shotRequestId 重投递必须判重（不得二次纠偏/二次撤销）");
            Assert.That(registry.IsDuplicateConfirm(43ul), Is.False, "不同 id 互不影响");
        }

        [Test]
        public void ConsumeOldestPending_DropsStaleEpochEntries()
        {
            var registry = new PredictedShotRegistry();
            var stale = registry.Register(Vector3.one, Vector3.up, true, 1u, null); // 上一条命（死亡前）
            var current = registry.Register(Vector3.one * 2f, Vector3.up, true, 2u, null); // 复活后

            var consumed = registry.ConsumeOldestPending(2u);
            Assert.That(consumed, Is.SameAs(current), "跨生命残留（旧代际）不得被当前代际的确认纠偏，应被丢弃");
            Assert.That(stale.Consumed, Is.True, "旧代际登记标记为已消费（静默丢弃）");
            Assert.That(registry.ConsumeOldestPending(2u), Is.Null);
        }

        [Test]
        public void ConsumePending_UsesExactRequestId_RejectCannotConsumeAnotherShot()
        {
            var registry = new PredictedShotRegistry();
            var first = registry.Register(41u, new Vector3(1, 0, 5), Vector3.up, true, 1u, null);
            var second = registry.Register(42u, new Vector3(2, 0, 5), Vector3.up, true, 1u, null);

            Assert.That(registry.ConsumePending(99u, 1u), Is.Null, "未知/拒绝 id 不得 FIFO 吃掉另一发");
            Assert.That(first.Consumed, Is.False);
            Assert.That(registry.ConsumePending(42u, 1u), Is.SameAs(second));
            Assert.That(first.Consumed, Is.False, "后发确认只能消费同 id 的预测表现");
            Assert.That(registry.ConsumePending(42u, 1u), Is.Null, "同 id 确认不得二次消费");
        }

        [Test]
        public void Register_TrimsBeyondCapacity_OldestMarkedConsumed()
        {
            var registry = new PredictedShotRegistry();
            for (int i = 0; i < 40; i++)
                registry.Register(new Vector3(i, 0, 0), Vector3.up, true, 1u, null);
            Assert.That(registry.PendingCount, Is.LessThanOrEqualTo(32), "登记必须有界（防未确认确认风暴下无限增长）");
            var pending = registry.PeekOldestPending();
            Assert.That(pending.LocalIndex, Is.GreaterThan(8), "溢出时应淘汰最老登记");
        }

        [Test]
        public void Clear_DropsAllPendingEntries()
        {
            var registry = new PredictedShotRegistry();
            registry.Register(Vector3.one, Vector3.up, true, 1u, null);
            Assert.That(registry.IsDuplicateConfirm(1), Is.False);
            registry.Clear();
            Assert.That(registry.PendingCount, Is.EqualTo(0));
            Assert.That(registry.ConsumeOldestPending(1u), Is.Null);
            Assert.That(registry.IsDuplicateConfirm(1), Is.False, "a reused view/new session can restart shot ids");
        }

        // ---------------- RemoteShotPresentation 载荷 ----------------

        private static WeaponShot MakeShot(HitscanResult result, HitscanResult[] pellets)
        {
            return new WeaponShot(new Vector3(0, 1.6f, 0), Vector3.forward, Vector3.forward,
                result, 0f, default, 0, 0, pellets);
        }

        [Test]
        public void FromShot_SingleShot_CarriesMainResultWithoutPelletArrays()
        {
            var result = new HitscanResult(true, true, new Vector3(0, 1f, 25f), Vector3.back, null);
            var dto = RemoteShotPresentation.FromShot(MakeShot(result, null), 7u);

            Assert.That(dto.ShotRequestId, Is.EqualTo(7u));
            Assert.That(dto.PelletCount, Is.EqualTo(1));
            Assert.That(dto.PelletPoints, Is.Null);
            Assert.That(dto.FinalPoint, Is.EqualTo(new Vector3(0, 1f, 25f)).Within(1e-5f));
            Assert.That(dto.FinalHit, Is.True);
        }

        [Test]
        public void FromShot_Shotgun_CarriesEveryPelletEndpoint()
        {
            var main = new HitscanResult(true, true, Vector3.forward * 20f, Vector3.back, null);
            var pellets = new HitscanResult[9];
            for (int i = 0; i < 9; i++)
                pellets[i] = new HitscanResult(i % 2 == 0, false, new Vector3(i, 0, 20f), Vector3.back, null);
            var dto = RemoteShotPresentation.FromShot(MakeShot(main, pellets), 3u);

            Assert.That(dto.PelletCount, Is.EqualTo(9));
            Assert.That(dto.PelletPoints.Length, Is.EqualTo(9), "霰弹必须逐弹丸携带终点（旧广播丢失全部弹丸信息）");
            Assert.That(dto.PelletHits.Length, Is.EqualTo(9));
            for (int i = 0; i < 9; i++)
            {
                Assert.That(dto.PelletPoints[i], Is.EqualTo(new Vector3(i, 0, 20f)).Within(1e-5f));
                Assert.That(dto.PelletHits[i], Is.EqualTo(i % 2 == 0));
            }
            Assert.That(dto.FinalPoint, Is.EqualTo(main.Point).Within(1e-5f), "主结果（伤害权威）单独携带");
        }

        [Test]
        public void FromShot_Miss_CarriesFarPointAsFinal()
        {
            Vector3 farPoint = new Vector3(0, 1.6f, 300f);
            var result = new HitscanResult(false, false, farPoint, Vector3.zero, null);
            var dto = RemoteShotPresentation.FromShot(MakeShot(result, null), 0u);
            Assert.That(dto.FinalHit, Is.False);
            Assert.That(dto.FinalPoint, Is.EqualTo(farPoint).Within(1e-5f), "未命中远点也必须携带（观察者弹道有明确终点）");
        }
    }
}
