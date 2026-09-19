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
    /// 契约演变（审计者必读，各代都成立、后位不推翻前位的已验证部分）：
    /// ① Day4 残余审计 P0-1：受击覆盖**脚底→头顶**、纵向不双重计偏移。本轮以"躯干下端=脚底
    ///    （y=0）、头上端=头顶（y=1.80）、两胶囊 y=1.40 无缝重叠"延续，未推翻。
    /// ② 2026-09-18 第三轮：**前后中心必须贴合可见 TP 模型**（z=0.333），不能落在逻辑根上。
    ///    本轮双胶囊 z 全部沿用 0.333，未推翻。
    /// ③ 2026-09-19 第四轮：钉扎参照系从"当帧位姿"改为"作者基准"（池化复用+死亡瞬态不再烤入）。
    ///    本轮未推翻。
    /// ④ 2026-09-19 第五轮（用户实机 14:16 视频 + 拍板"动工1"）：**单胶囊 0.35 半径升级为
    ///    躯干+头部复合受击体**。第三轮只标定了中心、半径 0.35 维持旧值——头部可视半宽 ≈0.12m
    ///    的前提下，头旁 0.2m+ 的"看得见的空气"一直在胶囊内被判定命中（帧 h_018/h_032：
    ///    瞄头旁后方空气掉血+血雾悬空）。躯干 r=0.26（持枪躯干可视半宽 ≈0.22~0.26）、
    ///    头部 r=0.15（头盔外廓 ≈0.12~0.15）：躯干前后缘 0.073/0.593 与实测身体轮廓
    ///    0.076/0.590 几乎重合（旧 0.35 的前后可视空气各 ≈0.09m 归零）。
    ///    旧"单胶囊 r=0.35"的隐含契约（头旁/躯干旁 0.25~0.30m 空气可命中）就此推翻，
    ///    并以侧向 MISS 用例显式锁死。
    /// ⑤ 2026-09-19 F11 轮（四日审计）：胶囊端点是球冠——④的两胶囊在 y=1.40 仅极点相切，
    ///    横射 y=1.40 漏过两体、y=1.39 半宽 0.071m。躯干总高 1.40→1.50（centerY .75）：
    ///    接缝带 1.40~1.50 有 .15~.21m 有限宽度；头旁/躯干旁空气 MISS 反例原样保留。
    ///    可见蒙皮轮廓逐枪实测仍列实机待验。
    ///
    /// EditMode 要点：AddComponent 不跑 Awake；改变换后 Physics.SyncTransforms()；
    /// 受击体是 trigger，射线查询必须显式 QueryTriggerInteraction.Collide（与 CombatResolver 同口径）。
    /// </summary>
    public sealed class BodyHitboxAlignmentTests
    {
        /// <summary>躯干/头部胶囊的序列化参数（与 PlayerNetworkAdapter F11 轮字段一致：
        /// 躯干上延 1.50 与头部 1.40..1.80 形成 0.10m 有限重叠带）。</summary>
        private static readonly Vector3 TorsoCenter = new Vector3(0f, 0.75f, 0.333f);
        private const float TorsoRadius = 0.26f;
        private const float TorsoHeight = 1.50f;
        private static readonly Vector3 HeadCenter = new Vector3(0f, 1.60f, 0.333f);
        private const float HeadRadius = 0.15f;
        private const float HeadHeight = 0.40f;

        /// <summary>可见身体前后范围（玩家根空间 z，第三轮实测）。</summary>
        private const float BodyBackEdge = 0.076f;
        private const float BodyFrontEdge = 0.590f;

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        private (PlayerNetworkAdapter adapter, Transform hitbox, CapsuleCollider torso, CapsuleCollider head, Transform model)
            Build(Vector3 rootPos, Vector3 modelLocalPos, float modelYaw, Vector3 modelScale)
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
            var capsules = hitbox.GetComponents<CapsuleCollider>();
            Assert.That(capsules.Length, Is.GreaterThanOrEqualTo(2), "复合受击体应含躯干+头部双胶囊");
            return (adapter, hitbox, capsules[0], capsules[1], model.transform);
        }

        private static void InvokeEnsureBodyHitbox(PlayerNetworkAdapter adapter)
        {
            var method = typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(adapter, null);
        }

        /// <summary>期望世界中心：根位置 + 身体朝向系下的胶囊中心偏移（author 基准恒等旋转时）。</summary>
        private static Vector3 ExpectedWorldCenter(Vector3 rootPos, float yaw, Vector3 localCenter)
            => rootPos + Quaternion.Euler(0f, yaw, 0f) * localCenter;

        private static void AssertVerticalContract(Transform hitbox, CapsuleCollider torso, CapsuleCollider head,
            Vector3 rootPos)
        {
            // EditMode 下 collider.bounds 依赖物理世界懒同步——用 TransformPoint 纯数学断言
            var torsoBottom = hitbox.TransformPoint(torso.center - Vector3.up * (torso.height * 0.5f)).y;
            var headTop = hitbox.TransformPoint(head.center + Vector3.up * (head.height * 0.5f)).y;
            var torsoTop = hitbox.TransformPoint(torso.center + Vector3.up * (torso.height * 0.5f)).y;
            var headBottom = hitbox.TransformPoint(head.center - Vector3.up * (head.height * 0.5f)).y;
            Assert.That(torsoBottom, Is.EqualTo(rootPos.y).Within(0.01f), "躯干下端=脚底（腿部覆盖）");
            Assert.That(headTop, Is.EqualTo(rootPos.y + 1.8f).Within(0.01f), "头上端=头顶");
            Assert.That(torsoTop, Is.GreaterThanOrEqualTo(headBottom - 0.01f), "躯干与头部在肩颈处无缝重叠（不漏判）");
        }

        [Test]
        public void Hitbox_WorldCenters_SitOnVisibleBody_NotLogicalRoot()
        {
            var rootPos = new Vector3(3f, 5f, -7f);
            var (_, hitbox, torso, head, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);

            var torsoWorld = hitbox.TransformPoint(torso.center);
            var headWorld = hitbox.TransformPoint(head.center);
            Assert.That(Vector3.Distance(torsoWorld, ExpectedWorldCenter(rootPos, 0f, TorsoCenter)), Is.LessThan(0.01f),
                "躯干中心应落在根∘(0,0.70,0.333)");
            Assert.That(Vector3.Distance(headWorld, ExpectedWorldCenter(rootPos, 0f, HeadCenter)), Is.LessThan(0.01f),
                "头部中心应落在根∘(0,1.60,0.333)");

            // 显式锁死与旧契约的差异——旧契约正是"模型外空处受击"的几何根因
            var legacyCenter = rootPos + new Vector3(0f, 0.9f, 0f);
            Assert.That(Vector3.Distance(torsoWorld, legacyCenter), Is.GreaterThan(0.19f),
                "躯干中心不得退回旧单胶囊 (0,0.9,0) 轴线");
        }

        [Test]
        public void Hitbox_Offset_FollowsBodyFacing_AndStillOnBody()
        {
            // 真实运行期"身体转向"= 根 yaw 变化（TP_Model 作者基准旋转恒等，朝向由根承载）。
            foreach (var yaw in new[] { 0f, 90f, 180f, -90f })
            {
                var rootPos = new Vector3(-2f, 0f, 4f);
                var (_, hitbox, torso, head, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
                _root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                Assert.That(Vector3.Distance(hitbox.TransformPoint(torso.center),
                    ExpectedWorldCenter(rootPos, yaw, TorsoCenter)), Is.LessThan(0.01f), $"yaw={yaw}：躯干中心随身体朝向旋转");
                Assert.That(Vector3.Distance(hitbox.TransformPoint(head.center),
                    ExpectedWorldCenter(rootPos, yaw, HeadCenter)), Is.LessThan(0.01f), $"yaw={yaw}：头部中心随身体朝向旋转");
                AssertVerticalContract(hitbox, torso, head, rootPos);
            }
        }

        [Test]
        public void Hitbox_PinIsModelAnchored_AuthorBaseYawKeepsAuthorContract()
        {
            // 作者基准带偏航（资产侧授权姿态）时：钉扎把节点原点钉到"根在模型系的表达"，
            // 双胶囊中心表达在节点系 → 作者位姿下中心=根∘(R_base·center)，整个代理随
            // 模型基准朝向一起旋转（受击体粘着身体，与朝向定义自洽）。
            var rootPos = new Vector3(-2f, 0f, 4f);
            const float baseYaw = 90f;
            var (_, hitbox, torso, head, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), baseYaw, Vector3.one);
            var yawRot = Quaternion.Euler(0f, baseYaw, 0f);

            Assert.That(Vector3.Distance(hitbox.TransformPoint(torso.center), rootPos + yawRot * TorsoCenter),
                Is.LessThan(0.01f), "作者位姿下躯干中心=根∘(R_base·center)（代理随基准朝向旋转）");
            Assert.That(Vector3.Distance(hitbox.TransformPoint(head.center), rootPos + yawRot * HeadCenter),
                Is.LessThan(0.01f), "作者位姿下头部中心=根∘(R_base·center)（代理随基准朝向旋转）");
        }

        [Test]
        public void HittableVolume_IsAnchoredOnVisibleBody_AirGapBehindAndFrontBothWithinProxyTolerance()
        {
            // 症状回归锚点（第三轮版）：旧 0.35 半径的前后可视空气各 ≈0.09m；第五轮躯干 r=0.26 后
            // 前后缘 0.073/0.593 与实测身体轮廓 0.076/0.590 几乎重合（空气 ≈0）。
            var rootPos = Vector3.zero;
            Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // ① 沿身体中轴从正后方向前打（躯干高度）：命中点=躯干胶囊最后缘
            var behindOrigin = rootPos + new Vector3(0f, 1.2f, -2f);
            Assert.That(Physics.Raycast(behindOrigin, Vector3.forward, out var backHit, 20f, ~0,
                QueryTriggerInteraction.Collide), Is.True, "受击体仍须可被射线命中（trigger + 显式 Collide）");
            float airBehindBody = BodyBackEdge - backHit.point.z;
            Assert.That(airBehindBody, Is.LessThanOrEqualTo(0.01f),
                $"躯干后方可命中空气厚度须收敛到贴身（第三轮 0.093m，本次实测 {airBehindBody:F3}m）");

            // ② 从正前方向后打：命中点必须到达身体前缘附近
            var frontOrigin = rootPos + new Vector3(0f, 1.2f, 2.5f);
            Assert.That(Physics.Raycast(frontOrigin, Vector3.back, out var frontHit, 20f, ~0,
                QueryTriggerInteraction.Collide), Is.True, "胸口最前段必须可命中");
            Assert.That(frontHit.point.z, Is.GreaterThanOrEqualTo(BodyFrontEdge - 0.01f),
                $"命中点应到达身体前缘（本次 z={frontHit.point.z:F3}，身体前缘 {BodyFrontEdge:F3}）");

            // ③ 侧向经过躯干中心：仍正常命中（收半径不得把躯干打漏）
            Assert.That(HitSurface(rootPos + new Vector3(2.5f, 0.9f, TorsoCenter.z), Vector3.left), Is.True);
        }

        [Test]
        public void Raycasts_LegTorsoHeadHit_AboveHeadMisses()
        {
            var rootPos = new Vector3(5f, 0f, -3f);
            Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // 沿标定轴线横向射击：腿/躯干/头都必须命中
            foreach (var height in new[] { 0.3f, 0.9f, 1.7f })
            {
                var shooter = rootPos + new Vector3(10f, height, TorsoCenter.z);
                Assert.That(HitSurface(shooter, Vector3.left), Is.True, $"高度 {height}m 的射线必须命中受击体");
            }

            var above = rootPos + new Vector3(10f, 2.6f, TorsoCenter.z);
            Assert.That(HitSurface(above, Vector3.left), Is.False, "头顶上方（+2.6m）不得命中");
        }

        [Test]
        public void Raycasts_BesideHeadAndTorso_AirMisses_WithinSilhouetteHit()
        {
            // 第五轮核心回归（用户 14:16 实机症状）：头旁/躯干旁的"看得见的空气"不得命中。
            var rootPos = new Vector3(5f, 0f, -3f);
            Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // 头部区（y=1.65）：偏轴 0.25m 的横向射线必须 MISS（旧 r=0.35 会 HIT——正是实机症状）
            var besideHeadFar = rootPos + new Vector3(10f, 1.65f, TorsoCenter.z + 0.25f);
            Assert.That(HitSurface(besideHeadFar, Vector3.left), Is.False, "头旁 0.25m 空气不得命中（旧 0.35 半径在此命中）");
            // 头部区：偏轴 0.10m 仍命中（头盔本体覆盖）
            var besideHeadNear = rootPos + new Vector3(10f, 1.65f, TorsoCenter.z + 0.10f);
            Assert.That(HitSurface(besideHeadNear, Vector3.left), Is.True, "头部本体（偏轴 0.10m）必须命中");

            // 躯干区（y=0.9）：偏轴 0.30m 必须 MISS；0.20m 仍命中
            var besideTorsoFar = rootPos + new Vector3(10f, 0.9f, TorsoCenter.z + 0.30f);
            Assert.That(HitSurface(besideTorsoFar, Vector3.left), Is.False, "躯干旁 0.30m 空气不得命中");
            var besideTorsoNear = rootPos + new Vector3(10f, 0.9f, TorsoCenter.z + 0.20f);
            Assert.That(HitSurface(besideTorsoNear, Vector3.left), Is.True, "躯干本体（偏轴 0.20m）必须命中");
        }

        [Test]
        public void Hitbox_RerunDoesNotRebakeLivePose_CenterRidesBody()
        {
            var rootPos = new Vector3(1f, 2f, 3f);
            var (adapter, hitbox, torso, head, model) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            var pinBefore = hitbox.localPosition;

            // 模型被视觉平滑/动画移动+转向后重入 EnsureBodyHitbox（池化复用 OnStartNetwork 语义）：
            // 钉扎=模型系常量，不得把当帧位姿烤进去；中心随身体走（模型锚定）。
            model.localPosition = new Vector3(0.4f, 0.2f, 0.6f);
            model.localRotation = Quaternion.Euler(0f, 35f, 0f);
            InvokeEnsureBodyHitbox(adapter);

            Assert.That(hitbox.localPosition, Is.EqualTo(pinBefore).Within(1e-5f),
                "重入不得改变钉扎（第四轮契约：模型系常量，与当帧姿态无关）");
            // 模型系中心 = 钉扎（节点原点=根在模型系的表达）+ 胶囊中心（节点系）：
            var torsoInModel = model.InverseTransformPoint(hitbox.TransformPoint(torso.center));
            var headInModel = model.InverseTransformPoint(hitbox.TransformPoint(head.center));
            Assert.That(Vector3.Distance(torsoInModel, pinBefore + TorsoCenter), Is.LessThan(0.01f),
                "躯干中心随身体走（模型系=钉扎+参数中心，不回退根锚定）");
            Assert.That(Vector3.Distance(headInModel, pinBefore + HeadCenter), Is.LessThan(0.01f),
                "头部中心随身体走（模型系=钉扎+参数中心，不回退根锚定）");
        }

        [Test]
        public void Hitbox_SurvivesPooledReuseDuringDeathPose_NoTransientBakedIn()
        {
            // 2026-09-19 实机取证回归（第四轮根因）：死亡表现 applied 后 TP_Model = 前倾 85° +
            // 贴地抬升 0.402m，池化复用触发 OnStartNetwork → EnsureBodyHitbox 重入。
            // 旧实现 InverseTransformPoint(倒地位姿) 把瞬态烤进钉扎，复位后受击体与可见身体
            // 错位 ≈0.8m——实机"瞄模型后方空气仍掉血 + 血雾悬空"。
            var rootPos = new Vector3(-1f, 0f, 2f);
            var (adapter, hitbox, torso, _, model) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            model.localRotation = Quaternion.Euler(85f, 0f, 0f);
            model.localPosition = new Vector3(0f, 0.402f, 0.341f);
            InvokeEnsureBodyHitbox(adapter);

            // 死亡表现复位（ResetDeathVisual → 还原作者位姿 + RepinBodyHitboxAfterRestore）
            model.localRotation = Quaternion.identity;
            model.localPosition = new Vector3(0f, 0f, 0.341f);

            Assert.That(Vector3.Distance(hitbox.TransformPoint(torso.center), rootPos + TorsoCenter), Is.LessThan(0.01f),
                "死亡期间被重入的钉扎不得污染：复位后躯干中心必须回到根∘参数中心");
        }

        [Test]
        public void Hitbox_HealsLegacySingleCollider_AndConfiguresBothCapsules()
        {
            // 池化复用自愈：旧构建生成的对象只有一个"旧全身胶囊"，新装配入口必须补齐第二胶囊
            // 并把两个胶囊都按当前参数重配（旧 0.35 半径/0.9 中心不得残留）。
            var rootPos = new Vector3(2f, 0f, -4f);
            var (adapter, hitbox, torso, head, _) = Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);

            Object.DestroyImmediate(head); // 模拟旧构建对象：只剩单胶囊
            InvokeEnsureBodyHitbox(adapter);

            var capsules = hitbox.GetComponents<CapsuleCollider>();
            Assert.That(capsules.Length, Is.EqualTo(2), "重入必须补齐躯干+头部双胶囊");
            Assert.That(capsules[0].radius, Is.EqualTo(TorsoRadius).Within(1e-4f), "躯干半径按当前参数重配");
            Assert.That(capsules[0].center, Is.EqualTo(TorsoCenter).Within(0.001f), "躯干中心按当前参数重配");
            Assert.That(capsules[1].radius, Is.EqualTo(HeadRadius).Within(1e-4f), "头部半径按当前参数重配");
            Assert.That(capsules[1].center, Is.EqualTo(HeadCenter).Within(0.001f), "头部中心按当前参数重配");
            Assert.That(capsules[0].isTrigger && capsules[1].isTrigger, Is.True, "双胶囊均为 trigger（受击/阻挡分离）");
        }

        [Test]
        public void Raycasts_ShoulderSeamBand_HasFiniteOverlapWidth_HeadSideAirStillMisses()
        {
            // F11（2026-09-19 审计）核心反例：旧参数躯干顶球心 y=1.14 与头部底球心 y=1.55 在
            // y=1.40 仅极点相切——横射 y=1.40 漏过两体、y=1.39 可命中半宽仅 0.071m。
            // 新参数躯干上延（顶球心 y=1.24）：接缝带各高度都有有限宽度（解析值见断言）。
            var rootPos = new Vector3(5f, 0f, -3f);
            Build(rootPos, new Vector3(0f, 0f, 0.341f), 0f, Vector3.one);
            Physics.SyncTransforms();

            // ① 轴向射线：接缝带全部命中（1.39/1.40/1.41/1.45——旧实现 1.40 必 MISS）
            foreach (var height in new[] { 1.35f, 1.39f, 1.40f, 1.41f, 1.45f })
            {
                var shooter = rootPos + new Vector3(10f, height, TorsoCenter.z);
                Assert.That(HitSurface(shooter, Vector3.left), Is.True,
                    $"肩颈接缝高度 {height}m 的轴向射线必须命中（球冠极点相切不构成有限覆盖）");
            }

            // ② 宽度界（躯干 r=.26、顶球心 y=1.24）：y=1.40 半宽 ≈0.205 ——
            //    偏轴 0.19 命中（有限覆盖），偏轴 0.22 必 MISS（不是把整个人体扩回旧空气命中）
            var inSeam = rootPos + new Vector3(10f, 1.40f, TorsoCenter.z + 0.19f);
            Assert.That(HitSurface(inSeam, Vector3.left), Is.True, "接缝带 y=1.40 偏轴 0.19m 在躯干球冠宽度内（命中）");
            var outSeam = rootPos + new Vector3(10f, 1.40f, TorsoCenter.z + 0.22f);
            Assert.That(HitSurface(outSeam, Vector3.left), Is.False, "接缝带 y=1.40 偏轴 0.22m 超出躯干球冠（MISS，宽度有界）");

            // ③ 头旁空气反例保持：y=1.60（头部圆柱区）偏轴 0.20m 仍 MISS（躯干上延没有波及头侧）
            var besideHead = rootPos + new Vector3(10f, 1.60f, TorsoCenter.z + 0.20f);
            Assert.That(HitSurface(besideHead, Vector3.left), Is.False, "头旁 0.20m 空气不得命中（F11 不得以扩大全身换接缝）");
        }

        /// <summary>与 CombatResolver 同口径的受击查询（受击体是 trigger，必须显式 Collide）。</summary>
        private static bool HitSurface(Vector3 origin, Vector3 direction)
            => Physics.Raycast(origin, direction.normalized, 20f, ~0, QueryTriggerInteraction.Collide);
    }
}
