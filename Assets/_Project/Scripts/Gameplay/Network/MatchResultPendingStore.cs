using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// DS 权威终局上报的本地持久补偿队列（2026-09-13，补 R2/R3 已知限制"三次重试耗尽后丢弃"）：
    /// 有界重试耗尽仍不可达时，请求落盘（JSON，临时文件+替换原子写），DS 运行期与重启后周期重放。
    /// 纪律：重放只重发落盘的原始请求对象（不从内存重建——来源校验不放松）；后端按 matchId 幂等
    /// 且对同 matchId 重复上报做同内容一致性校验（Docs/27 §7.2），Accepted/StateConflict 之外一律
    /// 保留待下轮。单写者=DS 主线程（TimeManager tick 驱动），无锁假设成立。
    /// </summary>
    public sealed class MatchResultPendingStore
    {
        private readonly string _filePath;
        private readonly List<ServerMatchResultReportRequest> _pending = new();

        public MatchResultPendingStore(string directory = null)
        {
            _filePath = Path.Combine(directory ?? Application.persistentDataPath, "match-result-pending.json");
            Load();
        }

        public int Count => _pending.Count;
        public string FilePath => _filePath;

        /// <summary>落盘一条待补偿上报；同 matchId 至多一条（与后端幂等键一致）。</summary>
        public void Append(ServerMatchResultReportRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.matchId)) return;
            if (_pending.Exists(p => p.matchId == request.matchId)) return;
            _pending.Add(request);
            SaveUnsafe();
        }

        /// <summary>重放快照（副本）：winnerTeam 空白归一为 null（A01 双保险；JsonUtility 往返会把
        /// null 序列化成空串，后端两种形态均归一接受，此处归一保证重放与首次同内容）。</summary>
        public List<ServerMatchResultReportRequest> CollectSnapshot()
        {
            var snapshot = new List<ServerMatchResultReportRequest>(_pending.Count);
            foreach (var request in _pending) snapshot.Add(Normalize(request));
            return snapshot;
        }

        /// <summary>终态（后端接受/权威冲突）后移除并落盘；返回是否确有移除。</summary>
        public bool Remove(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return false;
            int removed = _pending.RemoveAll(p => p.matchId == matchId);
            if (removed > 0) SaveUnsafe();
            return removed > 0;
        }

        private static ServerMatchResultReportRequest Normalize(ServerMatchResultReportRequest request)
        {
            var copy = new ServerMatchResultReportRequest
            {
                matchId = request.matchId,
                durationSeconds = request.durationSeconds,
                winnerTeam = string.IsNullOrWhiteSpace(request.winnerTeam) ? null : request.winnerTeam,
                players = request.players,
            };
            return copy;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                var loaded = JsonUtility.FromJson<PendingList>(json);
                if (loaded?.items == null) return;
                foreach (var item in loaded.items)
                    if (item != null && !string.IsNullOrEmpty(item.matchId))
                        _pending.Add(item);
            }
            catch (Exception exception)
            {
                // 补偿通道自身故障不得影响终局上报主链：记录后从空队列继续
                Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_PENDING_LOAD_FAILED path={_filePath}: {exception.Message}");
            }
        }

        private void SaveUnsafe()
        {
            try
            {
                var json = JsonUtility.ToJson(new PendingList { items = _pending.ToArray() }, prettyPrint: false);
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_filePath)) File.Replace(tmp, _filePath, null);
                else File.Move(tmp, _filePath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_PENDING_SAVE_FAILED path={_filePath}: {exception.Message}");
            }
        }

        [Serializable]
        private sealed class PendingList
        {
            public ServerMatchResultReportRequest[] items;
        }
    }
}
