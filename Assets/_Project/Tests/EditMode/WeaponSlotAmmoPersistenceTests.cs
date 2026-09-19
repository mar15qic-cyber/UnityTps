using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day4 残余审计 P0-2 回归：切槽不得补满弹药——每把武器持有独立且持久的服务器权威
    /// 运行时状态（WeaponController 按 WeaponId 缓存弹匣/备弹），切槽只切换当前引用；
    /// 换弹中切枪只取消换弹不赠送子弹；配件容量变化按新容量钳制当前弹药（不隐式补满）；
    /// OnAmmoChanged（服务器 SyncVar 数据源）不回退。
    /// 搭建方式与 ArsenalTests 一致（AddComponent 不跑 Awake——引用反射直写）。
    /// </summary>
    public sealed class WeaponSlotAmmoPersistenceTests
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
            _root = new GameObject("SlotAmmo_Test");
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
            // 小步 Tick + 逐帧 EvaluateSwap（交换点=收枪时长耗尽且动作未完成；
            // 一口气 Tick 越过整个动作会让 CurrentAction 变 None、交换窗口错过）
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
                Assert.That(_controller.TryFire(), Is.True, $"第 {i + 1} 发必须成功（冷却用 Runtime.Tick 清除）");
                _controller.Runtime.Tick(0.2f); // 清除射速冷却（internal，InternalsVisibleTo）
            }
        }

        [Test]
        public void ExtendedMagazine_SwitchBackAndReapplyAttachments_KeepsLoadedRounds()
        {
            var extended = new AttachmentAssetEntry
            {
                slot = AttachmentSlotType.Magazine,
                modifiers = { new AttachmentModifierEntry { stat = WeaponStatId.MagazineSize, op = ModifierOperation.Add, value = 6f } },
            };
            _controller.SetAttachments(new[] { extended });
            Assert.That(_controller.TryReload(), Is.True);
            _controller.Runtime.CompleteReload(); // EditMode fixture 不依赖 MonoBehaviour.OnEnable 的动作事件接线
            _actions.Tick(2f);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(18));
            FireRounds(1);
            var reserve = _controller.Runtime.ReserveAmmo;

            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(30), "副枪不得继承主枪扩容");
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            _controller.SetAttachments(new[] { extended }); // 服务器装备回调重套同一权威配装
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(17));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(reserve));

            _controller.SetAttachments(System.Array.Empty<AttachmentAssetEntry>());
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12), "真实卸下扩容仍按新容量钳制");
        }

        [Test]
        public void FireSwitchAwayAndBack_KeepsPrimaryAmmo_NoFreeRefill()
        {
            FireRounds(3);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9), "前置：主枪已耗 3 发");

            Assert.That(_arsenal.TrySelectSlot(1), Is.True);
            CompleteSwitch();
            Assert.That(_controller.Definition, Is.SameAs(_rifle));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "副枪首次装备=满弹（独立状态）");

            Assert.That(_arsenal.TrySelectSlot(0), Is.True);
            CompleteSwitch();
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9),
                "切回主枪必须恢复切出时的 9 发——不得隐式补满（P0-2 玩法漏洞回归）");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹同样持久");
        }

        [Test]
        public void SecondaryKeepsOwnFiredState_Independently()
        {
            FireRounds(3); // pistol 9/48
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            FireRounds(2); // rifle 28/120
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9));

            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(28),
                "副枪自己打掉的 2 发必须持久（两槽状态互不串扰）");
        }

        [Test]
        public void CancelReloadBySwitch_ThenBack_GrantsNoAmmo()
        {
            FireRounds(1); // 11/48
            Assert.That(_controller.TryReload(), Is.True, "前置：进入换弹");

            _arsenal.TrySelectSlot(1);
            CompleteSwitch(); // 切枪打断换弹（不赠送子弹）
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();

            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(11),
                "换弹中切枪再切回：弹药保持打断时状态，不得因换弹赠送");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹未被换弹消耗");
        }

        [Test]
        public void AttachmentMagSizeChange_ClampsCurrent_NoImplicitRefill()
        {
            FireRounds(2); // pistol 10/12
            var before = _controller.Runtime.CurrentAmmo;

            // 加长弹匣（MagazineSize Add +6 → 12→18）
            var extended = new AttachmentAssetEntry
            {
                itemId = "test.mag.extended",
                slot = AttachmentSlotType.Magazine,
                modifiers = { new AttachmentModifierEntry { stat = WeaponStatId.MagazineSize, op = ModifierOperation.Add, value = 6f } },
            };
            _controller.SetAttachments(new[] { extended });
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(18), "容量按配件重算");
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(before),
                "容量变化只钳制不补满（当前弹药原样保留）");

            // 卸下配件（容量回落 12）——当前弹药被钳制在 10 < 12 不变
            _controller.SetAttachments(System.Array.Empty<AttachmentAssetEntry>());
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(12));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(before), "回落钳制后弹药不变");
        }

        [Test]
        public void OnAmmoChanged_FiresWithRestoredValues_ServerSyncContract()
        {
            FireRounds(3); // 9/48
            (int current, int reserve)? lastEvent = null;
            _controller.OnAmmoChanged += (current, reserve) => lastEvent = (current, reserve);

            _arsenal.TrySelectSlot(1);
            CompleteSwitch(); // 副枪满弹事件
            Assert.That(lastEvent.HasValue && lastEvent.Value.current == 30 && lastEvent.Value.reserve == 120,
                Is.True, "切到副枪：服务器 SyncVar 数据源必须收到副枪满弹");

            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(lastEvent.HasValue && lastEvent.Value.current == 9 && lastEvent.Value.reserve == 48,
                Is.True, "切回主枪：SyncVar 数据源必须收到恢复后的 9/48（服务器权威不退化）");
        }

        // ---------- 辅助（与 ArsenalTests 同款） ----------

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
    }
}
