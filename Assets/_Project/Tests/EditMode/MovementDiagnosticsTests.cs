using Game.Gameplay.Movement;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 移动诊断汇总（审计 2026-09-15 §5.2）：一行稳定字段、区间计数可清零、空步与帧统计口径正确。
    /// 诊断只做输出，不参与任何判定——本套件只锁格式与计数语义，便于 shell 侧按 key=value 对账。
    /// </summary>
    public sealed class MovementDiagnosticsTests
    {
        private static MovementDiagnosticsContext Context()
            => new MovementDiagnosticsContext(
                role: "owner", processId: 4321, connectionId: 1, networkObjectId: 7,
                matchId: "f6b7e25a97d94b38a9c609449c82b5aa", monotonicSeconds: 123.4, tickRate: 30,
                localTick: 120, ackedTick: 118, serverTick: 4001, queueDepth: 2,
                droppedSteps: 3, droppedStale: 4, droppedBacklog: 5, droppedFuture: 6, droppedFrozen: 0,
                roundTripMs: 23, background: false, focused: true, visualOffsetMeters: 0.041f);

        [Test]
        public void Format_EmitsEveryAccountableCounter()
        {
            var diag = new MovementDiagnostics();
            diag.NoteFrame(0.0083, 1d / 30d);
            diag.NoteFrame(0.1000, 1d / 30d); // 长帧（>2×固定步长 = 66.7ms）
            diag.NoteGenerated(4);
            diag.NoteSubmit(4);
            diag.NoteServerTotals(120, 118);
            diag.NoteServerConsumed(0); // 空步
            diag.NoteSnap(0.9f);
            diag.NoteSmooth(0.2f, 0.2f, 0.3f);
            diag.NoteSmoothApplied(0.05f);

            var line = diag.Format(Context());

            foreach (var key in new[]
            {
                "role=owner", "pid=4321", "conn=1", "obj=7",
                "match=f6b7e25a97d94b38a9c609449c82b5aa", "t=123.4", "tickRate=30",
                "gen=4", "sub=1/4", "recv=120", "cons=118", "empty=1", "emptyTotal=1",
                "local=120", "ack=118", "stTick=4001", "qdepth=2",
                "dropSteps=3", "dropStale=4", "dropBacklog=5", "dropFuture=6", "dropFrozen=0",
                "rtt=23ms", "long=1", "bg=0", "focus=1",
                "snaps=1", "smooth=1", "smoothTotal=1", "applied=0.050m", "pend=0.200m",
                "err=0.200m", "errRaw=0.300m", "visOff=0.041m",
            })
                Assert.That(line, Does.Contain(key), $"诊断行缺少字段 {key}：{line}");
            Assert.That(line, Does.StartWith("[MoveDiag] "), "诊断行前缀固定，便于 grep");
        }

        [Test]
        public void ResetInterval_ClearsIntervalCounters_KeepsInstantaneousState()
        {
            var diag = new MovementDiagnostics();
            diag.NoteFrame(0.016, 1d / 30d);
            diag.NoteGenerated(3);
            diag.NoteSubmit(2);
            diag.NoteServerConsumed(2);
            diag.NoteSnap(0.9f);
            diag.NoteSmooth(0.2f, 0.2f, 0.3f);
            diag.NoteSmoothApplied(0.05f);

            diag.ResetInterval();

            Assert.That(diag.GeneratedTicks, Is.EqualTo(0));
            Assert.That(diag.SubmitBatches, Is.EqualTo(0));
            Assert.That(diag.SubmittedCommands, Is.EqualTo(0));
            Assert.That(diag.Snaps, Is.EqualTo(0));
            Assert.That(diag.Smooths, Is.EqualTo(0));
            Assert.That(diag.AppliedMeters, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(diag.LongFrames, Is.EqualTo(0));
            Assert.That(diag.HasData, Is.False, "无数据区间不输出空行");
            // 瞬时值跨区间保留（诊断读的是"当前状态"，不是区间累计）
            Assert.That(diag.PendingMeters, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(diag.LastErrorMeters, Is.EqualTo(0.2f).Within(1e-6f));
            // G2（审计 2026-09-17）：会话累计口径跨区间保留（smooth=/empty= 是区间口径，不可跨段比）
            Assert.That(diag.SmoothsTotal, Is.EqualTo(1), "会话累计不随区间清零");
        }

        /// <summary>G2（审计 2026-09-17）：会话累计口径与区间口径分离——`empty=` 只代表该 2s 采样
        /// 区间，整局是否空转/外推必须看 `emptyTotal=`（上一轮审计曾因区间口径误读"整局未外推"）。</summary>
        [Test]
        public void SessionTotals_AccumulateAcrossIntervals_WhileIntervalCountersReset()
        {
            var diag = new MovementDiagnostics();
            diag.NoteServerConsumed(0); // 第 1 区间 1 空步
            diag.ResetInterval();
            Assert.That(diag.ServerEmptySteps, Is.EqualTo(0), "区间计数已清");
            Assert.That(diag.ServerEmptyStepsTotal, Is.EqualTo(1), "会话累计保留");

            diag.NoteServerConsumed(0);
            diag.NoteServerConsumed(0);
            diag.ResetInterval();
            Assert.That(diag.ServerEmptySteps, Is.EqualTo(0));
            Assert.That(diag.ServerEmptyStepsTotal, Is.EqualTo(3), "跨区间单调累加");
        }

        [Test]
        public void NoteServerConsumed_ZeroIsCountedAsEmptyStep()
        {
            var diag = new MovementDiagnostics();
            diag.NoteServerConsumed(0);
            diag.NoteServerConsumed(0);
            diag.NoteServerConsumed(1);

            Assert.That(diag.ServerEmptySteps, Is.EqualTo(2), "空步=服务器无输入的 tick 数（2026-09-17 D2 起保持位姿不模拟）");
            Assert.That(diag.ServerConsumed, Is.EqualTo(1));
        }
    }
}
