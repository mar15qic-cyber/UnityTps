using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 死亡倒地表现回归（审计 2026-09-15 §3）：
    /// ① 前倾姿态必须按实际地面探测贴地——模型原点是支点时前倾会让包围盒下沿穿入地面（实机"下半身埋地"）；
    /// ② 复活必须完整还原（位置 + 旋转 + 动画写入者 + 碰撞体），且重复死亡广播幂等；
    /// ③ 不硬编码统一高度、不整体抬高活人模型。
    /// </summary>
    public sealed class DeathVisualGroundingTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private const float BodyHeight = 1.8f;
        private readonly List<GameObject> _spawned = new();
        private GameObject _ground;
        private GameObject _root;
        private Transform _tpModel;
        private NetworkCombatAuthority _authority;

        private static void Destroy(GameObject go)
        {
            if (go != null) Object.DestroyImmediate(go);
        }

        /// <summary>搭建：地面（顶面 y=0）+ 玩家根 + TP_Model（模型原点在脚底，身体向上 1.8m）。</summary>
        private void Build(Vector3 modelLocalPosition, Quaternion modelLocalRotation)
        {
            _ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _ground.name = "Ground";
            _ground.transform.position = new Vector3(0f, -0.5f, 0f);
            _ground.transform.localScale = new Vector3(30f, 1f, 30f);
            _spawned.Add(_ground);

            _root = new GameObject("Player");
            _root.transform.position = Vector3.zero;
            _spawned.Add(_root);
            _authority = _root.AddComponent<NetworkCombatAuthority>();

            var model = new GameObject("TP_Model");
            model.transform.SetParent(_root.transform, false);
            model.transform.localPosition = modelLocalPosition;
            model.transform.localRotation = modelLocalRotation;
            _tpModel = model.transform;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            // 只留渲染（受击体另有 hitbox；同时避免自身碰撞体干扰地面探测的自层级过滤校验）
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(_tpModel, false);
            body.transform.localPosition = new Vector3(0f, BodyHeight * 0.5f, 0f);
            body.transform.localScale = new Vector3(0.5f, BodyHeight, 0.3f);
        }

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) Destroy(go);
            _spawned.Clear();
            _ground = null;
            _root = null;
            _tpModel = null;
            _authority = null;
        }

        private void InvokeDeath() =>
            typeof(NetworkCombatAuthority).GetMethod("ApplyDeathVisual", NonPublic).Invoke(_authority, null);

        private void InvokeRespawn() =>
            typeof(NetworkCombatAuthority).GetMethod("ResetDeathVisual", NonPublic).Invoke(_authority, null);

        /// <summary>姿态世界包围盒下沿（用 Renderer.bounds：非蒙皮网格即时可用）。</summary>
        private static float PoseBottom(Transform model)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any ? bounds.min.y : 0f;
        }

        /// <summary>倒地姿态必须落在地面之上（地面顶面 y=0）：旧实现只转不贴地，下沿穿入地面 ~0.25m。</summary>
        [Test]
        public void DeathPose_IsLiftedAboveGround_NotBuried()
        {
            Build(Vector3.zero, Quaternion.identity);

            InvokeDeath();

            Assert.That(_tpModel.localRotation.eulerAngles.x, Is.EqualTo(0f).Within(0.1f), "死亡动画不得旋转整个 TP 根节点");
            Assert.That(_tpModel.localPosition.y, Is.GreaterThan(0f), "贴地修正必须抬升姿态（脚为支点前倾会埋地）");
            Assert.That(PoseBottom(_tpModel), Is.GreaterThanOrEqualTo(-0.001f),
                "倒地姿态包围盒下沿不得穿入地面（实机'下半身埋地'）");
        }

        /// <summary>贴地高度必须来自本地实测地面，而不是硬编码的世界 0：站在高台上倒下要落在高台面上。</summary>
        [Test]
        public void DeathPose_OnRaisedSurface_RestsOnThatSurface()
        {
            Build(Vector3.zero, Quaternion.identity);
            _root.transform.position = new Vector3(0f, 5f, 0f);
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Platform";
            platform.transform.position = new Vector3(0f, 4.5f, 0f); // 顶面 y=5
            platform.transform.localScale = new Vector3(30f, 1f, 30f);
            _spawned.Add(platform);

            InvokeDeath();

            Assert.That(PoseBottom(_tpModel), Is.GreaterThanOrEqualTo(5f - 0.001f),
                "倒地姿态必须落在本地高台面（y=5）上，不得按硬编码地面高度落到 y=0");
            Assert.That(PoseBottom(_tpModel), Is.GreaterThan(4.9f), "姿态仍在高台之上");
        }

        /// <summary>
        /// 审计 2026-09-16 §5：贴地必须允许**有界下修**——旧实现只允许 lift&gt;0，已经浮空的尸体
        /// 永远落不下来（实测第二次倒地浮空 1.47m 后无法自愈）。低空死亡的尸体应落到支撑面上。
        /// </summary>
        [Test]
        public void DeathPose_AboveSupport_DropsToSupport()
        {
            Build(Vector3.zero, Quaternion.identity);
            _root.transform.position = new Vector3(0f, 1.5f, 0f); // 低空死亡（离地不足上限）

            InvokeDeath();

            Assert.That(PoseBottom(_tpModel), Is.EqualTo(0f).Within(0.05f),
                "低空死亡必须落到支撑面（有界下修），不得悬在半空");
        }

        /// <summary>
        /// 反向边界：修正量超出上限（探测异常/极高）时保持原姿态——有界修正绝不变成瞬移。
        /// </summary>
        [Test]
        public void DeathPose_FarAboveSupport_KeepsPose_BoundedAdjustRejected()
        {
            Build(Vector3.zero, Quaternion.identity);
            _root.transform.position = new Vector3(0f, 50f, 0f);
            var savedLocal = _tpModel.localPosition;

            InvokeDeath();

            Assert.That(_tpModel.localPosition, Is.EqualTo(savedLocal),
                "修正量超过上限时必须保持原姿态（不做无依据的大位移）");
            Assert.That(PoseBottom(_tpModel), Is.GreaterThan(40f), "姿态仍停在原处");
        }

        /// <summary>重复死亡广播幂等：不得用已抬升的姿态覆盖还原基准。</summary>
        [Test]
        public void RepeatedDeathBroadcast_IsIdempotent_AndRestoresExactly()
        {
            Build(new Vector3(0f, 0f, 0.341f), Quaternion.Euler(0f, 90f, 0f));
            // 还原基准 = 实际写入后的姿态（Transform 会对四元数做归一化，不能拿作者值当基准）
            var beforePosition = _tpModel.localPosition;
            var beforeRotation = _tpModel.localRotation;

            InvokeDeath();
            var lifted = _tpModel.localPosition;
            Assert.That(lifted.y, Is.GreaterThan(beforePosition.y), "首次倒地已贴地抬升");

            InvokeDeath(); // 乱序/重复广播
            Assert.That(_tpModel.localPosition, Is.EqualTo(lifted), "重复死亡广播不得二次变换（幂等）");
            Assert.That(PoseBottom(_tpModel), Is.GreaterThanOrEqualTo(-0.001f), "重复广播后仍不埋地");

            InvokeRespawn();
            Assert.That(_tpModel.localPosition, Is.EqualTo(beforePosition), "复活完整还原位置（含贴地抬升）");
            Assert.That(Quaternion.Angle(_tpModel.localRotation, beforeRotation), Is.LessThan(0.01f),
                "复活完整还原旋转");
        }

        /// <summary>连续两轮死亡→复活：每轮都贴地且都能精确还原（用户"最少三轮"要求的定向子集）。</summary>
        [Test]
        public void TwoDeathRespawnCycles_LiftAndRestoreEachRound()
        {
            Build(new Vector3(0.2f, 0f, 0.341f), Quaternion.Euler(0f, 90f, 0f));
            var beforePosition = _tpModel.localPosition;
            var beforeRotation = _tpModel.localRotation;

            for (int round = 1; round <= 2; round++)
            {
                InvokeDeath();
                Assert.That(PoseBottom(_tpModel), Is.GreaterThanOrEqualTo(-0.001f), $"第 {round} 轮倒地不埋地");
                Assert.That(_tpModel.localRotation.eulerAngles.x, Is.EqualTo(0f).Within(0.1f));

                InvokeRespawn();
                Assert.That(_tpModel.localPosition, Is.EqualTo(beforePosition), $"第 {round} 轮位置还原");
                Assert.That(Quaternion.Angle(_tpModel.localRotation, beforeRotation), Is.LessThan(0.01f),
                    $"第 {round} 轮旋转还原");
            }
        }

        /// <summary>
        /// 审计 2026-09-16 §4.1 + 2026-09-18 §4.1：死亡冻结的**范围**仍严格限定 TP_Model 子树
        /// （旧实现扫整棵玩家树，把 Owner 第一人称武器视图的 Animancer/IK 一起停掉且无重建路径 →
        /// "必须按一次 ADS 才恢复"）；但**冻结方式**改了：TP 的 Animator / Animancer 组件不再被停用，
        /// 而是经 IThirdPersonPoseLifecycle 暂停图——停用会触发 Animancer 的 DisableAction.Reset
        /// （Graph.Stop + Animator.Rebind），既毁掉尸体姿态，又让复活后图里没有任何状态在播
        /// （实机"重生陷地"的直接机制）。
        /// </summary>
        [Test]
        public void DeathPose_FreezesOnlyTpSubtree_LeavesFirstPersonAnimatorsRunning()
        {
            Build(Vector3.zero, Quaternion.identity);
            var tpAnimator = _tpModel.gameObject.AddComponent<Animator>();
            var fpView = new GameObject("FP_Weapon_View");
            fpView.transform.SetParent(_root.transform, false);
            _spawned.Add(fpView);
            var fpAnimator = fpView.AddComponent<Animator>();

            InvokeDeath();
            Assert.That(tpAnimator.enabled, Is.True,
                "TP 图组件由暂停冻结：停用即触发 Rebind（2026-09-18 §4.1 修正）");
            Assert.That(fpAnimator.enabled, Is.True,
                "第一人称写者不得被 TP 死亡冻结（它有自己的死亡/复活入口）");

            InvokeRespawn();
            Assert.That(tpAnimator.enabled, Is.True, "复活后写者状态与冻结前一致");
        }

        /// <summary>
        /// 审计 2026-09-16 §5 核心反例：旁边站着另一个玩家时，尸体不得把对方胶囊当成地面
        /// （实测第二次倒地多抬 1.47m = 选中了非承重体）。承重面探测必须排除所有玩家。
        /// </summary>
        [Test]
        public void DeathPose_NextToAnotherPlayer_IgnoresPlayerCapsule()
        {
            Build(Vector3.zero, Quaternion.identity);
            var other = new GameObject("OtherPlayer");
            other.transform.position = Vector3.zero;
            _spawned.Add(other);
            other.AddComponent<NetworkCombatAuthority>();
            var capsule = other.AddComponent<CharacterController>();
            capsule.height = 1.8f;
            capsule.radius = 0.35f;
            capsule.center = new Vector3(0f, 0.9f, 0f);

            InvokeDeath();

            Assert.That(PoseBottom(_tpModel), Is.EqualTo(0f).Within(0.05f),
                "承重面必须是地面：不得站在另一个玩家的胶囊上（浮空 1.47m 的直接原因）");
        }

        /// <summary>无地面可探测（悬空）时不得抛异常、不得凭猜测抬升。</summary>        [Test]
        public void NoGroundUnderPose_IsSafe_AndDoesNotGuess()
        {
            Build(Vector3.zero, Quaternion.identity);
            Destroy(_ground);
            _ground = null;
            _root.transform.position = new Vector3(0f, 200f, 0f);
            var savedLocal = _tpModel.localPosition;

            Assert.DoesNotThrow(() => InvokeDeath(), "探测不到地面时必须安全降级");
            Assert.That(_tpModel.localPosition, Is.EqualTo(savedLocal), "无地面依据时不做抬升");
            Assert.That(_tpModel.localRotation.eulerAngles.x, Is.EqualTo(0f).Within(0.1f), "无地面时仍保留作者根旋转");
        }
    }
}
