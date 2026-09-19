using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Timing;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// Day3 Phase 2 参数：默认 200ms 回溯窗口（可运行时改）。
    /// </summary>
    public static class LagCompensationConfig
    {
        /// <summary>回溯窗口上限（毫秒）。超出窗口的命中请求裁剪到最老可用快照。</summary>
        public const int MaxRewindMs = 200;
        /// <summary>窗口外历史保留余量（tick 数），防边界抖动导致空窗。</summary>
        public const int WindowMarginTicks = 4;
    }

    /// <summary>
    /// 纯策略（可离线测试）：窗口 tick 换算与回溯目标裁剪。
    /// 裁剪规则：目标早于最老可用快照 → 裁剪到最老（禁止无限补偿高延迟玩家）；
    /// 目标超前于当前 → 裁剪到当前；窗口内原样放行。
    /// </summary>
    public static class LagCompensationPolicy
    {
        private const int WindowTicksFloor = 2;

        /// <summary>窗口换算成 tick 数（含余量，下限 WindowTicksFloor）。</summary>
        public static int ComputeWindowTicks(int maxRewindMs, int tickRate)
        {
            if (maxRewindMs <= 0 || tickRate <= 0) return WindowTicksFloor;
            int ticks = Mathf.CeilToInt(maxRewindMs / 1000f / (1f / tickRate));
            return Mathf.Max(WindowTicksFloor, ticks + LagCompensationConfig.WindowMarginTicks);
        }

        public static uint ClampTargetTick(long targetTick, long oldestAvailableTick, long currentTick)
        {
            if (oldestAvailableTick <= 0) return 0; // 无历史：由调用方拒绝回溯
            if (targetTick < oldestAvailableTick) return (uint)oldestAvailableTick;
            if (targetTick > currentTick) return (uint)currentTick;
            return (uint)targetTick;
        }
    }

    /// <summary>
    /// 服务器开火回溯上下文（Phase 2）：把"本次结算实际使用的回溯 tick"从 ServerFireRequest
    /// 经 WeaponController.TryFire 传到 ResolveHitscanTwoStage，供命中判定按射击时刻快照比对
    /// 生命代际/无敌状态。default = 无回溯语境（本地预测/离线/未回溯）——判定跳过代际闸。
    /// </summary>
    public readonly struct LagCompRewindContext
    {
        public readonly uint UsedTick;
        public readonly bool Rewound;
        /// <summary>客户端请求 tick（Phase 6 诊断：与 UsedTick 不同 = 发生了越窗裁剪）。</summary>
        public readonly uint RequestedTick;

        /// <summary>是否发生裁剪（used != requested；仅 Rewound 时有意义）。</summary>
        public bool WasClamped => Rewound && UsedTick != RequestedTick;

        public LagCompRewindContext(uint usedTick, bool rewound, uint requestedTick = 0)
        {
            UsedTick = usedTick;
            Rewound = rewound;
            RequestedTick = requestedTick;
        }
    }

    /// <summary>
    /// 服务器 hitbox 历史 + 命中回滚（Day3 Phase 2，运行时挂载零资产改动）：
    /// ① 只在服务器记录：每个服务器 tick 记录各联网玩家 root 下全部 hitbox 碰撞体的位姿，按服务器 tick 索引；
    ///    历史数组循环复用（稳态零分配）。
    /// ② 回滚仅服务 hitscan 命中判定：TryBeginRewind 先保存当前位姿再把碰撞体移到历史位姿
    ///    （目标越窗按 LagCompensationPolicy 裁剪：过老裁到最老、超前裁到当前）；
    ///    TryFire 同步判定完成后 EndRewind 立即恢复当前位姿。
    /// ③ 伤害/击杀/比分/终局不变——仍走既有服务器权威链（CombatResolver/DamageableTarget/MatchLifecycle）。
    /// ④ authored 假人/未认证对象不注册（PlayerNetworkAdapter 只注册已生成的联网玩家），
    ///    现有零正式计分规则不受影响。
    /// 功能开关：ServerLagCompensation.Enabled（默认开；异常时可关，回退为即时位姿判定，实现保持可编译）。
    /// </summary>
    public sealed class ServerLagCompensation : MonoBehaviour
    {
        /// <summary>功能开关（默认开启；实机异常时可整体关闭）。</summary>
        public static bool Enabled { get; set; } = true;

        public static ServerLagCompensation Instance { get; private set; }

        private TimeManager _timeManager;
        private readonly List<PlayerHistory> _players = new();
        private readonly List<SavedPose> _savedPoses = new();
        private bool _rewindActive;
        // 审计 2026-09-15 §5.2：两遍回溯的临时参与集（玩家+其目标 tick 快照）与保存去重集
        private readonly List<(PlayerHistory player, Entry entry)> _rewindScratch = new();
        private readonly HashSet<Transform> _savedTransforms = new();
        // Day3 验收诊断：回滚计数与裁剪证据（trimmed=true=越窗裁剪；used 恒 ≥ oldest=不回溯到最老帧以外）
        private long _rewindCount;

        private sealed class PlayerHistory
        {
            public Transform Root;
            public Collider[] Colliders;
            /// <summary>该玩家的 NetworkCombatAuthority（注册时缓存；快照携带其生命代际/无敌态）。可为 null（测试/无 authority）。</summary>
            public NetworkCombatAuthority Authority;
            public readonly List<Entry> Entries = new();
            public readonly Stack<Entry> Recycle = new();
        }

        private readonly struct Entry
        {
            public readonly uint Tick;
            public readonly Vector3[] Positions;
            public readonly Quaternion[] Rotations;
            /// <summary>采集时刻该玩家的生命代际（Phase 2：跨生命回溯判定的比对基准）。</summary>
            public readonly ulong LifeGeneration;
            /// <summary>采集时刻该玩家是否处于出生保护（Phase 2：按"射击对应历史 tick"判无敌）。</summary>
            public readonly bool Invincible;

            public Entry(uint tick, Vector3[] positions, Quaternion[] rotations, ulong lifeGeneration, bool invincible)
            {
                Tick = tick;
                Positions = positions;
                Rotations = rotations;
                LifeGeneration = lifeGeneration;
                Invincible = invincible;
            }
        }

        private readonly struct SavedPose
        {
            public readonly Transform Transform;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;

            public SavedPose(Transform transform, Vector3 position, Quaternion rotation)
            {
                Transform = transform;
                Position = position;
                Rotation = rotation;
            }
        }

        private void Awake()
        {
            Instance = this;
            _timeManager = InstanceFinder.TimeManager;
            if (_timeManager != null) _timeManager.OnTick += CaptureCurrentTick;
        }

        private void OnDestroy()
        {
            if (_timeManager != null)
            {
                _timeManager.OnTick -= CaptureCurrentTick;
                _timeManager = null;
            }
            if (Instance == this) Instance = null;
        }

        private void CaptureCurrentTick()
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null || !nm.IsServerStarted) return;
            uint tick = _timeManager != null ? _timeManager.Tick : 0;
            Capture(tick);
        }

        /// <summary>注册一个联网玩家（服务器侧；PlayerNetworkAdapter.OnStartServer 调用，幂等）。
        /// authority：该玩家的 NetworkCombatAuthority（快照按采集 tick 记录其生命代际与无敌态；
        /// null = 无 authority——快照元数据恒 0/false，判定退化为既有行为）。</summary>
        public void RegisterPlayer(Transform root, Collider[] colliders, NetworkCombatAuthority authority = null)
        {
            if (root == null || colliders == null || colliders.Length == 0) return;
            foreach (var p in _players)
                if (p.Root == root) return;
            _players.Add(new PlayerHistory { Root = root, Colliders = colliders, Authority = authority });
        }

        public void UnregisterPlayer(Transform root)
        {
            for (int i = _players.Count - 1; i >= 0; i--)
                if (_players[i].Root == root)
                {
                    _players.RemoveAt(i);
                    return;
                }
        }

        public int RegisteredCount => _players.Count;

        /// <summary>按指定服务器 tick 记录一帧历史（tick 循环的可测入口；tick=0 视为无效）。</summary>
        public void Capture(uint tick)
        {
            if (tick == 0 || _players.Count == 0) return;
            int windowTicks = WindowTicks();
            long oldestAllowed = (long)tick - windowTicks;

            for (int p = 0; p < _players.Count; p++)
            {
                var player = _players[p];
                if (player.Root == null) continue;
                int n = player.Colliders.Length;

                Vector3[] positions;
                Quaternion[] rotations;
                if (player.Recycle.Count > 0)
                {
                    var recycled = player.Recycle.Pop();
                    positions = recycled.Positions;
                    rotations = recycled.Rotations;
                }
                else
                {
                    positions = new Vector3[n];
                    rotations = new Quaternion[n];
                }

                for (int i = 0; i < n; i++)
                {
                    var collider = player.Colliders[i];
                    if (collider == null) continue;
                    positions[i] = collider.transform.position;
                    rotations[i] = collider.transform.rotation;
                }
                // Phase 2：快照携带采集时刻的生命代际与无敌态（按本 tick 语义判定保护窗口）
                ulong lifeGeneration = player.Authority != null ? player.Authority.LifeGeneration : 0ul;
                bool invincible = player.Authority != null && player.Authority.IsInvincibleAt(tick);
                player.Entries.Add(new Entry(tick, positions, rotations, lifeGeneration, invincible));

                // 裁剪窗口外历史（含余量）；被裁剪条目的数组回收复用
                while (player.Entries.Count > 0 && player.Entries[0].Tick < oldestAllowed)
                {
                    player.Recycle.Push(player.Entries[0]);
                    player.Entries.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// 开始回滚：保存当前位姿后，把注册玩家的 hitbox 移到 targetTick 的历史位姿。
        /// 目标越窗（过老/超前）按策略裁剪；无历史/开关关闭/回滚进行中返回 false（服务器权威拒绝补偿）。
        /// 调用方必须 try/finally 配对 EndRewind。
        /// 审计 2026-09-15 §5.2 两项硬性修正：
        /// ① 两遍执行——先【全量保存】相关 Transform 的回滚前位姿（父子共存时"边保存边回写"会把
        ///    子 hitbox 的保存值变成已被父级回写污染的世界位姿，EndRewind 恢复后子级局部位姿漂移），
        ///    再统一回写；恢复值恒为回滚前快照，恢复顺序无关（全为绝对位姿）。
        /// ② excludeRoot（射手根）不参与回溯——射手以服务器当前权威姿态开火，其 AimOrigin/AimDirection
        ///    不得随历史位姿移动；目标仍按请求 tick 回溯。
        /// </summary>
        public bool TryBeginRewind(uint targetTick, Transform excludeRoot = null)
        {
            return TryBeginRewind(targetTick, excludeRoot, out _);
        }

        /// <summary>带 usedTick 出参的重载（Phase 2）：调用方把"实际回溯 tick"随结算上下文传给
        /// CombatResolver，供命中判定按射击时刻快照比对生命代际/无敌状态。</summary>
        public bool TryBeginRewind(uint targetTick, Transform excludeRoot, out uint usedTick)
        {
            usedTick = 0;
            if (!Enabled || _rewindActive || _players.Count == 0) return false;
            long oldest = OldestAvailableTick();
            if (oldest <= 0) return false;
            uint clamped = LagCompensationPolicy.ClampTargetTick(targetTick, oldest, CurrentTick());
            if (clamped == 0) return false;
            usedTick = clamped;

            _savedPoses.Clear();
            _rewindScratch.Clear();
            for (int p = 0; p < _players.Count; p++)
            {
                var player = _players[p];
                if (player.Root == null) continue;
                if (excludeRoot != null && player.Root == excludeRoot) continue;
                Entry? entry = ResolveEntry(player, clamped);
                if (!entry.HasValue) continue;
                _rewindScratch.Add((player, entry.Value));
            }

            // PASS 1：全量保存回滚前位姿（去重——同 Transform 多碰撞体只保存一次）
            _savedTransforms.Clear();
            for (int i = 0; i < _rewindScratch.Count; i++)
            {
                var colliders = _rewindScratch[i].player.Colliders;
                for (int c = 0; c < colliders.Length; c++)
                {
                    var collider = colliders[c];
                    if (collider == null) continue;
                    var t = collider.transform;
                    if (!_savedTransforms.Add(t)) continue;
                    _savedPoses.Add(new SavedPose(t, t.position, t.rotation));
                }
            }

            _rewindCount++;
            bool trimmed = clamped != targetTick;
            if (trimmed || _rewindCount == 1 || _rewindCount % 30 == 0)
                Debug.Log($"[Day3][LagComp] rewind req={targetTick} used={clamped} oldest={oldest} current={CurrentTick()} trimmed={trimmed} count={_rewindCount} players={_rewindScratch.Count} poses={_savedPoses.Count}");

            // PASS 2：统一回写历史位姿（CharacterController 直接写 transform 仅用于查询期判定）
            for (int i = 0; i < _rewindScratch.Count; i++)
            {
                var player = _rewindScratch[i].player;
                var e = _rewindScratch[i].entry;
                for (int c = 0; c < player.Colliders.Length; c++)
                {
                    var collider = player.Colliders[c];
                    if (collider == null) continue;
                    collider.transform.SetPositionAndRotation(e.Positions[c], e.Rotations[c]);
                }
            }
            Physics.SyncTransforms();
            _rewindActive = true;
            return true;
        }

        /// <summary>恢复全部 hitbox 到回滚前位姿（判定后立即调用；幂等）。</summary>
        public void EndRewind()
        {
            if (!_rewindActive) return;
            _rewindActive = false;
            for (int i = 0; i < _savedPoses.Count; i++)
            {
                var saved = _savedPoses[i];
                if (saved.Transform == null) continue;
                saved.Transform.SetPositionAndRotation(saved.Position, saved.Rotation);
            }
            _savedPoses.Clear();
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Phase 2：查询目标玩家在指定回溯 tick 快照里的生命代际与无敌态。
        /// 命中判定据此拒绝"旧生命回溯伤害新生命"（代际不符）与"射击时刻目标处于出生保护"。
        /// 语义：取该玩家 ≤ rewindTick 的最近快照（与 TryBeginRewind 的 ResolveEntry 同口径——
        /// 两侧必须同 tick 取语境，配对比较才成立）。
        /// 无该玩家/无历史快照返回 false（调用方不拦截，维持既有即时判定语义）。
        /// </summary>
        public bool TryGetRewindContext(Transform targetRoot, uint rewindTick, out ulong lifeGeneration, out bool invincible)
        {
            lifeGeneration = 0;
            invincible = false;
            if (targetRoot == null) return false;
            for (int p = 0; p < _players.Count; p++)
            {
                var player = _players[p];
                if (player.Root != targetRoot) continue;
                Entry? entry = ResolveEntry(player, rewindTick);
                if (!entry.HasValue) return false;
                lifeGeneration = entry.Value.LifeGeneration;
                invincible = entry.Value.Invincible;
                return true;
            }
            return false;
        }

        public int RewindWindowTicks => WindowTicks();

        // ---- 内部 ----

        private int WindowTicks()
        {
            int tickRate = _timeManager != null ? (int)_timeManager.TickRate : 30;
            return LagCompensationPolicy.ComputeWindowTicks(LagCompensationConfig.MaxRewindMs, tickRate);
        }

        /// <summary>当前 tick：TimeManager 不可用时回退到最新历史（离线测试/极端时序）。</summary>
        private long CurrentTick()
        {
            long tick = _timeManager != null ? _timeManager.Tick : 0;
            long latest = 0;
            for (int p = 0; p < _players.Count; p++)
            {
                var entries = _players[p].Entries;
                if (entries.Count > 0 && entries[entries.Count - 1].Tick > latest)
                    latest = entries[entries.Count - 1].Tick;
            }
            return tick > latest ? tick : latest;
        }

        private long OldestAvailableTick()
        {
            long oldest = long.MaxValue;
            for (int p = 0; p < _players.Count; p++)
                if (_players[p].Entries.Count > 0 && _players[p].Entries[0].Tick < oldest)
                    oldest = _players[p].Entries[0].Tick;
            return oldest == long.MaxValue ? 0 : oldest;
        }

        /// <summary>取该玩家 ≤ 目标 tick 的最近快照（缺口容忍，等价于按最老可用裁剪）。</summary>
        private static Entry? ResolveEntry(PlayerHistory player, uint targetTick)
        {
            for (int i = player.Entries.Count - 1; i >= 0; i--)
                if (player.Entries[i].Tick <= targetTick)
                    return player.Entries[i];
            return null;
        }
    }
}
