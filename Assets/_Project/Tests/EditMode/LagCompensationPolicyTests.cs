using System.Reflection;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>Day3 Phase 2 定向测试：回溯窗口换算、越窗裁剪、历史记录/恢复（服务器权威拒绝）。</summary>
    public sealed class LagCompensationPolicyTests
    {
        [SetUp]
        public void SetUp()
        {
            ServerLagCompensation.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            ServerLagCompensation.Enabled = true;
            if (ServerLagCompensation.Instance != null)
                Object.DestroyImmediate(ServerLagCompensation.Instance.gameObject);
        }

        // ---- 窗口换算 ----

        [Test]
        public void WindowTicks_200msAt30Hz_HasMargin()
        {
            // 200ms @30Hz = 6 tick + 4 余量
            Assert.That(LagCompensationPolicy.ComputeWindowTicks(200, 30), Is.EqualTo(10));
        }

        [Test]
        public void WindowTicks_200msAt60Hz()
        {
            Assert.That(LagCompensationPolicy.ComputeWindowTicks(200, 60), Is.EqualTo(16));
        }

        [Test]
        public void WindowTicks_InvalidInputs_FallToFloor()
        {
            Assert.That(LagCompensationPolicy.ComputeWindowTicks(0, 30), Is.EqualTo(2));
            Assert.That(LagCompensationPolicy.ComputeWindowTicks(200, 0), Is.EqualTo(2));
        }

        // ---- 目标裁剪：窗口内放行 / 过老裁到最老 / 超前裁到当前 ----

        [Test]
        public void Clamp_InsideWindow_PassesThrough()
        {
            Assert.That(LagCompensationPolicy.ClampTargetTick(95, 90, 100), Is.EqualTo(95u));
        }

        [Test]
        public void Clamp_TooOld_TruncatesToOldest()
        {
            Assert.That(LagCompensationPolicy.ClampTargetTick(10, 90, 100), Is.EqualTo(90u),
                "超窗请求裁剪到最老可用快照（禁止无限补偿高延迟玩家）");
        }

        [Test]
        public void Clamp_Future_TruncatesToCurrent()
        {
            Assert.That(LagCompensationPolicy.ClampTargetTick(150, 90, 100), Is.EqualTo(100u));
        }

        [Test]
        public void Clamp_NoHistory_Rejected()
        {
            Assert.That(LagCompensationPolicy.ClampTargetTick(50, 0, 100), Is.EqualTo(0u));
        }

        // ---- 历史记录 / 回滚应用 / 恢复当前位姿 ----

        private static (ServerLagCompensation manager, Transform root, Collider[] colliders) BuildHarness()
        {
            var managerGo = new GameObject("LagCompHarness");
            var manager = managerGo.AddComponent<ServerLagCompensation>();

            var rootGo = new GameObject("Player");
            var hitboxGo = new GameObject("Hitbox");
            hitboxGo.transform.SetParent(rootGo.transform, false);
            var collider = hitboxGo.AddComponent<BoxCollider>();
            var colliders = new Collider[] { collider };
            manager.RegisterPlayer(rootGo.transform, colliders);
            return (manager, rootGo.transform, colliders);
        }

        [Test]
        public void CaptureAndRewind_AppliesHistoricalPose_EndRestoresCurrent()
        {
            var (manager, root, colliders) = BuildHarness();
            var hitbox = colliders[0].transform;

            hitbox.position = new Vector3(0f, 0f, 0f);
            manager.Capture(1);
            hitbox.position = new Vector3(1f, 0f, 0f);
            manager.Capture(2);
            hitbox.position = new Vector3(2f, 0f, 0f);
            manager.Capture(3);

            // 回滚到 tick 1：hitbox 移动到历史位姿
            Assert.That(manager.TryBeginRewind(1), Is.True);
            Assert.That(hitbox.position, Is.EqualTo(new Vector3(0f, 0f, 0f)).Within(1e-4));

            // 判定完成恢复：回到回滚前的当前位姿（tick 3）
            manager.EndRewind();
            Assert.That(hitbox.position, Is.EqualTo(new Vector3(2f, 0f, 0f)).Within(1e-4));
        }

        [Test]
        public void Rewind_FutureRequest_IsRejected()
        {
            var (manager, root, colliders) = BuildHarness();
            var hitbox = colliders[0].transform;
            hitbox.position = new Vector3(7f, 0f, 0f);
            manager.Capture(1);
            hitbox.position = new Vector3(8f, 0f, 0f);
            manager.Capture(2);

            // 请求超前于当前：裁剪到当前最新快照（tick 2），不产生未来命中
            Assert.That(manager.TryBeginRewind(9999), Is.False);
            Assert.That(hitbox.position, Is.EqualTo(new Vector3(8f, 0f, 0f)).Within(1e-4));
            manager.EndRewind();

            // 过老目标（tick 0 / 早于最老历史）：裁剪到最老可用
            Assert.That(LagCompensationPolicy.ClampTargetTick(0, 1, 2), Is.EqualTo(1u));
        }

        [Test]
        public void Rewind_WhenDisabled_RejectedByServerAuthority()
        {
            var (manager, root, colliders) = BuildHarness();
            var hitbox = colliders[0].transform;
            hitbox.position = new Vector3(1f, 0f, 0f);
            manager.Capture(1);

            ServerLagCompensation.Enabled = false;
            Assert.That(manager.TryBeginRewind(1), Is.False, "开关关闭：服务器拒绝补偿，按即时位姿判定");
            Assert.That(hitbox.position, Is.EqualTo(new Vector3(1f, 0f, 0f)).Within(1e-4));
        }

        [Test]
        public void Capture_DropsHistoryOutsideWindow()
        {
            var (manager, root, colliders) = BuildHarness();
            var hitbox = colliders[0].transform;
            // 记录 30 个 tick，位置 = tick 号
            for (uint t = 1; t <= 30; t++)
            {
                hitbox.position = new Vector3(t, 0f, 0f);
                manager.Capture(t);
            }
            int window = manager.RewindWindowTicks;

            Assert.That(manager.TryBeginRewind(1), Is.False, "Old requests cannot consume storage margin");
            Assert.That(manager.TryBeginRewind(23), Is.False, "7 ticks at 30Hz exceeds 200ms");
            Assert.That(manager.TryBeginRewind(24), Is.True);
            Assert.That(hitbox.position.x, Is.EqualTo(24));
            manager.EndRewind();
            Assert.That(hitbox.position.x, Is.EqualTo(30));
        }

        [Test]
        public void Register_IsIdempotent()
        {
            var (manager, root, colliders) = BuildHarness();
            manager.RegisterPlayer(root, colliders);
            Assert.That(manager.RegisteredCount, Is.EqualTo(1));
        }

        // ---- 审计 2026-09-15 §5.2：父子 Transform 完整保存后统一回写 + 射手排除 ----

        private static (ServerLagCompensation manager, Transform root, Collider[] colliders, Transform hitbox)
            BuildParentChildHarness()
        {
            var managerGo = new GameObject("LagCompHarness_ParentChild");
            var manager = managerGo.AddComponent<ServerLagCompensation>();

            // 复刻运行时注册集：root 碰撞体（模拟 CharacterController——其 transform 就是玩家根）
            // + model/BodyHitbox 子碰撞体（父子共存）；PlayerNetworkAdapter.OnStartServer
            // 注册的是 GetComponentsInChildren<Collider>(true)，两者都在内。
            var rootGo = new GameObject("Player");
            var rootCollider = rootGo.AddComponent<BoxCollider>();
            var modelGo = new GameObject("TP_Model");
            modelGo.transform.SetParent(rootGo.transform, false);
            var hitboxGo = new GameObject("BodyHitbox");
            hitboxGo.transform.SetParent(modelGo.transform, false);
            var hitbox = hitboxGo.AddComponent<BoxCollider>();
            var colliders = new Collider[] { rootCollider, hitbox };
            manager.RegisterPlayer(rootGo.transform, colliders);
            return (manager, rootGo.transform, colliders, hitboxGo.transform);
        }

        [Test]
        public void Rewind_ParentChild_PreservesChildLocalPose()
        {
            var (manager, root, colliders, hitbox) = BuildParentChildHarness();

            // 历史：tick1 时父在 x=0；随后父移动到 x=5（子随父）
            root.position = new Vector3(0f, 0f, 0f);
            hitbox.localPosition = Vector3.zero;
            Physics.SyncTransforms();
            manager.Capture(1);
            root.position = new Vector3(5f, 0f, 0f);
            Physics.SyncTransforms();
            manager.Capture(2);

            Quaternion childLocalRotationBefore = hitbox.localRotation;

            Assert.That(manager.TryBeginRewind(1), Is.True);
            Assert.That(root.position.x, Is.EqualTo(0f).Within(1e-4), "父回溯到历史位姿");
            manager.EndRewind();

            Assert.That(root.position.x, Is.EqualTo(5f).Within(1e-4), "父恢复到回滚前位姿");
            // 审计 §5.2 核心断言：旧实现"边保存边回写"会把子的保存值写成被父回写污染的世界位姿，
            // 恢复后子的世界/局部位姿漂移；修复后子的局部位姿必须原样恢复
            Assert.That(hitbox.position, Is.EqualTo(root.position).Within(1e-4),
                "子 hitbox 世界位姿 = 父位姿 + 未漂移的局部偏移");
            Assert.That(hitbox.localRotation, Is.EqualTo(childLocalRotationBefore).Within(1e-5));
        }

        [Test]
        public void Rewind_ExcludeRoot_ShooterKeepsCurrentPose()
        {
            var (manager, root, colliders, hitbox) = BuildParentChildHarness();

            root.position = new Vector3(0f, 0f, 0f);
            hitbox.localPosition = Vector3.zero;
            Physics.SyncTransforms();
            manager.Capture(1);
            root.position = new Vector3(5f, 0f, 0f);
            Physics.SyncTransforms();
            manager.Capture(2);

            // 射手=该玩家根：开火期间不回溯（以当前权威姿态开火，AimOrigin 不得随历史移动）
            Assert.That(manager.TryBeginRewind(1, root), Is.True);
            Assert.That(root.position.x, Is.EqualTo(5f).Within(1e-4), "射手不参与回溯");
            manager.EndRewind();
            Assert.That(root.position.x, Is.EqualTo(5f).Within(1e-4));

            // 其他玩家（excludeRoot=null 的调用）仍正常回溯
            Assert.That(manager.TryBeginRewind(1, null), Is.True);
            Assert.That(root.position.x, Is.EqualTo(0f).Within(1e-4), "非排除路径照常回溯");
            manager.EndRewind();
        }

        [Test]
        public void Rewind_RepeatedCycles_RestoreIsExact()
        {
            // 审计 §5.2 建议的连续回溯测试：100 次回溯/恢复后父子位姿与回滚前完全一致
            var (manager, root, colliders, hitbox) = BuildParentChildHarness();
            root.position = new Vector3(3f, 1f, 2f);
            hitbox.localPosition = Vector3.zero;
            Physics.SyncTransforms();
            manager.Capture(1);
            manager.Capture(2);
            manager.Capture(3);

            Vector3 rootBefore = root.position;
            Quaternion rootRotationBefore = root.rotation;
            Vector3 hitboxWorldBefore = hitbox.position;

            for (int i = 0; i < 100; i++)
            {
                Assert.That(manager.TryBeginRewind(1), Is.True);
                manager.EndRewind();
            }

            Assert.That(root.position, Is.EqualTo(rootBefore).Within(1e-5));
            Assert.That(root.rotation, Is.EqualTo(rootRotationBefore).Within(1e-5));
            Assert.That(hitbox.position, Is.EqualTo(hitboxWorldBefore).Within(1e-5));
        }
    }
}
