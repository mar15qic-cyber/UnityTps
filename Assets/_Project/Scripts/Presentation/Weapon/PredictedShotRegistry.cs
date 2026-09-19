using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>一条本地预测开的登记（弹孔 GameObject + 预测几何）。</summary>
    public sealed class PredictedShotEntry
    {
        public long LocalIndex;
        public Vector3 PredictedPoint;
        public Vector3 PredictedNormal;
        public bool Hit;
        public uint Epoch;
        public GameObject Decal;
        public bool Consumed;
    }

    /// <summary>
    /// Owner 本地预测开火表现的登记/消费队列（ADS 审计 S2，2026-09-19）。
    /// 铁律：本地预测允许暂态偏差；服务器确认到达后——接受→按偏差纠偏持久表现（弹孔），
    /// 拒绝→撤销（弹孔不得留存）；同一 shotRequestId 的确认只消费一次（去重）；
    /// 生命代际不匹配的登记在消费时静默丢弃（死亡/复活边界的跨生命残留清理）。
    /// FIFO 消费：服务器确认按可靠通道有序到达，与本地开火顺序 1:1 对齐。
    /// 纯 C# 逻辑（GameObject 仅作载荷），EditMode 可直驱测试。
    /// </summary>
    public sealed class PredictedShotRegistry
    {
        private const int Capacity = 32;
        private readonly System.Collections.Generic.List<PredictedShotEntry> _entries = new(Capacity + 1);
        private readonly System.Collections.Generic.HashSet<ulong> _consumedConfirmIds = new();
        private long _sequence;

        /// <summary>登记一发本地预测（HandleShot 时调用；decal=本发持久表现载体，可为 null）。</summary>
        public PredictedShotEntry Register(Vector3 predictedPoint, Vector3 predictedNormal, bool hit,
            uint epoch, GameObject decal)
        {
            var entry = new PredictedShotEntry
            {
                LocalIndex = ++_sequence,
                PredictedPoint = predictedPoint,
                PredictedNormal = predictedNormal,
                Hit = hit,
                Epoch = epoch,
                Decal = decal
            };
            _entries.Add(entry);
            while (_entries.Count > Capacity)
            {
                var dropped = _entries[0];
                _entries.RemoveAt(0);
                dropped.Consumed = true; // 溢出视为已消费：确认到达时无对应登记可纠偏（表现按本地为准）
            }
            return entry;
        }

        /// <summary>取最老的未消费登记（无则 null）。</summary>
        public PredictedShotEntry PeekOldestPending()
        {
            for (int i = 0; i < _entries.Count; i++)
                if (!_entries[i].Consumed) return _entries[i];
            return null;
        }

        /// <summary>消费最老的未消费登记；epoch 不匹配（跨生命残留）时丢弃并继续找下一条。
        /// 无可消费条目返回 null。</summary>
        public PredictedShotEntry ConsumeOldestPending(uint currentEpoch)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Consumed) continue;
                entry.Consumed = true;
                if (entry.Epoch != currentEpoch) continue; // 跨生命残留：丢弃（调用方不得纠偏旧代际表现）
                return entry;
            }
            return null;
        }

        /// <summary>确认去重：首见登记并返回 false；同 id 再次到达返回 true（不得二次消费/纠偏）。</summary>
        public bool IsDuplicateConfirm(ulong shotRequestId)
        {
            if (_consumedConfirmIds.Add(shotRequestId)) return false;
            return true;
        }

        public int PendingCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _entries.Count; i++)
                    if (!_entries[i].Consumed) count++;
                return count;
            }
        }

        /// <summary>清空（生命边界/视图禁用）；不销毁 decal——调用方自行决定其生命周期。</summary>
        public void Clear() => _entries.Clear();
    }
}
