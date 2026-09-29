using System.Collections.Generic;
using System.Reflection;
using Animancer;
using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 真实 TPAnimDriver + 真实 Animancer 图回归（2026-09-18 复核 R2/R3）。
    ///
    /// 复核 R3 指出的问题成立：TpPoseLifecycleTests 用的是接口探针，证明的是
    /// "NetworkCombatAuthority 会不会按顺序调用生命周期入口"，**不能**证明
    /// RecoverPoseAfterRespawn 内部的清缓存 / 装载 / 选态 / 求值结果。本文件用真实驱动器 +
    /// 真实 AnimancerComponent 图补上这一层（EditMode 显式 InitializeGraph，不依赖 Play 循环）。
    ///
    /// 复核 R2 是本文件的重点回归：旧实现每次复活都 LoadClips → 每次 new CartesianMixerState
    /// 注册进 Layer0，而旧 mixer 从不销毁 → 图随复活次数线性增长。现在同武器复用、换武器先销毁。
    ///
    /// 仍未覆盖（诚实标注，属运行环境限制）：骨骼世界姿态（hips/feet Y）与蒙皮求值结果——
    /// 需要 Play 循环里的真实 Animator 更新，列 PlayMode/实机待验，不用本文件冒充。
    /// </summary>
    public sealed class TpAnimDriverRespawnTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<Object> _temp = new();
        private GameObject _player;
        private AnimancerComponent _animancer;
        private TPAnimDriver _driver;
        private WeaponController _controller;
        private readonly List<AnimationClip> _clips = new();

        /// <summary>搭一套真实可初始化的 TP 动画栈：Animator + AnimancerComponent + 武器定义 + 驱动器。</summary>
        private void Build(bool withWalkClips)
        {
            _player = new GameObject("TP_Player");
            _temp.Add(_player);

            var animator = _player.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animancer = _player.AddComponent<AnimancerComponent>();
            _animancer.InitializeGraph(); // EditMode 不跑 Awake：显式建图（幂等）

            _controller = _player.AddComponent<WeaponController>();
            Set(_controller, "definition", NewWeaponDefinition(withWalkClips));

            _player.AddComponent<PlayerStateView>(); // 无 Locomotor → 恒 Idle（本地状态源）

            _driver = _player.AddComponent<TPAnimDriver>();
            Invoke(_driver, "Awake");
            Invoke(_driver, "Start"); // LoadClips + ApplyState(true)
        }

        private WeaponDefinition NewWeaponDefinition(bool withWalkClips)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            _temp.Add(def);
            var so = new SerializedObject(def);
            var locomotion = so.FindProperty("thirdPersonLocomotion");
            Assert.That(locomotion, Is.Not.Null, "WeaponDefinition.thirdPersonLocomotion 必须存在");
            locomotion.FindPropertyRelative("Idle").objectReferenceValue = Clip("TP_Idle");
            if (withWalkClips)
            {
                locomotion.FindPropertyRelative("WalkForward").objectReferenceValue = Clip("TP_WalkF");
                locomotion.FindPropertyRelative("WalkRight").objectReferenceValue = Clip("TP_WalkR");
                locomotion.FindPropertyRelative("WalkBackward").objectReferenceValue = Clip("TP_WalkB");
                locomotion.FindPropertyRelative("WalkLeft").objectReferenceValue = Clip("TP_WalkL");
                locomotion.FindPropertyRelative("RunForward").objectReferenceValue = Clip("TP_RunF");
                locomotion.FindPropertyRelative("RunRight").objectReferenceValue = Clip("TP_RunR");
                locomotion.FindPropertyRelative("RunBackward").objectReferenceValue = Clip("TP_RunB");
                locomotion.FindPropertyRelative("RunLeft").objectReferenceValue = Clip("TP_RunL");
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private AnimationClip Clip(string name)
        {
            var clip = new AnimationClip { name = name };
            _clips.Add(clip);
            _temp.Add(clip);
            return clip;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _temp)
                if (o != null) Object.DestroyImmediate(o);
            _temp.Clear();
            _clips.Clear();
            _player = null;
            _animancer = null;
            _driver = null;
            _controller = null;
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, NonPublic);
            Assert.That(m, Is.Not.Null, $"{target.GetType().Name}.{method} 必须存在");
            m.Invoke(target, null);
        }

        private static void Set(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, NonPublic);
            Assert.That(f, Is.Not.Null, $"field not found: {target.GetType().Name}.{field}");
            f.SetValue(target, value);
        }

        private void Freeze() => _driver.FreezePoseForDeath();
        private void Recover() => _driver.RecoverPoseAfterRespawn();

        [Test]
        public void ServerTicks_EvaluateBonesTwiceWithinOneRenderFrame_WithoutRenderAdvance()
        {
            Build(withWalkClips: false);
            var bone = new GameObject("ProbeBone");
            bone.transform.SetParent(_player.transform, false);
            var clip = _clips[0];
            clip.SetCurve("ProbeBone", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
            _driver.EvaluateServerAnimationTick(.1f);
            float first = bone.transform.localPosition.x;
            _driver.EvaluateServerAnimationTick(.1f);
            Assert.That(bone.transform.localPosition.x, Is.GreaterThan(first + .05f));
            float afterTicks = bone.transform.localPosition.x;
            Assert.That(_animancer.Graph.IsGraphPlaying, Is.False);
            Invoke(_driver, "Update");
            Assert.That(bone.transform.localPosition.x, Is.EqualTo(afterTicks).Within(.00001f));
            Freeze();
            Assert.That(_animancer.Graph.IsGraphPlaying, Is.False, "Death must retain tick ownership");
            Recover();
            Assert.That(_animancer.Graph.IsGraphPlaying, Is.False, "Respawn must retain tick ownership");
        }

        [Test]
        public void IdleDeathIdle_ReplaysLocomotionStateOnRealGraph()
        {
            Build(withWalkClips: true);
            Assert.That(_driver.LocomotionPlaying, Is.True, "前置：Start 后 Layer0 有在播状态");
            Assert.That(_driver.PoseFrozenForDeath, Is.False);

            Freeze();
            Assert.That(_driver.PoseFrozenForDeath, Is.True, "死亡：进入冻结闸");
            Assert.That(_animancer.Layers[1].Weight, Is.EqualTo(0f).Within(1e-4f),
                "动作层立即归零（复活不残留 Fire/Reload）");

            Recover();

            Assert.That(_driver.PoseFrozenForDeath, Is.False, "复活：解冻");
            Assert.That(_driver.CachedLocomotionState, Is.EqualTo(LocomotionState.Idle),
                "复活后缓存必须落在当前有效状态上（先清成无效值再重设），不得停在死亡前的旧值");
            Assert.That(_driver.LocomotionPlaying, Is.True,
                "站桩（无输入、状态值仍是 Idle）也必须恢复有效播放——旧实现这里恒 false");
        }

        [Test]
        public void RepeatedRevives_DoNotGrowLayer0Graph()
        {
            // R2 核心：旧实现每复活一次净增一套 walk/run mixer，直到图销毁
            Build(withWalkClips: true);
            // The death clip is cached once on first use; repeated revives must not grow it.
            Freeze(); Recover();
            int creationsAfterStart = _driver.LocomotionMixerCreations;
            int statesAfterStart = _driver.LocomotionLayerStateCount;
            Assert.That(creationsAfterStart, Is.EqualTo(2), "前置：walk + run 两套混合器");

            for (int i = 0; i < 20; i++)
            {
                Freeze();
                Recover();
            }

            Assert.That(_driver.LocomotionMixerCreations, Is.EqualTo(creationsAfterStart),
                "同一武器连续 20 次复活不得再创建混合器（旧实现 = 40）");
            Assert.That(_driver.LocomotionLayerStateCount, Is.EqualTo(statesAfterStart),
                "Layer0 子状态数不随复活次数增长");
            Assert.That(_driver.LocomotionPlaying, Is.True, "回收后仍正常播放");
        }

        [Test]
        public void WeaponChange_DestroysOldMixersInsteadOfStacking()
        {
            Build(withWalkClips: true);
            int statesWithClips = _driver.LocomotionLayerStateCount;

            // 换到"无 walk/run"的武器：应销毁旧 mixer（Layer0 只剩 Idle 一个状态）
            Set(_controller, "definition", NewWeaponDefinition(withWalkClips: false));
            Recover();

            Assert.That(_driver.LocomotionLayerStateCount, Is.LessThan(statesWithClips),
                "换武器时旧方向混合器必须从图上摘除（Stop/权重归零不等于移除）");
            Assert.That(_driver.LocomotionMixerCreations, Is.EqualTo(2), "无 walk/run 集不再新建 mixer");

            // 换回带 walk/run 的武器：新建两套（增长只由 clip 集变化驱动）
            Set(_controller, "definition", NewWeaponDefinition(withWalkClips: true));
            Recover();
            Assert.That(_driver.LocomotionMixerCreations, Is.EqualTo(4), "每次 clip 集变化恰好建 2 套");
        }

        [Test]
        public void DoubleFreeze_AndDoubleRecover_AreIdempotentOnRealDriver()
        {
            Build(withWalkClips: false);

            Freeze();
            Freeze();
            Recover();
            Recover();

            Assert.That(_driver.PoseFrozenForDeath, Is.False);
            Assert.That(_driver.LocomotionPlaying, Is.True);
            Assert.That(_animancer.IsGraphInitialized, Is.True, "重复生命周期不得把图弄坏");
        }

        [Test]
        public void LegacyDisablePath_WouldLeaveNothingPlaying_DocumentsTheMechanism()
        {
            // 反例锚点：证明"停用组件"不等于"冻结"。prefab 的 _ActionOnDisable=3（Reset）会
            // Graph.Stop + Animator.Rebind + PauseGraph；OnEnable 只 UnpauseGraph → 复活后无状态在播。
            // 这正是修复前"重生陷地"的机制，也解释为什么修复改用 PauseGraph。
            Build(withWalkClips: false);
            Assert.That(_driver.LocomotionPlaying, Is.True);

            _animancer.ActionOnDisable = AnimancerComponent.DisableAction.Reset;
            Invoke(_animancer, "OnDisable");
            Invoke(_animancer, "OnEnable");

            Assert.That(_animancer.IsGraphInitialized, Is.True, "图仍在（组件重新启用只恢复图，不恢复状态）");
            Assert.That(_driver.LocomotionPlaying, Is.False,
                "Reset 之后 Layer0 无在播状态：这条同时证明新诊断不是假阳性机器（旧 ChildCount>0 这里会给 true）");
        }
    }
}
