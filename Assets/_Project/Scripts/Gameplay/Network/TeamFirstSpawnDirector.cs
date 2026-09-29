using System.Collections.Generic;
using System.Reflection;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// R8（审计复审 F2）首次出生队伍分区接管：接管 PlayerSpawner 的真实
    /// SceneManager.OnClientLoadedStartScenes 生成入口，在 ServerManager.Spawn 之前选点并把
    /// 位置/朝向传给 GetPooledInstantiated——
    /// 修复"首次出生仍走 8 点轮转、16 连接复用同点互推、不守队伍分区"。补人/重生路径不受影响
    /// （重生本就走 NetworkCombatAuthority.ServerRespawn 的 SelectTeamRespawnPoint，同一队伍策略）。
    /// 不修改 FishNet 原厂；原 PlayerSpawner 的私有回调仍订阅着事件，但其 prefab 被显式置空，
    /// 因此不会发生双生成；禁用本组件时恢复 prefab，保留原生生成行为。None 队/无出生点/
    /// 离线（未开服）零介入。
    /// </summary>
    public sealed class TeamFirstSpawnDirector : MonoBehaviour
    {
        private FishNet.Component.Spawning.PlayerSpawner _spawner;
        private NetworkManager _networkManager;
        private NetworkObject _playerPrefab;
        private bool _addToDefaultScene = true;
        private TeamSpawnDirectory.SpawnSlot[] _redSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
        private TeamSpawnDirectory.SpawnSlot[] _blueSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
        private int _redNext;
        private int _blueNext;
        private int _fallbackNext;
        private Transform[] _spawnPoints = System.Array.Empty<Transform>();
        private TeamSpawnDirectory.SpawnSlot[] _fallbackSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
        private bool _takeoverActive;

        private static readonly FieldInfo PlayerPrefabField = typeof(FishNet.Component.Spawning.PlayerSpawner)
            .GetField("_playerPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AddToDefaultSceneField = typeof(FishNet.Component.Spawning.PlayerSpawner)
            .GetField("_addToDefaultScene", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>首个生成调用的已定位姿态；计划完成后才允许进入 pooled instantiate。</summary>
        public readonly struct SpawnPlan
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly int NextIndex;

            public SpawnPlan(Vector3 position, Quaternion rotation, int nextIndex)
            {
                Position = position;
                Rotation = rotation;
                NextIndex = nextIndex;
            }
        }

        /// <summary>
        /// 纯计划 seam：运行时和 EditMode 回归共用，保证队伍姿态在 pooled instantiate 前已经选定。
        /// 非队伍/无槽位时保留 prefab 姿态，NextIndex 不推进。
        /// </summary>
        public static SpawnPlan PlanTeamSpawn(string team, TeamSpawnDirectory.SpawnSlot[] redSlots,
            TeamSpawnDirectory.SpawnSlot[] blueSlots, int startIndex, Vector3 prefabPosition,
            Quaternion prefabRotation, Vector3[] occupiedPositions)
        {
            var slots = team == MatchRules.TeamRed ? redSlots
                : team == MatchRules.TeamBlue ? blueSlots
                : null;
            if (slots == null || slots.Length == 0)
                return new SpawnPlan(prefabPosition, prefabRotation, startIndex);

            int picked = TeamSpawnDirectory.PickTeamSlot(slots, occupiedPositions, startIndex, out int nextIndex);
            return picked < 0
                ? new SpawnPlan(prefabPosition, prefabRotation, startIndex)
                : new SpawnPlan(slots[picked].Position, slots[picked].Rotation, nextIndex);
        }

        private void OnEnable()
        {
            _spawner = FindFirstObjectByType<FishNet.Component.Spawning.PlayerSpawner>();
            if (_spawner == null) return; // 无 PlayerSpawner（异常场景）：零介入
            _networkManager = _spawner.GetComponentInParent<NetworkManager>() ?? InstanceFinder.NetworkManager;
            _playerPrefab = PlayerPrefabField?.GetValue(_spawner) as NetworkObject;
            if (_networkManager == null || _playerPrefab == null)
            {
                Debug.LogWarning("[TeamFirstSpawnDirector] 无法取得 PlayerSpawner 的生成入口或 prefab，保持 FishNet 原生生成（首帧队伍出生未接管）");
                return;
            }
            if (AddToDefaultSceneField?.GetValue(_spawner) is bool addToDefaultScene)
                _addToDefaultScene = addToDefaultScene;
            RebuildSlots();
            // PlayerSpawner.Awake 已订阅 FishNet 的私有 sceneLoaded 回调；仅 enabled=false
            // 不会解绑它。置空 prefab 是公开 API 可逆的“熔断”，原回调看到 null 后直接返回，
            // 本接管回调成为唯一实际生成入口，避免一次连接生成两个 Player。
            _spawner.SetPlayerPrefab(null);
            _networkManager.SceneManager.OnClientLoadedStartScenes += HandleClientLoadedStartScenes;
            _takeoverActive = true;
        }

        private void OnDisable()
        {
            if (!_takeoverActive) return;
            if (_networkManager != null)
                _networkManager.SceneManager.OnClientLoadedStartScenes -= HandleClientLoadedStartScenes;
            if (_spawner != null && _playerPrefab != null)
                _spawner.SetPlayerPrefab(_playerPrefab);
            _takeoverActive = false;
        }

        /// <summary>由 PlayerSpawner.Spawns（公开字段）构建红蓝槽位表（场景出生点变更时可在 OnEnable 重跑）。</summary>
        public void RebuildSlots()
        {
            var spawns = SceneSpawnPoints.Current();
            if (spawns.Length == 0) spawns = _spawner != null ? _spawner.Spawns : null;
            if (spawns != null) spawns = System.Array.FindAll(spawns, point => point != null);
            if (spawns == null || spawns.Length == 0)
            {
                _redSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
                _blueSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
                _spawnPoints = System.Array.Empty<Transform>();
                _fallbackSlots = System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
                return;
            }
            bool unchanged = spawns.Length == _spawnPoints.Length;
            for (int i = 0; unchanged && i < spawns.Length; i++)
                unchanged = spawns[i] == _spawnPoints[i] && spawns[i] != null
                    && spawns[i].position == _fallbackSlots[i].Position
                    && spawns[i].rotation == _fallbackSlots[i].Rotation;
            if (unchanged) return;
            _spawnPoints = (Transform[])spawns.Clone();
            _fallbackSlots = new TeamSpawnDirectory.SpawnSlot[spawns.Length];
            var positions = new Vector3[spawns.Length];
            var rotations = new Quaternion[spawns.Length];
            for (int i = 0; i < spawns.Length; i++)
            {
                positions[i] = spawns[i] != null ? spawns[i].position : Vector3.zero;
                rotations[i] = spawns[i] != null ? spawns[i].rotation : Quaternion.identity;
                _fallbackSlots[i] = new TeamSpawnDirectory.SpawnSlot(positions[i], rotations[i]);
            }
            var red = new List<TeamSpawnDirectory.SpawnSlot>();
            var blue = new List<TeamSpawnDirectory.SpawnSlot>();
            TeamSpawnDirectory.BuildTeamSlots(positions, rotations, red, blue);
            _redSlots = red.ToArray();
            _blueSlots = blue.ToArray();
            _redNext = 0;
            _blueNext = 0;
            _fallbackNext = 0;
        }

        /// <summary>测试接缝：直接以既有槽位表初始化（不走场景 PlayerSpawner）。</summary>
        public void InjectSlotsForTests(TeamSpawnDirectory.SpawnSlot[] redSlots, TeamSpawnDirectory.SpawnSlot[] blueSlots)
        {
            _redSlots = redSlots ?? System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
            _blueSlots = blueSlots ?? System.Array.Empty<TeamSpawnDirectory.SpawnSlot>();
            _redNext = 0;
            _blueNext = 0;
            _fallbackNext = 0;
        }

        private void HandleClientLoadedStartScenes(NetworkConnection connection, bool asServer)
        {
            if (!asServer || !_takeoverActive || _networkManager == null || !_networkManager.IsServerStarted)
                return; // 客户端实例/离线：零介入

            RebuildSlots(); // map transitions can leave PlayerSpawner with old-scene references

            string team = ResolveTeam(_networkManager, connection);
            var slots = team == MatchRules.TeamRed ? _redSlots
                : team == MatchRules.TeamBlue ? _blueSlots
                : null;
            Vector3 position = _playerPrefab.transform.position;
            Quaternion rotation = _playerPrefab.transform.rotation;
            if (slots != null && slots.Length > 0)
            {
                int startIndex = team == MatchRules.TeamRed ? _redNext : _blueNext;
                var plan = PlanTeamSpawn(team, _redSlots, _blueSlots, startIndex,
                    position, rotation, CollectOccupiedPositions());
                if (team == MatchRules.TeamRed) _redNext = plan.NextIndex;
                else _blueNext = plan.NextIndex;
                position = plan.Position;
                rotation = plan.Rotation;
            }
            else
            {
                // None 队/非 TDM 仍走与 FishNet 原生相同的 Spawn 数组轮转，
                // 但也由本入口完成，确保不会因熔断原 PlayerSpawner 而漏生成。
                if (_fallbackSlots.Length > 0)
                {
                    int picked = TeamSpawnDirectory.PickTeamSlot(_fallbackSlots, CollectOccupiedPositions(),
                        _fallbackNext, out _fallbackNext);
                    if (picked >= 0)
                    {
                        position = _fallbackSlots[picked].Position;
                        rotation = _fallbackSlots[picked].Rotation;
                    }
                }
            }

            // 关键顺序：位置/朝向作为实例化参数进入 Spawn；ServerManager.Spawn 内部
            // 随即 RebuildObservers/WriteSpawn，首个序列化姿态因此已经是队伍槽位。
            position = SpawnGrounding.Align(position, _playerPrefab.GetComponent<CharacterController>());
            NetworkObject nob = _networkManager.GetPooledInstantiated(
                _playerPrefab, position, rotation, true);
            _networkManager.ServerManager.Spawn(nob, connection);
            if (_addToDefaultScene)
                _networkManager.SceneManager.AddOwnerToDefaultScene(nob);
        }

        private static string ResolveTeam(NetworkManager networkManager, NetworkConnection connection)
        {
            var authenticator = networkManager.GetComponent<JoinTicketAuthenticator>();
            if (connection == null || authenticator == null) return MatchRules.TeamNone;
            return authenticator.AcceptedUsers.TryGetValue(connection.ClientId, out var profile)
                   && profile != null
                   && !string.IsNullOrEmpty(profile.TeamId)
                ? profile.TeamId
                : MatchRules.TeamNone;
        }

        /// <summary>占用位置 = 其他【存活】玩家（含队友：占位避让对所有人对生效；死者不参与）。</summary>
        private static Vector3[] CollectOccupiedPositions()
        {
            var players = FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None);
            var positions = new List<Vector3>(players.Length);
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (player == null || player.IsDead) continue;
                positions.Add(player.transform.position);
            }
            return positions.ToArray();
        }
    }
}
