using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-18 实机九项反馈 Batch A/B 回归：
    /// ① 问题8——服务器重生弹药重置（ServerResetAmmoToLoadoutDefault）：弹匣补满+备弹回配装
    ///    初始值+切枪缓存清空+换弹/冷却态复位+OnAmmoChanged 推送（SyncVar 数据源契约）；
    /// ② 问题5——命中特效贴人（WeaponView.SpawnImpact）：Target 非空时特效挂在目标视觉体下
    ///    （尸体倒地/移动跟随，不再悬停原站立位置），Target 空（墙/地）保持世界坐标。
    /// 搭建方式与 WeaponSlotAmmoPersistenceTests 一致（AddComponent 不跑 Awake——引用反射直写）。
    /// </summary>
    public sealed class RespawnAmmoAndImpactParentTests
    {
        private GameObject _root;
        private ActionSystem _actions;
        private WeaponController _controller;
        private Arsenal _arsenal;
        private WeaponDefinition _pistol;
        private WeaponDefinition _rifle;
        private DemoBalanceConfig _balance;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("RespawnAmmo_Test");
            _actions = _root.AddComponent<ActionSystem>();
            _root.AddComponent<CombatResolver>();
            _controller = _root.AddComponent<WeaponController>();
            _arsenal = _root.AddComponent<Arsenal>();

            _pistol = NewWeapon("test.pistol", 0.3f, 0.3f);
            _rifle = NewWeapon("test.rifle", 0.3f, 0.3f);
            _balance = NewBalance(("test.pistol", 34, 12, 48, 1.0f), ("test.rifle", 26, 30, 120, 2.0f));

            SetField(_controller, "definition", _pistol);
            SetField(_controller, "balanceConfigAsset", _balance);
            SetField(_controller, "actionSystem", _actions);
            SetField(_controller, "combatResolver", _root.GetComponent<CombatResolver>());
            SetField(_controller, "processLocalInput", false);
            _controller.Initialize(_pistol, _balance);

            SetField(_arsenal, "controller", _controller);
            SetField(_arsenal, "actionSystem", _actions);
            SetField(_arsenal, "slots", new[] { _pistol, _rifle });
            _arsenal.TrySelectSlot(0);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_pistol);
            Object.DestroyImmediate(_rifle);
            Object.DestroyImmediate(_balance);
        }

        private void CompleteSwitch()
        {
            for (int i = 0; i < 200; i++)
            {
                _actions.Tick(0.01f);
                _arsenal.EvaluateSwap();
            }
        }

        private void FireRounds(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.That(_controller.TryFire(), Is.True, $"第 {i + 1} 发必须成功");
                _controller.Runtime.Tick(0.2f);
            }
        }

        [Test]
        public void ServerResetAmmo_RefillsMagazineAndReserve_FiresSyncContract()
        {
            FireRounds(5); // pistol 7/48
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(7), "前置：已耗 5 发");
            (int current, int reserve)? lastEvent = null;
            _controller.OnAmmoChanged += (current, reserve) => lastEvent = (current, reserve);

            _controller.ServerResetAmmoToLoadoutDefault();

            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12), "弹匣必须补满");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹必须回配装初始值");
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(lastEvent.HasValue && lastEvent.Value.current == 12 && lastEvent.Value.reserve == 48,
                Is.True, "OnAmmoChanged（服务器 SyncVar 数据源）必须推送重置后的满弹值");
        }

        [Test]
        public void ServerResetAmmo_ClearsSlotCache_SwitchBackIsFull()
        {
            FireRounds(3); // pistol 9/48
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            FireRounds(2); // rifle 28/120
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(28), "前置：副枪已耗 2 发");

            _controller.ServerResetAmmoToLoadoutDefault(); // 重置当前槽（rifle）
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "当前槽补满");

            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12),
                "切枪缓存已清——切回主枪是满弹而非死亡前的 9 发");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48));
        }

        [Test]
        public void ServerResetAmmo_DuringReload_CancelsReloadState()
        {
            FireRounds(4); // 8/48
            Assert.That(_controller.TryReload(), Is.True, "前置：进入换弹");
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Reloading));

            _controller.ServerResetAmmoToLoadoutDefault();

            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready),
                "死亡打断的换弹必须复位（不得复活后卡换弹态）");
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48));
        }

        [Test]
        public void SpawnImpact_WithTarget_ParentsEffectToTarget()
        {
            var (viewGo, view) = NewViewWithPrefabs(out GameObject bloodPrefab, out GameObject wallPrefab);
            var victimGo = new GameObject("Victim");
            var target = victimGo.AddComponent<DamageableTarget>();
            Vector3 point = new Vector3(1.5f, 1.2f, 3f);
            Vector3 normal = Vector3.back;
            var shot = new WeaponShot(Vector3.zero, Vector3.forward,
                new HitscanResult(true, true, point, normal, target));
            try
            {
                InvokeSpawnImpact(view, shot);

                Assert.That(victimGo.transform.childCount, Is.EqualTo(1),
                    "命中玩家：特效必须挂在目标视觉体下（倒地/移动跟随）");
                var fx = victimGo.transform.GetChild(0);
                Assert.That(Vector3.Distance(fx.position, point + normal * 0.01f), Is.LessThan(1e-4f),
                    "挂接后世界位置仍=命中点+法线偏移（Instantiate parent 保持世界位姿）");
            }
            finally
            {
                Object.DestroyImmediate(viewGo);
                Object.DestroyImmediate(victimGo);
                Object.DestroyImmediate(bloodPrefab);
                Object.DestroyImmediate(wallPrefab);
            }
        }

        /// <summary>
        /// 2026-09-18 审计 §6.3：旧实现按 `Damaged ? damagedImpactPrefab : impactPrefab` 选择特效，
        /// 于是**命中角色但零伤害**（出生无敌/友军/已死目标）会在玩家胶囊上贴一枚墙面弹孔——
        /// 用户实机看到的"空气里的实体弹孔"。断言必须按新产品语义写：角色命中永不用环境弹孔。
        /// </summary>
        [Test]
        public void SpawnImpact_OnCharacterWithoutDamage_NeverUsesEnvironmentDecal()
        {
            var (viewGo, view) = NewViewWithPrefabs(out GameObject bloodPrefab, out GameObject wallPrefab);
            var victimGo = new GameObject("Victim");
            var target = victimGo.AddComponent<DamageableTarget>();
            string wallPrefix = wallPrefab.name;
            var shot = new WeaponShot(Vector3.zero, Vector3.forward,
                new HitscanResult(true, false, new Vector3(0f, 1f, 3f), Vector3.back, target));
            try
            {
                int wallRootsBefore = CountSceneRootsWithName(wallPrefix);
                InvokeSpawnImpact(view, shot);

                Assert.That(CountSceneRootsWithName(wallPrefix), Is.EqualTo(wallRootsBefore),
                    "角色命中不得生成环境弹孔（零伤害也一样）");
                Assert.That(victimGo.transform.childCount, Is.EqualTo(1),
                    "角色只播短时命中反馈，并挂到目标视觉体下跟随身体");
                Assert.That(victimGo.transform.GetChild(0).name.StartsWith(bloodPrefab.name), Is.True,
                    "角色反馈用的是 damagedImpactPrefab（血花），不是 impactPrefab（弹孔）");
            }
            finally
            {
                Object.DestroyImmediate(viewGo);
                Object.DestroyImmediate(victimGo);
                Object.DestroyImmediate(bloodPrefab);
                Object.DestroyImmediate(wallPrefab);
            }
        }

        [Test]
        public void SpawnImpact_OnCharacterWithoutCharacterFx_SpawnsNothing()
        {
            // 没配角色反馈时是"不生成"，而不是"退回弹孔"——宁可无反馈也不产生空气弹孔。
            var (viewGo, view) = NewViewWithPrefabs(out GameObject bloodPrefab, out GameObject wallPrefab);
            SetField(view, "damagedImpactPrefab", null);
            var victimGo = new GameObject("Victim");
            var target = victimGo.AddComponent<DamageableTarget>();
            string wallPrefix = wallPrefab.name;
            var shot = new WeaponShot(Vector3.zero, Vector3.forward,
                new HitscanResult(true, true, new Vector3(0f, 1f, 3f), Vector3.back, target));
            try
            {
                int wallRootsBefore = CountSceneRootsWithName(wallPrefix);
                InvokeSpawnImpact(view, shot);

                Assert.That(CountSceneRootsWithName(wallPrefix), Is.EqualTo(wallRootsBefore));
                Assert.That(victimGo.transform.childCount, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(viewGo);
                Object.DestroyImmediate(victimGo);
                Object.DestroyImmediate(bloodPrefab);
                Object.DestroyImmediate(wallPrefab);
            }
        }

        [Test]
        public void SpawnImpact_OnEnvironment_KeepsWorldSpaceDecal()
        {
            // 环境命中路径不受角色规则影响：仍是真实表面上的弹孔（回归保护）
            var (viewGo, view) = NewViewWithPrefabs(out GameObject bloodPrefab, out GameObject wallPrefab);
            string prefix = wallPrefab.name;
            var shot = new WeaponShot(Vector3.zero, Vector3.forward,
                new HitscanResult(true, false, new Vector3(0f, 1f, 3f), Vector3.back, null));
            try
            {
                int before = CountSceneRootsWithName(prefix);
                InvokeSpawnImpact(view, shot);
                Assert.That(CountSceneRootsWithName(prefix), Is.EqualTo(before + 1),
                    "环境命中（Target 空）必须继续生成弹孔");
            }
            finally
            {
                foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                    if (go != null && go.transform.parent == null && go.name.StartsWith(prefix))
                        Object.DestroyImmediate(go);
                Object.DestroyImmediate(viewGo);
                Object.DestroyImmediate(bloodPrefab);
                Object.DestroyImmediate(wallPrefab);
            }
        }

        [Test]
        public void SpawnImpact_WithoutTarget_StaysWorldSpace()
        {
            var (viewGo, view) = NewViewWithPrefabs(out GameObject bloodPrefab, out GameObject wallPrefab);
            string prefix = wallPrefab.name;
            Vector3 point = new Vector3(0.5f, 0.8f, 2f);
            Vector3 normal = Vector3.up;
            var shot = new WeaponShot(Vector3.zero, Vector3.forward,
                new HitscanResult(true, false, point, normal, null));
            try
            {
                int rootsBefore = CountSceneRootsWithName(prefix);
                InvokeSpawnImpact(view, shot);
                int rootsAfter = CountSceneRootsWithName(prefix);
                Assert.That(rootsAfter, Is.EqualTo(rootsBefore + 1),
                    "静态场景命中（Target 空）：特效保持世界坐标（根级，无父级）");
            }
            finally
            {
                // 清掉生成的根级特效与模板，避免污染其他用例（prefix 先取值，防模板已毁后访问 .name）
                foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                    if (go != null && go.transform.parent == null && go.name.StartsWith(prefix))
                        Object.DestroyImmediate(go);
                Object.DestroyImmediate(viewGo);
                Object.DestroyImmediate(bloodPrefab);
            }
        }

        // ---------- 辅助 ----------

        private static (GameObject go, Game.Presentation.Weapon.WeaponView view) NewViewWithPrefabs(
            out GameObject bloodPrefab, out GameObject wallPrefab)
        {
            bloodPrefab = new GameObject("Fx_Blood_Test");
            wallPrefab = new GameObject("Fx_Wall_Test");
            var go = new GameObject("WeaponView_Test");
            var view = go.AddComponent<Game.Presentation.Weapon.WeaponView>();
            SetField(view, "damagedImpactPrefab", bloodPrefab);
            SetField(view, "impactPrefab", wallPrefab);
            return (go, view);
        }

        private static void InvokeSpawnImpact(Game.Presentation.Weapon.WeaponView view, WeaponShot shot)
        {
            var m = typeof(Game.Presentation.Weapon.WeaponView).GetMethod("SpawnImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(m, Is.Not.Null, "WeaponView.SpawnImpact 必须存在（私有）");
            m.Invoke(view, new object[] { shot });
        }

        private static int CountSceneRootsWithName(string name)
        {
            int count = 0;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                if (go != null && go.transform.parent == null && go.name.StartsWith(name)) count++;
            return count;
        }

        private static WeaponDefinition NewWeapon(string id, float draw, float holster)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("weaponId").stringValue = id;
            so.FindProperty("drawTime").floatValue = draw;
            so.FindProperty("holsterTime").floatValue = holster;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static DemoBalanceConfig NewBalance(params (string id, int damage, int mag, int reserve, float reload)[] entries)
        {
            var balance = ScriptableObject.CreateInstance<DemoBalanceConfig>();
            var so = new SerializedObject(balance);
            var weapons = so.FindProperty("weapons");
            weapons.arraySize = entries.Length;
            for (int i = 0; i < entries.Length; i++)
            {
                var e = weapons.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("WeaponId").stringValue = entries[i].id;
                var stat = e.FindPropertyRelative("Stat");
                stat.FindPropertyRelative("Damage").intValue = entries[i].damage;
                stat.FindPropertyRelative("Rpm").intValue = 360;
                stat.FindPropertyRelative("MagSize").intValue = entries[i].mag;
                stat.FindPropertyRelative("ReserveAmmo").intValue = entries[i].reserve;
                stat.FindPropertyRelative("ReloadTime").floatValue = entries[i].reload;
                stat.FindPropertyRelative("Spread").floatValue = 0.25f;
                stat.FindPropertyRelative("MaxRange").floatValue = 120;
                stat.FindPropertyRelative("AdsFov").floatValue = 50f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return balance;
        }

        private static void SetField(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new System.InvalidOperationException($"field not found: {target.GetType().Name}.{field}");
            f.SetValue(target, value);
        }

        // ---- F12（2026-09-19 审计）：Owner 本地重生补弹镜像 ----

        [Test]
        public void OwnerResetAmmo_MirrorsRespawnBaseline_IncludingSlotCache()
        {
            // 死亡前残弹状态：主枪耗 5 发（7/48）
            FireRounds(5);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(7), "前置：死亡前残弹");

            // Owner 收到重生通知 → 本地镜像（ObservesRespawned 在 Owner 客户端调用的方法）
            _controller.OwnerResetAmmoToRespawnBaseline();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12), "本地 Runtime 补满——TryFire 不再被残弹拒绝");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹回配装初始值");

            // 两槽缓存一并清空：副枪耗弹后回主枪，再镜像 → 副枪缓存不得读回残弹
            FireRounds(2); // 主枪再耗 2 发（10/48）→ 进缓存
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            FireRounds(2); // rifle 28/120
            _controller.OwnerResetAmmoToRespawnBaseline(); // 当前槽 rifle 补满
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "当前槽（rifle）补满");

            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "镜像后切回副枪必须读满弹（缓存已清，旧实现读回 28）");
        }
    }
}
