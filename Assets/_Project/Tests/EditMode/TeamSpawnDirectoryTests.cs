using System.Collections.Generic;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>R8 审计修复：队伍出生目录——红蓝半场切分、16 人槽位展开不复用 8 点、
    /// 占位避让（同帧多生成不重位）、安全点排除与回退。</summary>
    public sealed class TeamSpawnDirectoryTests
    {
        /// <summary>Arena 形态：8 个外围环带出生点（±9），4 左 4 右。</summary>
        private static Vector3[] ArenaLikeSpawns()
        {
            return new[]
            {
                new Vector3(-9f, 0f, -9f), new Vector3(-9f, 0f, -3f), new Vector3(-9f, 0f, 3f), new Vector3(-9f, 0f, 9f),
                new Vector3(9f, 0f, -9f), new Vector3(9f, 0f, -3f), new Vector3(9f, 0f, 3f), new Vector3(9f, 0f, 9f),
            };
        }

        [Test]
        public void SplitByCentroid_PartitionsLeftRed_RightBlue()
        {
            var red = new List<int>();
            var blue = new List<int>();
            TeamSpawnDirectory.SplitByCentroid(ArenaLikeSpawns(), red, blue);
            Assert.That(red.Count, Is.EqualTo(4), "x<0 半场 4 点");
            Assert.That(blue.Count, Is.EqualTo(4));
            foreach (var i in red) Assert.That(ArenaLikeSpawns()[i].x, Is.LessThan(0f));
            foreach (var i in blue) Assert.That(ArenaLikeSpawns()[i].x, Is.GreaterThan(0f));
        }

        [Test]
        public void BuildTeamSlots_ExpandsBeyondBasePoints_ForSixteenPlayers()
        {
            var red = new List<TeamSpawnDirectory.SpawnSlot>();
            var blue = new List<TeamSpawnDirectory.SpawnSlot>();
            TeamSpawnDirectory.BuildTeamSlots(ArenaLikeSpawns(), null, red, blue);
            Assert.That(red.Count, Is.GreaterThanOrEqualTo(8), "每队槽位 ≥8（16 连接 = 每队 8 人）");
            Assert.That(blue.Count, Is.GreaterThanOrEqualTo(8));

            // The four authored spawn markers are used before overflow offsets.
            for (int i = 0; i < 4; i++)
            {
                Assert.That(red[i].Position, Is.EqualTo(ArenaLikeSpawns()[i]));
                Assert.That(blue[i].Position, Is.EqualTo(ArenaLikeSpawns()[i + 4]));
            }

            // 槽位两两不重合（占位偏移网格生效）
            for (int i = 0; i < red.Count; i++)
                for (int j = i + 1; j < red.Count; j++)
                    Assert.That(red[i].Position != red[j].Position, $"槽位 {i}/{j} 重合");

            // 队伍分区：红槽全在 x<0、蓝槽全在 x>0
            foreach (var slot in red) Assert.That(slot.Position.x, Is.LessThan(0f));
            foreach (var slot in blue) Assert.That(slot.Position.x, Is.GreaterThan(0f));
        }

        [Test]
        public void PickTeamSlot_RoundRobin_DistinctForSimultaneousSpawns()
        {
            var red = new List<TeamSpawnDirectory.SpawnSlot>();
            var blue = new List<TeamSpawnDirectory.SpawnSlot>();
            TeamSpawnDirectory.BuildTeamSlots(ArenaLikeSpawns(), null, red, blue);
            var slots = red.ToArray();

            // 同帧 4 人生成（无已占用位置）：轮转指针保证互不重位
            int next = 0;
            var picks = new List<Vector3>();
            for (int i = 0; i < 4; i++)
            {
                int picked = TeamSpawnDirectory.PickTeamSlot(slots, null, next, out next);
                Assert.That(picked, Is.GreaterThanOrEqualTo(0));
                picks.Add(slots[picked].Position);
            }
            for (int i = 0; i < picks.Count; i++)
                for (int j = i + 1; j < picks.Count; j++)
                    Assert.That(picks[i] != picks[j], "同帧生成不得重位");
        }

        [Test]
        public void PickTeamSlot_AvoidsOccupiedPositions_WithExclusionRadius()
        {
            var red = new List<TeamSpawnDirectory.SpawnSlot>();
            var blue = new List<TeamSpawnDirectory.SpawnSlot>();
            TeamSpawnDirectory.BuildTeamSlots(ArenaLikeSpawns(), null, red, blue);
            var slots = red.ToArray();
            // 首槽附近站着一名玩家：占位安全距离为 1.5m，跳过当前点。
            var occupied = new[] { slots[0].Position + new Vector3(1f, 0f, 0f) };
            int picked = TeamSpawnDirectory.PickTeamSlot(slots, occupied, 0, out _);
            Assert.That(Vector3.Distance(slots[picked].Position, occupied[0]),
                Is.GreaterThanOrEqualTo(1.5f));
        }

        [Test]
        public void PickTeamSlot_AllBlocked_FallsBackToFarthest()
        {
            var slots = new[]
            {
                new TeamSpawnDirectory.SpawnSlot(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new TeamSpawnDirectory.SpawnSlot(new Vector3(1f, 0f, 0f), Quaternion.identity),
            };
            // 全部槽位都被占用者近距覆盖 → 回退最远者（确定性）
            var occupied = new[]
            {
                new Vector3(0.1f, 0f, 0f), new Vector3(1.2f, 0f, 0f),
            };
            int picked = TeamSpawnDirectory.PickTeamSlot(slots, occupied, 0, out _);
            Assert.That(picked, Is.EqualTo(1), "槽 1 距占用者 0.2m > 槽 0 的 0.1m，应取更远者");
        }

        [Test]
        public void FirstSpawnPlan_ResolvesTeamPoseBeforeSpawnInvocation()
        {
            var redSlots = new[]
            {
                new TeamSpawnDirectory.SpawnSlot(new Vector3(-7f, 1f, 3f), Quaternion.Euler(0f, 90f, 0f)),
            };
            var prefabPosition = new Vector3(100f, 100f, 100f);
            var prefabRotation = Quaternion.identity;
            var plan = TeamFirstSpawnDirector.PlanTeamSpawn(
                MatchRules.TeamRed, redSlots, System.Array.Empty<TeamSpawnDirectory.SpawnSlot>(), 0,
                prefabPosition, prefabRotation, null);

            // 模拟 GetPooledInstantiated/Spawn 的最小入口：收到的已是计划姿态，
            // 不存在 Spawn 后再改 Transform 的窗口。
            Vector3 spawnedPosition = prefabPosition;
            Quaternion spawnedRotation = prefabRotation;
            void SpawnAt(Vector3 position, Quaternion rotation)
            {
                spawnedPosition = position;
                spawnedRotation = rotation;
            }
            SpawnAt(plan.Position, plan.Rotation);

            Assert.That(spawnedPosition, Is.EqualTo(redSlots[0].Position));
            Assert.That(spawnedRotation, Is.EqualTo(redSlots[0].Rotation));
            Assert.That(spawnedPosition, Is.Not.EqualTo(prefabPosition));
        }

        [Test]
        public void EmptyOrDegenerateInputs_AreSafe()
        {
            var red = new List<TeamSpawnDirectory.SpawnSlot>();
            var blue = new List<TeamSpawnDirectory.SpawnSlot>();
            TeamSpawnDirectory.BuildTeamSlots(new Vector3[0], null, red, blue);
            Assert.That(red.Count, Is.EqualTo(0));
            Assert.That(TeamSpawnDirectory.PickTeamSlot(new TeamSpawnDirectory.SpawnSlot[0], null, 0, out _), Is.EqualTo(-1));

            // 单侧分布退化：全部点在一侧 → 两队都退化为全集
            TeamSpawnDirectory.BuildTeamSlots(new[] { new Vector3(-1f, 0f, 0f), new Vector3(-2f, 0f, 0f) }, null, red, blue);
            Assert.That(red.Count, Is.GreaterThan(0));
            Assert.That(blue.Count, Is.GreaterThan(0));
        }
    }
}
