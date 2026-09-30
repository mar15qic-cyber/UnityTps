using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Animation;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// TP 死亡/复活生命周期回归（2026-09-18 审计 §4、§7 用例 1–2 的可静态验证部分）。
    ///
    /// 被测根因链（实机"重生后第三人称模型陷地"）：
    /// prefab 里 AnimancerComponent `_ActionOnDisable=3`（DisableAction.Reset）在停用时执行
    /// Graph.Stop + Animator.Rebind + PauseGraph，而 OnEnable 只 UnpauseGraph；TPAnimDriver 又以
    /// _currentState 缓存做"状态没变就不重播"。于是 Idle→死亡→Idle 之后图里没有任何状态在播、
    /// 模型停在 Rebind 出来的骨架姿态上，站桩永远不恢复。
    /// 修复：死亡用**暂停图**冻结（不停用 Animator/Animancer），复活走显式
    /// <see cref="IThirdPersonPoseLifecycle.RecoverPoseAfterRespawn"/> 重建，且 TP 根位姿在
    /// 任何可能 Rebind 的动作**之前**保存。
    ///
    /// 注：真实 Animancer 图求值（复活后 Layer0 确实播放并出骨骼姿态）需要 Play 循环，
    /// EditMode 不做图求值——该部分留作 PlayMode/实机矩阵待验项，本文件不冒充。
    /// </summary>
    public sealed class TpPoseLifecycleTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _spawned = new();
        private GameObject _root;
        private Transform _tpModel;
        private NetworkCombatAuthority _authority;

        /// <summary>TP 生命周期探针：记录调用次数，并可模拟"冻结动作把 TP 根改脏"（Rebind 的副作用形态）。</summary>
        private sealed class TpLifecycleProbe : MonoBehaviour, IThirdPersonPoseLifecycle
        {
            public int FreezeCalls;
            public int RecoverCalls;
            public Transform DirtyOnFreeze;

            public bool DeathPoseComplete => true;
            public void PlayDeathPose(float elapsedSeconds) => FreezePoseForDeath();
            public void FreezePoseForDeath()
            {
                FreezeCalls++;
                if (DirtyOnFreeze != null)
                {
                    DirtyOnFreeze.localPosition += new Vector3(0f, 1.47f, 0f); // 模拟贴地误抬/Rebind
                    DirtyOnFreeze.localRotation = Quaternion.Euler(0f, 90f, 0f);
                }
            }

            public void RecoverPoseAfterRespawn() => RecoverCalls++;
        }

        /// <summary>名字命中姿态写者清单（含 "TPAim"）的假写者：验证停用/精确还原原始状态。</summary>
        private sealed class TPAimDriverProbe : MonoBehaviour { }

        [SetUp]
        public void SetUp()
        {
            Physics.autoSyncTransforms = true;
            _root = new GameObject("Player");
            _spawned.Add(_root);
            _authority = _root.AddComponent<NetworkCombatAuthority>();
            _authority.TestOverrideSpawnedForDeathVisual = true; // EditMode 构造不出已生成 NetworkObject

            var model = new GameObject("TP_Model");
            model.transform.SetParent(_root.transform, false);
            model.transform.localPosition = new Vector3(0f, 0f, 0.341f); // 与正式 prefab 同形（作者偏移）
            _tpModel = model.transform;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
            _root = null;
            _tpModel = null;
            _authority = null;
        }

        private TpLifecycleProbe AddTpLifecycleProbe(bool dirtyTpRootOnFreeze = false)
        {
            var probe = _tpModel.gameObject.AddComponent<TpLifecycleProbe>();
            if (dirtyTpRootOnFreeze) probe.DirtyOnFreeze = _tpModel;
            return probe;
        }

        private void InvokeDeath() => Invoke("ApplyDeathVisual");
        private void InvokeRespawn() => Invoke("ResetDeathVisual");

        private void Invoke(string method)
        {
            var m = typeof(NetworkCombatAuthority).GetMethod(method, NonPublic);
            Assert.That(m, Is.Not.Null, $"{method} 必须存在（死亡表现私有入口）");
            m.Invoke(_authority, null);
        }

        [Test]
        public void DeathFreezesTpThroughLifecycle_RespawnRecoversOnce()
        {
            var probe = AddTpLifecycleProbe();

            InvokeDeath();
            Assert.That(probe.FreezeCalls, Is.EqualTo(1), "死亡必须走 TP 生命周期冻结入口");
            Assert.That(probe.RecoverCalls, Is.EqualTo(0));

            InvokeRespawn();
            Assert.That(probe.RecoverCalls, Is.EqualTo(1), "复活必须显式重建，而不是只还原 enabled");
        }

        [Test]
        public void TpRootPoseSavedBeforeFreeze_RespawnRestoresAuthorBaseline()
        {
            // §4.1 顺序修正的核心断言：冻结动作若把 TP 根改脏（旧实现"先停写者后保存"就会保存脏值），
            // 复活后必须仍然是**死亡前**的作者位姿。
            var authorPosition = _tpModel.localPosition;
            var authorRotation = _tpModel.localRotation;
            AddTpLifecycleProbe(dirtyTpRootOnFreeze: true);

            InvokeDeath();
            Assert.That(_tpModel.localPosition, Is.Not.EqualTo(authorPosition), "前置：冻结确实改过 TP 根");

            InvokeRespawn();
            Assert.That(_tpModel.localPosition, Is.EqualTo(authorPosition), "复活必须还原到死亡前的位姿");
            Assert.That(Quaternion.Angle(_tpModel.localRotation, authorRotation), Is.LessThan(0.01f));
        }

        [Test]
        public void RepeatedDeathAndRespawnBroadcasts_AreIdempotent()
        {
            var probe = AddTpLifecycleProbe();

            InvokeDeath();
            InvokeDeath(); // 乱序/重复的死亡广播
            Assert.That(probe.FreezeCalls, Is.EqualTo(1), "重复死亡广播不得二次冻结（会覆盖保存的基准）");

            InvokeRespawn();
            InvokeRespawn(); // 迟到的第二次复活广播
            Assert.That(probe.RecoverCalls, Is.EqualTo(1), "复活重建一次即可：不得每次广播都重建");
        }

        [Test]
        public void SecondDeathAfterRespawn_RefreezesAndRecoversAgain()
        {
            var probe = AddTpLifecycleProbe();

            InvokeDeath();
            InvokeRespawn();
            InvokeDeath();
            InvokeRespawn();

            Assert.That(probe.FreezeCalls, Is.EqualTo(2), "复活后重新武装：下一次死亡照常冻结");
            Assert.That(probe.RecoverCalls, Is.EqualTo(2));
        }

        [Test]
        public void NonGraphWritersAreDisabledDuringDeath_AndRestoredToOriginalEnabledState()
        {
            // §4.1：原本就禁用的写者不得被复活"打开"
            var enabledAim = _tpModel.gameObject.AddComponent<TPAimDriverProbe>();
            var disabledAim = _tpModel.gameObject.AddComponent<TPAimDriverProbe>();
            disabledAim.enabled = false;

            InvokeDeath();
            Assert.That(enabledAim.enabled, Is.False, "TP 骨骼写者死亡期间停用");
            Assert.That(disabledAim.enabled, Is.False);

            InvokeRespawn();
            Assert.That(enabledAim.enabled, Is.True, "还原原始状态");
            Assert.That(disabledAim.enabled, Is.False, "死亡前本就关闭的不得被打开");
        }

        [Test]
        public void AnimatorIsNotDisabledBecauseFreezeUsesGraphPause()
        {
            // §4.1：停用 Animator/Animancer 会触发 DisableAction.Reset 的 Rebind——冻结改由生命周期
            // 暂停图承担，因此 Animator 必须保持启用（这条断言就是"不再用停用模拟冻结"的证据）。
            var animator = _tpModel.gameObject.AddComponent<Animator>();

            InvokeDeath();
            Assert.That(animator.enabled, Is.True, "TP Animator 不得被停用（停用即 Rebind 风险）");

            InvokeRespawn();
            Assert.That(animator.enabled, Is.True);
        }

        [Test]
        public void LifecycleImplementersOutsideTpSubtree_AreNotFrozen()
        {
            // §4.1 范围红线：冻结只限 TP_Model 子树，不得误停 Owner FP 图
            var outsideGo = new GameObject("FP_View");
            outsideGo.transform.SetParent(_root.transform, false);
            _spawned.Add(outsideGo);
            var outside = outsideGo.AddComponent<TpLifecycleProbe>();
            var inside = AddTpLifecycleProbe();

            InvokeDeath();
            InvokeRespawn();

            Assert.That(inside.FreezeCalls, Is.EqualTo(1));
            Assert.That(outside.FreezeCalls, Is.EqualTo(0), "TP 子树外的实现者不得被死亡冻结波及");
            Assert.That(outside.RecoverCalls, Is.EqualTo(0));
        }

        [Test]
        public void RespawnBroadcastWithoutPriorDeath_TouchesNothing()
        {
            var authorPosition = _tpModel.localPosition;
            var probe = AddTpLifecycleProbe();

            InvokeRespawn(); // 迟到的复活广播（本实例从未死亡）

            Assert.That(probe.RecoverCalls, Is.EqualTo(0), "未冻结过就不重建");
            Assert.That(_tpModel.localPosition, Is.EqualTo(authorPosition),
                "不得用默认值覆盖从未改过的 TP 根位姿");
        }
    }
}
