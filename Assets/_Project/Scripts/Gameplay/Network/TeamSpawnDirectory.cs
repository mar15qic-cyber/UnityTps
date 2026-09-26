using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// R8（审计修复）队伍出生目录（纯逻辑，EditMode 可测）：首次生成/补人/重生共用的队伍出生策略数据源。
    /// Arena 只有 8 个原厂出生点（Spawn_0..7）——按几何中轴切分红蓝半场（与 MatchRules.SelectTeamRespawnPoint
    /// 同一切分），并为每队按占位偏移展开确定性槽位（同基础点多玩家的避让网格），使 16 连接不复用 8 点、
    /// 首次出生遵守队伍分区。不改 FishNet 原厂：TeamFirstSpawnDirector 接管项目侧生成入口，
    /// 在 ServerManager.Spawn 前以本目录槽位作为实例化位置。
    /// </summary>
    public static class TeamSpawnDirectory
    {
        /// <summary>同一基础点的占位偏移（米，XZ 平面；索引 0 = 原点）。间距 ≥ 2m，避让角色胶囊互推。</summary>
        public static readonly Vector2[] SlotOffsets =
        {
            new(0f, 0f), new(2.2f, 0f), new(-2.2f, 0f), new(0f, 2.2f), new(0f, -2.2f),
            new(2.2f, 2.2f), new(-2.2f, 2.2f), new(2.2f, -2.2f), new(-2.2f, -2.2f),
        };

        /// <summary>每队槽位上限（16 连接 = 每队 8 人；基础点数 × 偏移数已远超需要，显式封顶防膨胀）。</summary>
        public const int MaxSlotsPerTeam = 16;

        public readonly struct SpawnSlot
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public SpawnSlot(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        }

        /// <summary>按几何中轴切分基础点（x &lt; 全体质心 → 红半场，其余蓝半场；与 SelectTeamRespawnPoint 同切分）。</summary>
        public static void SplitByCentroid(IReadOnlyList<Vector3> basePoints, List<int> redIndices, List<int> blueIndices)
        {
            redIndices.Clear();
            blueIndices.Clear();
            if (basePoints == null || basePoints.Count == 0) return;
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < basePoints.Count; i++) centroid += basePoints[i];
            centroid /= basePoints.Count;
            for (int i = 0; i < basePoints.Count; i++)
            {
                if (basePoints[i].x < centroid.x) redIndices.Add(i);
                else blueIndices.Add(i);
            }
        }

        /// <summary>展开一队槽位：基础点 × 占位偏移（确定性顺序），并封顶 MaxSlotsPerTeam。
        /// 任一半场为空（单侧分布退化）时两队列各自退化为全集槽位（调用方按空检查走全集选点）。</summary>
        public static void BuildTeamSlots(Vector3[] basePositions, Quaternion[] baseRotations,
            List<SpawnSlot> redSlots, List<SpawnSlot> blueSlots)
        {
            redSlots.Clear();
            blueSlots.Clear();
            if (basePositions == null || basePositions.Length == 0) return;
            var redIndices = new List<int>();
            var blueIndices = new List<int>();
            SplitByCentroid(basePositions, redIndices, blueIndices);
            // 半场为空/单侧全占 → 两队都退化为全集（SelectRespawnPoint 的全集语义兜底）
            if (redIndices.Count == 0 || blueIndices.Count == 0)
            {
                redIndices.Clear();
                blueIndices.Clear();
                for (int i = 0; i < basePositions.Length; i++) { redIndices.Add(i); blueIndices.Add(i); }
            }
            AppendSlots(redIndices, basePositions, baseRotations, redSlots);
            AppendSlots(blueIndices, basePositions, baseRotations, blueSlots);
        }

        private static void AppendSlots(List<int> indices, Vector3[] basePositions, Quaternion[] baseRotations,
            List<SpawnSlot> output)
        {
            // Use every authored point once before deriving overflow slots. Four players
            // per side therefore occupy four fixed spawn markers rather than four offsets
            // clustered around Spawn_0.
            foreach (Vector2 offset in SlotOffsets)
            {
                foreach (int baseIndex in indices)
                {
                    if (output.Count >= MaxSlotsPerTeam) return;
                    Vector3 basePosition = basePositions[baseIndex];
                    Vector3 position = basePosition + new Vector3(offset.x, 0f, offset.y);
                    Quaternion rotation = baseRotations != null
                        && baseIndex < baseRotations.Length
                        && baseRotations[baseIndex] != default(Quaternion)
                        ? baseRotations[baseIndex]
                        : Quaternion.identity;
                    output.Add(new SpawnSlot(position, rotation));
                }
            }
        }

        /// <summary>
        /// 选槽（纯函数）：从 startIndex 轮转扫描一圈——
        /// ① 首选「与所有已占用位置距离 ≥ SpawnExclusionRadiusMeters」的槽位（安全占位避让）；
        /// ② 全部被排除 → 取「到最近占用位置距离最大」的槽位（并列取扫描序最先）；
        /// ③ 无占用位置 → 直接取 startIndex（轮转分配，同时生成不撞位）。
        /// nextIndex = 选中槽位的下一个（轮转指针推进；并发同帧生成各自推进、互不重位）。
        /// 返回 -1 = 无槽位。
        /// </summary>
        public static int PickTeamSlot(SpawnSlot[] slots, Vector3[] occupiedPositions, int startIndex, out int nextIndex)
        {
            nextIndex = startIndex;
            if (slots == null || slots.Length == 0) return -1;
            startIndex = ((startIndex % slots.Length) + slots.Length) % slots.Length;

            if (occupiedPositions == null || occupiedPositions.Length == 0)
            {
                nextIndex = (startIndex + 1) % slots.Length;
                return startIndex;
            }

            int safe = -1;
            int farthest = -1;
            float farthestDistance = float.MinValue;
            for (int offset = 0; offset < slots.Length; offset++)
            {
                int index = (startIndex + offset) % slots.Length;
                float nearest = float.MaxValue;
                for (int j = 0; j < occupiedPositions.Length; j++)
                {
                    float distance = Vector3.Distance(slots[index].Position, occupiedPositions[j]);
                    if (distance < nearest) nearest = distance;
                }
                if (nearest > farthestDistance)
                {
                    farthestDistance = nearest;
                    farthest = index;
                }
                if (nearest >= 1.5f)
                {
                    safe = index;
                    break; // 轮转序优先：第一个安全槽位即取（分配确定性 + 同帧多生成不重位）
                }
            }
            int picked = safe >= 0 ? safe : farthest;
            nextIndex = (picked + 1) % slots.Length;
            return picked;
        }
    }
}
