using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day3 切枪流：计时器真相、收枪完成交换点（旧枪 holsterTime 耗尽即换，新枪出枪接续）、打断矩阵、弹药不回滚。
    /// EditMode 要点：AddComponent 不跑 Awake/Start/OnEnable（引用字段需反射解析）；
    /// SerializedObject 的 objectReferenceValue 对内存 SO 静默丢弃（值字段正常）——引用一律反射直写。
    /// </summary>
    public sealed class ArsenalTests
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
            _root = new GameObject("Arsenal_Test");
            _actions = _root.AddComponent<ActionSystem>();
            _root.AddComponent<CombatResolver>();
            _controller = _root.AddComponent<WeaponController>();
            _arsenal = _root.AddComponent<Arsenal>();

            _pistol = NewWeapon("test.pistol", 0.4f, 0.4f);
            _rifle = NewWeapon("test.rifle", 0.6f, 0.4f);
            _balance = NewBalance(("test.pistol", 34, 12, 48, 1.35f), ("test.rifle", 26, 30, 120, 2.2f));

            // 引用字段反射直写（等价场景 Inspector 赋值 + Awake 解析）
            SetField(_controller, "definition", _pistol);
            SetField(_controller, "balanceConfigAsset", _balance);
            SetField(_controller, "actionSystem", _actions);
            SetField(_controller, "combatResolver", _root.GetComponent<CombatResolver>());
            SetField(_controller, "processLocalInput", false);
            _controller.Initialize(_pistol, _balance);

            SetField(_arsenal, "controller", _controller);
            SetField(_arsenal, "actionSystem", _actions);
            SetField(_arsenal, "slots", new[] { _pistol, _rifle });

            // 等价 Arsenal.Start：初始武器 = 槽 0
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

        [Test]
        public void InitialWeapon_IsSlotZero()
        {
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(0));
            Assert.That(_arsenal.ActiveWeapon, Is.SameAs(_pistol));
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_actions.IsBusy, Is.False, "初始装备不占用动作槽");
        }

        [Test]
        public void SameSlot_DuringNoAction_Rejected()
        {
            Assert.That(_arsenal.TrySelectSlot(0), Is.False, "重复选当前槽应被拒绝");
        }

        [Test]
        public void Switch_WeaponSwapsAtHolsterCompletion()
        {
            WeaponDefinition changed = null;
            _arsenal.OnActiveWeaponChanged += def => changed = def;

            Assert.That(_arsenal.TrySelectSlot(1), Is.True);
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.SwitchWeapon));
            Assert.That(_actions.Duration, Is.EqualTo(1.0f).Within(0.001f), "总时长 = 旧枪 holster(0.4) + 新枪 draw(0.6)");

            // 收枪未完成（<0.4s）：不换（此时若按比例过半需等到 0.5s，长出枪武器会产生空窗死区）
            _actions.Tick(0.39f);
            _arsenal.EvaluateSwap();
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(changed, Is.Null);

            // 收枪耗尽（≥0.4s）：换（新 Runtime、新武器身份），新枪出枪恰好在收枪完成帧接续
            _actions.Tick(0.02f);
            _arsenal.EvaluateSwap();
            Assert.That(changed, Is.SameAs(_rifle));
            Assert.That(_controller.Definition, Is.SameAs(_rifle));
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(30));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "新武器满弹匣");
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(1));
        }

        [Test]
        public void Switch_LongDrawWeapon_SwapsAtHolsterTime_NotAtHalfProgress()
        {
            // 手枪(0.4收) → 长出枪步枪(1.6出)：总 2.0s，比例过半=1.0s，收枪完成=0.4s。
            // 本测试锁定修复目标：交换点取 0.4s，避免 0.4~1.0s 持收枪姿态的死区。
            // 注：longRifle 需要数值条目——EquipDefinition→Initialize 按 weaponId 查表，
            // 缺条目 LogError 会使 EditMode 测试失败（CP2 补齐，预存缺陷）。
            var longRifle = NewWeapon("test.longrifle", 1.6f, 0.4f);
            var balance3 = NewBalance(("test.pistol", 34, 12, 48, 1.35f), ("test.rifle", 26, 30, 120, 2.2f), ("test.longrifle", 26, 30, 120, 2.2f));
            try
            {
                _controller.Initialize(_pistol, balance3); // 换三武器数值表（含 longrifle）
                SetField(_arsenal, "slots", new[] { _pistol, longRifle });
                Assert.That(_arsenal.TrySelectSlot(1), Is.True);
                Assert.That(_actions.Duration, Is.EqualTo(2.0f).Within(0.001f));

                _actions.Tick(0.4f);
                _arsenal.EvaluateSwap();
                Assert.That(_controller.Definition, Is.SameAs(longRifle), "收枪完成(0.4s)即交换，不等比例过半(1.0s)");
            }
            finally
            {
                Object.DestroyImmediate(longRifle);
                Object.DestroyImmediate(balance3);
            }
        }

        [Test]
        public void Switch_InterruptsReload_ReloadAmmoUnchanged()
        {
            // 打空一发制造可换弹状态
            _controller.TryFire();
            Assert.That(_controller.TryReload(), Is.True);
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.Reload));

            // 切枪打断换弹（打断矩阵：SwitchWeapon 可打断 Reload）
            Assert.That(_arsenal.TrySelectSlot(1), Is.True);
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.SwitchWeapon));

            // EditMode 下 controller 的 OnEnable（订阅 ActionSystem 事件）未跑——手动触发打断回滚路径
            InvokePrivate(_controller, "HandleActionInterrupted", PlayerActionType.Reload, ActionInterruptReason.SwitchWeapon);
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(11), "弹药不回滚");
        }

        [Test]
        public void InterruptedSwitchBeforeSwap_KeepsOldWeapon()
        {
            Assert.That(_arsenal.TrySelectSlot(1), Is.True);

            // 交换点之前被打断（如死亡）：武器不变
            _actions.Tick(0.1f);
            _arsenal.EvaluateSwap();
            _actions.Interrupt(ActionInterruptReason.Death);

            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(0));
        }

        [Test]
        public void InterruptedSwitchAfterSwap_KeepsNewWeapon()
        {
            Assert.That(_arsenal.TrySelectSlot(1), Is.True);

            // 交换点之后被打断：保留新武器（与换弹被打断不回滚弹药同理）
            _actions.Tick(0.45f);
            _arsenal.EvaluateSwap();
            Assert.That(_controller.Definition, Is.SameAs(_rifle));

            _actions.Interrupt(ActionInterruptReason.Death);
            Assert.That(_controller.Definition, Is.SameAs(_rifle));
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(1));
        }

        [Test]
        public void Switch_FiresPresentationEvents_InOrder()
        {
            var order = new List<string>();
            _arsenal.OnSwitchStarted += (_, slot) => order.Add($"start:{slot}");
            _arsenal.OnActiveWeaponChanged += def => order.Add($"changed:{def.WeaponId}");

            _arsenal.TrySelectSlot(1);
            for (int i = 0; i < 120; i++)
            {
                _actions.Tick(0.011f);
                _arsenal.EvaluateSwap();
            }

            // OnSwitchCompleted 由 ActionSystem 事件驱动（EditMode 下 OnEnable 未跑），此处验证 start/changed 序列
            Assert.That(order, Is.EqualTo(new[] { "start:1", "changed:test.rifle" }));
        }

        // ---------- 辅助 ----------

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

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new System.InvalidOperationException($"method not found: {target.GetType().Name}.{method}");
            m.Invoke(target, args);
        }
    }
}
