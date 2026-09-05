using System.Linq;
using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 切枪输入统一通路（Docs/23 表现迭代 2026-09-05）：数字键/滚轮/Q 全部经 Arsenal.TrySelectSlot
    /// 解析；滚轮循环跳空槽、Q 往返最近两把、失败不污染历史、意图事件一次解析一次触发。
    /// InputReader 的键/滚轮采样属 InputSystem 设备层（EditMode 不可模拟），此处锁 Arsenal 语义层。
    /// </summary>
    public sealed class WeaponSwapIntentTests
    {
        private GameObject _go;
        private Arsenal _arsenal;
        private ActionSystem _actions;
        private int _intentEvents;
        private int _lastIntent = -1;

        [SetUp]
        public void SetUp()
        {
            // NUnit 3 实例生命周期为 per-fixture（同类所有用例共享字段），必须显式重置
            _intentEvents = 0;
            _lastIntent = -1;
            // TMP 安全模式：先停用再 AddComponent（延迟 Awake），配置完再激活
            _go = new GameObject("ArsenalHost");
            _go.SetActive(false);
            _actions = _go.AddComponent<ActionSystem>();
            _arsenal = _go.AddComponent<Arsenal>();
            _go.SetActive(true);

            _arsenal.OnSlotIntentResolved += slot => { _intentEvents++; _lastIntent = slot; };
            // 直接构造含空槽的槽位表（ConfigureSlots 会过滤 null——跳空槽语义只存在于含 null 的槽位表）
            var slotsField = typeof(Arsenal).GetField("slots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            slotsField.SetValue(_arsenal, new WeaponDefinition[4]
            {
                null,
                ScriptableObject.CreateInstance<WeaponDefinition>(),
                null,
                ScriptableObject.CreateInstance<WeaponDefinition>(),
            });
            // EditMode 不跑 Start——手动初始化装备槽 1（初始化分支：无意图事件、无 previous 历史）
            Assert.That(_arsenal.TrySelectSlot(1), Is.True);
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(1));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        /// <summary>推进切枪计时到交换点并完成（走 ActionSystem 真计时器）。</summary>
        private void CompleteSwitch(float holsterPlusDraw)
        {
            int guard = 0;
            while (_actions.CurrentAction == PlayerActionType.SwitchWeapon && guard++ < 1000)
            {
                _arsenal.EvaluateSwap(); // 交换点在收枪时长耗尽处
                _actions.Tick(0.05f);
            }
        }

        [Test]
        public void ScrollDown_MovesToNextValidSlot_SkipsEmpty()
        {
            // 初始槽 1（第二把有效）。滚轮下（-1）→ 槽 2 空 → 跳到槽 3
            _arsenal.TryScrollSwap(-1f);
            Assert.That(_lastIntent, Is.EqualTo(3), "滚轮下应跳过空槽 2 到达槽 3");
            Assert.That(_intentEvents, Is.EqualTo(1), "一次滚动恰好一次意图");
            CompleteSwitch(2f);
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(3));
        }

        [Test]
        public void ScrollUp_WrapsAroundEnd()
        {
            // 初始槽 1。滚轮上（+1）→ 上一把是槽 3（索引 0 空、绕到尾）
            _arsenal.TryScrollSwap(1f);
            Assert.That(_lastIntent, Is.EqualTo(3), "滚轮上应首尾循环到槽 3");
            CompleteSwitch(2f);
            // 再滚轮下（-1）应回到槽 1（跳过空槽 0）
            _arsenal.TryScrollSwap(-1f);
            Assert.That(_lastIntent, Is.EqualTo(1), "滚轮下从槽 3 循环回槽 1");
        }

        [Test]
        public void QuickSwap_NoHistory_NoOp()
        {
            // ConfigureSlots(initialIndex) 的初始装备不是"切换"——无历史
            _arsenal.TryQuickSwap();
            Assert.That(_intentEvents, Is.EqualTo(0), "无历史时 Q 不产生任何意图");
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(1));
        }

        [Test]
        public void QuickSwap_PingPongsBetweenLastTwo()
        {
            _arsenal.TryScrollSwap(-1f); // 1 → 3
            CompleteSwitch(2f);
            _arsenal.TryQuickSwap(); // Q 回上一把 = 槽 1
            Assert.That(_lastIntent, Is.EqualTo(1));
            CompleteSwitch(2f);
            _arsenal.TryQuickSwap(); // 再 Q 回槽 3
            Assert.That(_lastIntent, Is.EqualTo(3));
            CompleteSwitch(2f);
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(3));
        }

        [Test]
        public void FailedOrBusyRequests_DoNotPolluteHistory()
        {
            // 忙碌期（切枪动作进行中）的滚轮/同槽请求被 ActionSystem 拒绝：无新意图、历史不变
            _arsenal.TryScrollSwap(-1f); // 发起 1→3（进入 SwitchWeapon 忙碌）
            int before = _intentEvents;  // =1
            Assert.That(before, Is.EqualTo(1));
            _arsenal.TryScrollSwap(-1f); // 忙碌期再滚 → TryStart 拒绝
            Assert.That(_intentEvents, Is.EqualTo(before), "忙碌期重复滚动不产生新意图");
            // 数字键选一个新槽（忙碌）同样被拒
            _arsenal.TrySelectSlot(3); // 目标==进行中目标槽：TryStart 忙碌拒绝
            Assert.That(_intentEvents, Is.EqualTo(before));
            CompleteSwitch(2f);
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(3), "忙碌期请求不破坏进行中的切枪");
        }

        [Test]
        public void DigitKey_BehaviourUnchanged_AndSingleIntent()
        {
            _arsenal.TrySelectSlot(3);
            Assert.That(_intentEvents, Is.EqualTo(1));
            Assert.That(_lastIntent, Is.EqualTo(3));
            CompleteSwitch(2f);
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(3));
        }

        [Test]
        public void IntentEvent_FiresOncePerResolvedSwitch_RegardlessOfSource()
        {
            // 三种入口到达同一目标时，每次解析只触发一次事件（线上转发与本地同源）
            _arsenal.TrySelectSlot(3);      // 数字键路径
            Assert.That(_intentEvents, Is.EqualTo(1));
            CompleteSwitch(2f);
            _arsenal.TryScrollSwap(1f);     // 滚轮路径（3 的上一把=1）
            Assert.That(_intentEvents, Is.EqualTo(2));
            Assert.That(_lastIntent, Is.EqualTo(1));
            CompleteSwitch(2f);
            _arsenal.TryQuickSwap();        // Q 路径（回 3）
            Assert.That(_intentEvents, Is.EqualTo(3));
            Assert.That(_lastIntent, Is.EqualTo(3));
        }
    }
}
