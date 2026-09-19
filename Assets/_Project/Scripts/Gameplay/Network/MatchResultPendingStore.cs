using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// DS 权威终局上报的本地持久补偿队列（2026-09-13，补 R2/R3 已知限制"三次重试耗尽后丢弃"）：
    /// 有界重试耗尽仍不可达时，请求落盘（JSON，临时文件+替换原子写），DS 运行期与重启后周期重放。
    /// 纪律：重放只重发落盘的原始请求对象（不从内存重建——来源校验不放松）；后端按 matchId 幂等
    /// 且对同 matchId 重复上报做同内容一致性校验（Docs/27 §7.2），Accepted/StateConflict 之外一律
    /// 保留待下轮。
    /// F09/F10（2026-09-19 审计）修复：
    /// ① 构造不再触碰 Unity API（原字段初始化器 new 读 persistentDataPath，MonoBehaviour 构造期
    ///    非法 → 9 项 EditMode 持续红）——目录由调用方在主线程生命周期阶段显式给出；
    /// ② 每实例独立目录（&lt;persistent&gt;/server-results/&lt;env指纹&gt;/&lt;instanceId&gt;/）：多地图部署
    ///    下五个 DS 共享同一补偿文件会互相覆盖丢单——.tmp 竞争同理，随目录隔离消除；
    /// ③ 保存结果显式可见（LastSaveSucceeded）：磁盘满/权限失败保留内存待重试，调用方不得
    ///    打印"已落盘"；EnsureSaved() 供补偿泵每轮重试未持久化内容；
    /// ④ 共享遗留文件一次性收养（仅服务器构建+批处理）：首个启动实例移走旧文件并备份，
    ///    防止各实例重复消费同一份历史补偿。
    /// </summary>
    public sealed class MatchResultPendingStore
    {
        private readonly string _filePath;
        private readonly List<ServerMatchResultReportRequest> _pending = new();

        /// <summary>F10：路径必须显式给定（禁止 Unity API 默认值——MonoBehaviour 构造期非法）。</summary>
        public MatchResultPendingStore(string filePath)
        {
            _filePath = filePath;
            Load();
        }

        /// <summary>每实例落盘路径：&lt;root&gt;/server-results/&lt;env指纹&gt;/&lt;instanceId&gt;/match-result-pending.json。
        /// 纯逻辑（F10：可在 EditMode 直接锁定语义），不触碰 Unity API。</summary>
        public static string BuildInstanceFilePath(string persistentRoot, string backendUrl, string instanceId)
        {
            return Path.Combine(
                string.IsNullOrEmpty(persistentRoot) ? "." : persistentRoot,
                "server-results",
                SafeSegment("env-" + Fingerprint(backendUrl ?? string.Empty)),
                SafeSegment(instanceId),
                "match-result-pending.json");
        }

        /// <summary>路径段消毒（F09：实例 ID 必须经路径校验——防越目录/不稳定键）：
        /// 仅保留 [A-Za-z0-9._-]，其余替换为 '_'；消毒后与原值不同或超长时追加内容指纹保证唯一；
        /// 空值回退 "unknown"。稳定（同输入同输出），不用随机 PID。</summary>
        public static string SafeSegment(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw)
                builder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' ? character : '_');
            var safe = builder.ToString();
            if (safe is "." or "..")
                safe = "seg-" + Fingerprint(raw); // 原样保留会成为相对目录段（越出实例目录）
            if (safe.Length > 64 || safe != raw)
                safe = safe[..Math.Min(safe.Length, 48)] + "-" + Fingerprint(raw);
            return safe;
        }

        private static string Fingerprint(string value)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(16);
            for (int i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }

        /// <summary>
        /// F09 一次性迁移：多实例部署前所有 DS 共享 &lt;persistent&gt;/match-result-pending.json。
        /// 仅服务器构建（非编辑器）+ 批处理进程允许收养：本实例尚无补偿文件且遗留文件存在时，
        /// 复制进本实例目录并把遗留文件改名备份——首个启动的实例收养一次，其余实例与后续重启
        /// 不再消费同一份（避免五实例重放重复）。编辑器/非批处理一律不触碰（EditMode 可能构造
        /// 真实启动链，不得改动真实磁盘遗留文件）。收养属来源不明数据，日志要求人工核对。
        /// </summary>
        public static void AdoptLegacySharedFileIfFirstBoot(string persistentRoot, string instanceFilePath)
        {
#if UNITY_EDITOR
            return;
#else
            if (!Application.isBatchMode) return;
#endif
            try
            {
                var legacy = Path.Combine(string.IsNullOrEmpty(persistentRoot) ? "." : persistentRoot, "match-result-pending.json");
                if (!File.Exists(legacy) || File.Exists(instanceFilePath)) return;
                var directory = Path.GetDirectoryName(instanceFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var backup = legacy + ".adopted-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + ".bak";
                File.Copy(legacy, instanceFilePath, overwrite: false);
                File.Move(legacy, backup);
                Debug.LogWarning("[ServerRegistry] MATCH_RESULT_PENDING_LEGACY_ADOPTED legacy=" + legacy
                    + " -> " + instanceFilePath + " backup=" + backup
                    + "——多实例部署前共享补偿文件已移交本实例（来源不明，请人工核对后并入或丢弃）");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ServerRegistry] MATCH_RESULT_PENDING_LEGACY_ADOPT_FAILED: " + exception.Message);
            }
        }

        public int Count => _pending.Count;
        public string FilePath => _filePath;
        /// <summary>最近一次落盘是否成功（F09：失败时调用方不得声称"已落盘"；内容保留内存待重试）。</summary>
        public bool LastSaveSucceeded { get; private set; } = true;
        /// <summary>内存队列存在未持久化变更（含上一次保存失败的重试需求）。</summary>
        public bool HasUnsavedChanges { get; private set; }

        /// <summary>落盘一条待补偿上报；同 matchId 至多一条（与后端幂等键一致）。返回是否已持久化。</summary>
        public bool Append(ServerMatchResultReportRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.matchId)) return true;
            if (_pending.Exists(p => p.matchId == request.matchId)) return EnsureSaved();
            _pending.Add(request);
            return Save();
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
            if (removed > 0) Save();
            return removed > 0;
        }

        /// <summary>补偿泵每轮调用：重试上次失败的落盘（干净队列零开销）。</summary>
        public bool EnsureSaved() => Save();

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

        private bool Save()
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
                LastSaveSucceeded = true;
                HasUnsavedChanges = false;
                return true;
            }
            catch (Exception exception)
            {
                // F09：失败必须可见——调用方不得打印"已落盘"；内容保留内存（重放/下轮 Save 重试）
                LastSaveSucceeded = false;
                HasUnsavedChanges = true;
                Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_PENDING_SAVE_FAILED path={_filePath}: {exception.Message}——未持久化（保留内存待重试）");
                return false;
            }
        }

        [Serializable]
        private sealed class PendingList
        {
            public ServerMatchResultReportRequest[] items;
        }
    }
}
