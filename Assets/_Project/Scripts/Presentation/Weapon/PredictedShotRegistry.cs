using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>一条本地预测开的登记（弹孔 GameObject + 预测几何）。</summary>
    public sealed class PredictedShotEntry
    {
        public long LocalIndex;
        public uint ShotRequestId;
        public Vector3 PredictedPoint;
        public Vector3 PredictedNormal;
        public bool Hit;
        public bool CharacterHit;
        public uint Epoch;
        public GameObject Decal;
        public GameObject[] PelletDecals;
        public bool[] PelletCharacters;
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
        private readonly System.Collections.Generic.Queue<ulong> _confirmOrder = new();
        private long _sequence;

        /// <summary>登记一发本地预测（HandleShot 时调用；decal=本发持久表现载体，可为 null）。</summary>
        public PredictedShotEntry Register(Vector3 predictedPoint, Vector3 predictedNormal, bool hit,
            uint epoch, GameObject decal)
            => Register(0u, predictedPoint, predictedNormal, hit, epoch, decal);

        /// <summary>登记带网络请求 id 的本地预测。纯客户端 Owner 必须由
        /// NetworkCombatAuthority.OnOwnerPredictedShot 调用，确认不能再猜 FIFO。</summary>
        public PredictedShotEntry Register(uint shotRequestId, Vector3 predictedPoint, Vector3 predictedNormal, bool hit,
            uint epoch, GameObject decal)
        {
            var entry = new PredictedShotEntry
            {
                LocalIndex = ++_sequence,
                ShotRequestId = shotRequestId,
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
                if (!dropped.Consumed) ReleaseEffects(dropped);
                dropped.Consumed = true;
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
                if (entry.Epoch != currentEpoch) { ReleaseEffects(entry); continue; }
                return entry;
            }
            return null;
        }

        /// <summary>按同一网络请求 id 消费。本地拒发/重复包/中间丢包不能误消费另一发的弹孔。</summary>
        public PredictedShotEntry ConsumePending(uint shotRequestId, uint currentEpoch)
        {
            if (shotRequestId == 0u) return null;
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Consumed || entry.ShotRequestId != shotRequestId) continue;
                entry.Consumed = true;
                if (entry.Epoch != currentEpoch) ReleaseEffects(entry);
                return entry.Epoch == currentEpoch ? entry : null;
            }
            return null;
        }

        /// <summary>确认去重：首见登记并返回 false；同 id 再次到达返回 true（不得二次消费/纠偏）。</summary>
        public bool IsDuplicateConfirm(ulong shotRequestId)
        {
            if (!_consumedConfirmIds.Add(shotRequestId)) return true;
            _confirmOrder.Enqueue(shotRequestId);
            while (_confirmOrder.Count > Capacity * 2) _consumedConfirmIds.Remove(_confirmOrder.Dequeue());
            return false;
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
        public void Clear(bool destroyPending = false)
        {
            if (destroyPending)
                foreach (var entry in _entries)
                    if (!entry.Consumed) ReleaseEffects(entry);
            _entries.Clear();
            _consumedConfirmIds.Clear();
            _confirmOrder.Clear();
        }

        private static void ReleaseEffects(PredictedShotEntry entry)
        {
            DestroyEffect(entry.Decal);
            if (entry.PelletDecals != null)
                foreach (var effect in entry.PelletDecals) DestroyEffect(effect);
            entry.Decal = null;
            entry.PelletDecals = null;
        }

        private static void DestroyEffect(GameObject effect)
        {
            if (effect == null) return;
            if (Application.isPlaying) Object.Destroy(effect);
            else Object.DestroyImmediate(effect);
        }
    }
}
