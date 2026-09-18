using System.Reflection;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// BodyHitbox 几何契约回归。
    ///
    /// 契约演变（审计者必读，两件事都成立、不是覆盖关系）：
    /// ① Day4 残余审计 P0-1：hitbox **原点**必须对齐玩家逻辑根，纵向半身高由 <c>center.y</c> 提供。
    ///    当时修的是"原点对齐根+0.9 又叠加 center.y=0.9 → 胶囊中心跑到 +1.8m，腿/下躯干漏判"。
    ///    本文件保留这条（纵向断言），它没有被推翻。
    /// ② 2026-09-18 第三轮（排查报告 §3.1）：**横向/前后中心必须贴合可见 TP 模型**，不能落在逻辑根上。
    ///    实测：prefab 作者偏移 TP_Model.localPosition.z = 0.341；角色 SkinnedMeshRenderer 世界包围盒
    ///    中心换算到玩家根空间 z = 0.333；身体前后范围 z ∈ [0.076, 0.590]。
    ///    旧契约"世界中心 = 根 + (0,0.9,0)"因此让胶囊轴线落在身体后方 0.33m 处：
    ///    身体后方约 0.43m 的**空气**可被打中（用户实机"没打到模型也掉血 + 浮空血花"），
    ///    而胸口最前约 0.24m 反而打不中。旧契约的这条断言已按新语义改写，并在下方用例里显式锁死差异。
    /// ③ 2026-09-19 第四轮（实机 03:50 视频取证）：钉扎的**参照系**从"当帧位姿"改为"作者基准"。
    ///    旧实现 InverseTransformPoint(当帧) 在 EnsureBodyHitbox 被重入（池化复用 OnStartNetwork）
    ///    且 TP_Model 带瞬态姿态（死亡前倾 85°+贴地抬升 ≈0.4m）时，把瞬态烤进受击体局部位置，
    ///    此后受击体与可见身体错位 0.4~0.8m——实机"瞄模型后方空气仍掉血 + 血雾悬空"的直接机制。
    ///    新不变量：**作者位姿下中心=根∘标定中心；模型偏离基准（视觉平滑/死亡）时中心随身体**。
    ///    "原点=根"只在基准位姿那一刻可定义——模型相对根的运动是常态（平滑/死亡），"跟根"不可维持，
    ///    "跟身体"才是可维持且正确的不变量。旧用例 ②/⑥ 的"任意模型位姿下原点仍=根"断言按此推翻改写。
    ///
    /// EditMode 要点：AddComponent 不跑 Awake；改变换后 Physics.SyncTransforms()；
    /// 受击体是 trigger，射线查询必须显式 QueryTriggerInteraction.Collide（与 CombatResolver 同口径）。
    /// </summary>
    public sealed class BodyHitboxAlignmentTests
    {
        /// <summary>实测标定值（与 PlayerNetworkAdapter.bodyHitboxCenter 一致）。</summary>
        private static readonly Vector3 CalibratedCenter = new Vector3(0f, 0.9f, 0.333f);

        /// <summary>可见身体前后范围（玩家根空间 z，实测）。</summary>
        private const float BodyBackEdge = 0.076f;
        private const float BodyFrontEdge = 0.590f;

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        private (PlayerNetworkAdapter adapter, Transform hitbox, CapsuleCollider collider, Transform model) Build(
            Vector3 rootPos, Vector3 modelLocalPos, float modelYaw, Vector3 modelScale)
        {
            _root = new GameObject("ProbePlayer");
            _root.transform.position = rootPos;
            var adapter = _root.AddComponent<PlayerNetworkAdapter>();
            var model = new GameObject("TP_Model");
            model.transform.SetParent(_root.transform, false);
            model.transform.localPosition = modelLocalPos;
            model.transform.localRotation = Quaternion.Euler(0f, modelYaw, 0f);
            model.transform.localScale = modelScale;
            model.AddComponent<DamageableTarget>();

            var method = typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "EnsureBodyHitbox missing");
            method.Invoke(adapter, null);

            var hitbox = model.transform.Find("BodyHitbox");
            Assert.That(hitbox, Is.Not.Null, "BodyHitbox 必须被创建");
            return (adapter, hitbox, hitbox.GetComponent<CapsuleCollider>(), model.transform);
        }

        /// <summary>期望世界中心 = 根位置 + 模型朝向系下的标定中心偏移。</summary>
        private static Vector3 ExpectedWorldCenter(Vector3 rootPos, float modelYaw)
            => rootPos + Quaternion.Euler(0f, modelYaw, 0f) * CalibratedCenter;

        private static void AssertVerticalCoversFeetToHead(Transform hitbox, CapsuleCollider collider, Vector3 rootPos)
        {
            // EditMode 下 collider.bounds 依赖物理世界懒同步——用 TransformPoint 纯数学断言
            var half = Vector3.up * (collider.height * 0.5f * hitbox.lossyScale.y);
            var center = hitbox.TransformPoint(collider.center);
            Assert.That((center - half).y, Is.EqualTo(rootPos.y).Within(0.01f), "胶囊下端=脚底（腿部覆盖）");
            Assert.That((center + half).y, Is.EqualTo(rootPos.y + 1.8f).Within(0.01f), "胶囊上端=头顶");
        }

        [Test]
        public void Hitbox_WorldCenter_SitsOnVisibleBody_NotLogicalRoot()
        {
            var rootPos = new Vector3(3f, 5f, -7f);
            var (_, hitbox, collider, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);

            var worldCenter = hitbox.TransformPoint(collider.center);
            var expected = ExpectedWorldCenter(rootPos, 0f);
            Assert.That(Vector3.Distance(worldCenter, expected), Is.LessThan(0.01f),
                "标定后：胶囊中心应落在实测身体中心（根 + (0,0.9,0.333)）");

            // 显式锁死与旧契约的差异——旧契约正是"模型外空处受击"的几何根因
            var legacyCenter = rootPos + new Vector3(0f, 0.9f, 0f);
            Assert.That(Vector3.Distance(worldCenter, legacyCenter), Is.GreaterThan(0.3f),
                "不得退回旧契约『世界中心=根+0.9、z 偏移为 0』（那会把胶囊放到身体后方 0.33m）");
        }

        [Test]
        public void Hitbox_Offset_FollowsBodyFacing_AndStillOnBody()
        {
            // 2026-09-19 第四轮改写：真实运行期"身体转向"= 根 yaw 变化（TP_Model 作者基准旋转为
            // 恒等，朝向由根承载，见 PinTpModelToRootForServer/远端平滑的目标位姿定义）。
            // 根转向时标定偏移随身体朝向一起转（受击体跟着可见身体，不钉死世界方向）。
            foreach (var yaw in new[] { 0f, 90f, 180f, -90f })
            {
                var rootPos = new Vector3(-2f, 0f, 4f);
                var (_, hitbox, collider, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
                _root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                var worldCenter = hitbox.TransformPoint(collider.center);
                Assert.That(Vector3.Distance(worldCenter, ExpectedWorldCenter(rootPos, yaw)),
                    Is.LessThan(0.01f), $"yaw={yaw}：中心应随身体朝向旋转");
                AssertVerticalCoversFeetToHead(hitbox, collider, rootPos);
            }
        }

        [Test]
        public void Hitbox_PinIsModelAnchored_AuthorBaseYawKeepsAuthorContract()
        {
            // 作者基准带偏航（资产侧授权姿态）时：钉扎公式保证"作者位姿下中心=根∘标定中心"
            // 对任意基准旋转恒成立（这是钉扎公式 derives 出的契约本体），且中心在模型系内为
            // 常量（不随模型后续偏离基准而漂）。
            var rootPos = new Vector3(-2f, 0f, 4f);
            const float baseYaw = 90f;
            var (_, hitbox, collider, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), baseYaw, Vector3.one);

            var worldCenter = hitbox.TransformPoint(collider.center);
            Assert.That(Vector3.Distance(worldCenter, rootPos + CalibratedCenter), Is.LessThan(0.01f),
                "作者位姿下中心=根∘标定中心，对任意基准旋转恒成立（钉扎公式契约）");
        }

        [Test]
        public void Hitbox_VerticalContract_UnchangedUnderNonZeroModelPlacementAndScale()
        {
            // Day4 P0-1 的纵向结论保留：不得出现双重 0.9（腿/下躯干漏判的原始症状）
            var rootPos = new Vector3(-2f, 1.5f, 4f);
            var (_, hitbox, collider, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, new Vector3(1f, 1f, 1f));
            AssertVerticalCoversFeetToHead(hitbox, collider, rootPos);
            Assert.That(collider.center.y, Is.EqualTo(0.9f).Within(1e-4f), "半身高偏移仍只算一次");
        }

        [Test]
        public void HittableVolume_IsAnchoredOnVisibleBody_AirGapBehindAndFrontBothWithinProxyTolerance()
        {
            // 症状回归锚点。修复前胶囊覆盖 z ∈ [-0.35, +0.35]，而实测身体在 [0.076, 0.590]：
            // 身体后方 0.426m 空气可被打中（= 用户看到的"没打到模型也掉血 + 浮空血花/弹孔"），
            // 胸口最前 0.24m 反而打不中。标定后胶囊覆盖 z ∈ [-0.017, +0.683]。
            var rootPos = Vector3.zero;
            Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // ① 沿身体中轴从正后方向前打：命中点 = 可受击体积的最后端
            var behindOrigin = rootPos + new Vector3(0f, 1.2f, -2f);
            Assert.That(Physics.Raycast(behindOrigin, Vector3.forward, out var backHit, 20f, ~0,
                QueryTriggerInteraction.Collide), Is.True, "受击体仍须可被射线命中（trigger + 显式 Collide）");
            float airBehindBody = BodyBackEdge - backHit.point.z;
            Assert.That(airBehindBody, Is.LessThanOrEqualTo(0.12f),
                $"身体后方可命中空气厚度须收敛到代理容差（修复前 0.426m，本次实测 {airBehindBody:F3}m）");

            // ② 从正前方向后打：命中点必须到达身体前缘附近
            var frontOrigin = rootPos + new Vector3(0f, 1.2f, 2.5f);
            Assert.That(Physics.Raycast(frontOrigin, Vector3.back, out var frontHit, 20f, ~0,
                QueryTriggerInteraction.Collide), Is.True, "胸口最前段必须可命中（修复前最远只到 z=0.35）");
            Assert.That(frontHit.point.z, Is.GreaterThanOrEqualTo(BodyFrontEdge - 0.12f),
                $"命中点应到达身体前缘附近（本次 z={frontHit.point.z:F3}，身体前缘 {BodyFrontEdge:F3}）");

            // ③ 侧向经过身体中心：仍正常命中（受击体没有因为标定而变窄到漏掉躯干）
            Assert.That(HitSurface(rootPos + new Vector3(2.5f, 1.2f, CalibratedCenter.z), Vector3.left), Is.True);
        }

        [Test]
        public void Raycasts_LegTorsoHeadHit_AboveHeadMisses()
        {
            var rootPos = new Vector3(5f, 0f, -3f);
            var (_, _, _, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // 沿标定后的胶囊轴线（根 + z 0.333）横向射击：腿/躯干/头都必须命中
            foreach (var height in new[] { 0.3f, 0.9f, 1.7f })
            {
                var shooter = rootPos + new Vector3(10f, height, CalibratedCenter.z);
                Assert.That(HitSurface(shooter, Vector3.left), Is.True, $"高度 {height}m 的射线必须命中 hitbox");
            }

            var above = rootPos + new Vector3(10f, 2.6f, CalibratedCenter.z);
            Assert.That(HitSurface(above, Vector3.left), Is.False,
                "头顶上方（+2.6m）不得命中（修复前中心在 +1.8m 时该区域可命中）");
        }

        [Test]
        public void Hitbox_RerunDoesNotRebakeLivePose_CenterRidesBody()
        {
            var rootPos = new Vector3(1f, 2f, 3f);
            var (adapter, hitbox, collider, model) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            var pinBefore = hitbox.localPosition;

            // 模型被视觉平滑/动画移动+转向后重入 EnsureBodyHitbox（池化复用 OnStartNetwork 语义）：
            // 钉扎=模型系常量，不得把当帧位姿烤进去；中心随身体走（模型锚定）。
            model.localPosition = new Vector3(0.4f, 0.2f, 0.6f);
            model.localRotation = Quaternion.Euler(0f, 35f, 0f);
            var method = typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(adapter, null);

            Assert.That(hitbox.localPosition, Is.EqualTo(pinBefore).Within(1e-5f),
                "重入不得改变钉扎（第四轮契约：模型系常量，与当帧姿态无关）");
            var worldCenter = hitbox.TransformPoint(collider.center);
            var centerInModel = model.InverseTransformPoint(worldCenter);
            Assert.That(Vector3.Distance(centerInModel, new Vector3(0f, 0.9f, -0.008f)), Is.LessThan(0.01f),
                "模型系中心=身体标定常量(0,0.9,-0.008)：中心随身体走，不回退根锚定");
        }

        [Test]
        public void Hitbox_SurvivesPooledReuseDuringDeathPose_NoTransientBakedIn()
        {
            // 2026-09-19 实机取证回归（本轮根因）：死亡表现 applied 后 TP_Model = 前倾 85° +
            // 贴地抬升 0.402m（NetworkCombatAuthority.ApplyDeathVisual 日志 grounded=(0,0,0.34)
            // lifted=0.402），池化复用触发 OnStartNetwork → EnsureBodyHitbox 重入。
            // 旧实现 InverseTransformPoint(倒地位姿) 把瞬态烤进钉扎，复位后受击体与可见身体
            // 错位 ≈0.8m（根空间中心 (0,0.53,1.05)）——实机"瞄模型后方空气仍掉血 + 血雾悬空"。
            var rootPos = new Vector3(-1f, 0f, 2f);
            var (adapter, hitbox, collider, model) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            model.localRotation = Quaternion.Euler(85f, 0f, 0f);
            model.localPosition = new Vector3(0f, 0.402f, 0.341f);
            var method = typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(adapter, null);

            // 死亡表现复位（ResetDeathVisual → 还原作者位姿 + RepinBodyHitboxAfterRestore）
            model.localRotation = Quaternion.identity;
            model.localPosition = new Vector3(0f, 0f, 0.341f);

            var worldCenter = hitbox.TransformPoint(collider.center);
            Assert.That(Vector3.Distance(worldCenter, rootPos + CalibratedCenter), Is.LessThan(0.01f),
                "死亡期间被重入的钉扎不得污染：复位后中心必须回到根∘标定中心");
        }

        /// <summary>与 CombatResolver 同口径的受击查询（受击体是 trigger，必须显式 Collide）。</summary>
        private static bool HitSurface(Vector3 origin, Vector3 direction)
            => Physics.Raycast(origin, direction.normalized, 20f, ~0, QueryTriggerInteraction.Collide);
    }
}
