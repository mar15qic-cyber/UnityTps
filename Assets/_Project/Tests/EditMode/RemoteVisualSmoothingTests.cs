using System.Reflection;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-16 实测（YYLL 硬校正风暴）定案方案A：远端权威碰撞根 + TP_Model 视觉平滑。
    /// 背景：本地 Owner 预测碰撞撞的是远端"插值代理"（滞后 2-8 tick），服务器撞的是"权威"位姿
    /// → 近距交火 CC 去贯穿分歧 → 单步跳变误差 → 追击方被反复 SNAP（36 次/局，平滑带只命中 5 次）。
    /// 本套件锁：基准只捕一次（防把倒地姿态存成基准）、死亡期间不写 TP_Model、
    /// 平滑值域恒在 [当前, 目标] 之间、收敛到作者基准、瞬移直接对位。
    /// </summary>
    public sealed class RemoteVisualSmoothingTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly System.Collections.Generic.List<Object> _spawned = new();

        private (PlayerNetworkAdapter adapter, Transform root, Transform model, NetworkCombatAuthority combat) Build()
        {
            var rootGo = new GameObject("RemotePlayer");
            _spawned.Add(rootGo);
            rootGo.AddComponent<CharacterController>();
            var combat = rootGo.AddComponent<NetworkCombatAuthority>();
            var adapter = rootGo.AddComponent<PlayerNetworkAdapter>();

            var modelGo = new GameObject("TP_Model");
            _spawned.Add(modelGo);
            modelGo.transform.SetParent(rootGo.transform, false);
            // 作者基准带偏移/朝向（真实 prefab 的 TP_Model localPosition=(0,0,0.34) 类似）
            modelGo.transform.localPosition = new Vector3(0.1f, 0f, 0.34f);
            modelGo.transform.localRotation = Quaternion.Euler(0f, 25f, 0f);

            var locomotor = rootGo.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.RemoteProxy);
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);
            typeof(PlayerNetworkAdapter).GetField("_combatAuthority", NonPublic)?.SetValue(adapter, combat);
            return (adapter, rootGo.transform, modelGo.transform, combat);
        }

        private static void SetDead(NetworkCombatAuthority combat, bool dead)
        {
            var syncVar = (SyncVar<bool>)typeof(NetworkCombatAuthority).GetField("_dead", Any).GetValue(combat);
            typeof(SyncVar<bool>).GetField("_value", Any).SetValue(syncVar, dead);
        }

        private static void Invoke(object target, string method)
            => target.GetType().GetMethod(method, NonPublic).Invoke(target, null);

        private static T Private<T>(object target, string field)
            => (T)target.GetType().GetField(field, NonPublic).GetValue(target);

        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, NonPublic).SetValue(target, value);

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _spawned)
                if (obj != null) Object.DestroyImmediate(obj);
            _spawned.Clear();
        }

        /// <summary>2026-09-17 缓冲插值版：远端模型以渲染帧率丝滑跟随（渲染步长恒 ≤ 权威 tick 步长），
        /// 权威位姿停住并超过自适应延迟后收敛到 根∘作者基准。</summary>
        [Test]
        public void Smoothing_InterpolatesSmoothly_AndConvergesAfterSettling()
        {
            var (adapter, root, model, _) = Build();
            Vector3 baseLocal = model.localPosition;

            float t = 0f;
            adapter.TestRemoteVisualTime = t;
            Invoke(adapter, "UpdateRemoteVisualSmoothing"); // 首帧：快照对位 + 首样本

            // 权威根以 30Hz 步进（每 tick 0.114m ≈ 冲刺速度）；渲染按 120Hz 求值
            const float tick = 1f / 30f;
            const float render = 1f / 120f;
            float prevZ = model.position.z;
            float maxRenderStep = 0f;
            for (int tickIndex = 0; tickIndex < 30; tickIndex++)
            {
                root.position += new Vector3(0f, 0f, 0.114f); // NT 到达：根瞬移一个 tick 步长
                for (int r = 0; r < 4; r++)
                {
                    t += render;
                    adapter.TestRemoteVisualTime = t;
                    Invoke(adapter, "UpdateRemoteVisualSmoothing");
                    float step = model.position.z - prevZ;
                    if (step > maxRenderStep) maxRenderStep = step;
                    prevZ = model.position.z;
                }
            }
            Assert.That(maxRenderStep, Is.LessThanOrEqualTo(0.114f + 1e-4f),
                "丝滑性：渲染帧位移不得再现 30Hz 权威台阶（缓冲插值把台阶摊平到渲染帧）");

            // 根停住、时间推进超过延迟窗口 → 视觉收敛到 根∘作者基准
            for (int i = 0; i < 30; i++)
            {
                t += tick;
                adapter.TestRemoteVisualTime = t;
                Invoke(adapter, "UpdateRemoteVisualSmoothing");
            }
            Vector3 target = root.TransformPoint(baseLocal);
            Assert.That(Vector3.Distance(model.position, target), Is.LessThan(0.002f),
                "权威停住后视觉必须收敛到 根 ∘ 作者局部基准");
        }

        /// <summary>
        /// 2026-09-18 复核 R1 竞态：观察者首次收到该玩家时他已死亡（基准尚未捕获）→ 复活广播使
        /// 权威 dead 变 false，但 NetworkCombatAuthority 的复位还没跑（本组件 DefaultExecutionOrder=-120
        /// 早于它的 Update）。那一刻 TP_Model 仍是"前倾 85° + 贴地抬升"，只看 IsDead 会把尸体位姿
        /// 永久存成作者基准 → 之后每次复活都按它对位（= 永久性陷地）。门禁必须是"死亡表现复位完成"。
        /// </summary>
        [Test]
        public void Baseline_FirstCapture_IsDeferredUntilDeathVisualIsFullyReset()
        {
            var (adapter, root, model, combat) = Build();
            Vector3 authorLocal = model.localPosition;
            Quaternion authorRot = model.localRotation;
            Assert.That(Private<bool>(adapter, "_tpModelBaseCaptured"), Is.False, "前置：基准尚未捕获");

            Invoke(combat, "ApplyDeathVisual"); // 首次收到即已死：死亡表现已写入 TP_Model
            Assert.That(model.localRotation.eulerAngles.x, Is.GreaterThan(10f), "前置：尸体位姿已写入");
            adapter.TestRemoteVisualTime = 0f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            Assert.That(Private<bool>(adapter, "_tpModelBaseCaptured"), Is.False, "死亡期间不捕获");

            SetDead(combat, false); // 复活广播先到：IsDead 已 false，表现尚未复位
            Assert.That(combat.IsDead, Is.False, "前置：IsDead 已变 false");
            Assert.That(combat.IsDeathVisualActive, Is.True, "前置：死亡表现仍在（复位未跑）");

            adapter.TestRemoteVisualTime = 0.033f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing"); // 本组件先 Update —— 旧实现在这里污染基准

            Assert.That(Private<bool>(adapter, "_tpModelBaseCaptured"), Is.False,
                "R1：仅凭 IsDead 守门会在这一帧把尸体位姿存成作者基准");

            Invoke(combat, "ResetDeathVisual"); // NCA 复位（TP_Model 还原 + 平滑失效）
            adapter.TestRemoteVisualTime = 0.066f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");

            Assert.That(Private<bool>(adapter, "_tpModelBaseCaptured"), Is.True, "复位完成后才允许捕获");
            Assert.That(Vector3.Distance(model.localPosition, authorLocal), Is.LessThan(1e-4f),
                "捕获到的基准必须是作者位姿，不是尸体位姿");
            Assert.That(Quaternion.Angle(model.localRotation, authorRot), Is.LessThan(0.01f));
        }

        /// <summary>写者唯一性：死亡期间 TP_Model 归死亡表现所有，平滑器必须停写并作废状态。</summary>
        [Test]
        public void Smoothing_Dead_DoesNotTouchModel_AndInvalidatesState()
        {
            var (adapter, root, model, combat) = Build();
            adapter.TestRemoteVisualTime = 0f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing"); // 首帧对位 + 首样本
            root.SetPositionAndRotation(root.position + new Vector3(1f, 0f, 0f), root.rotation);
            adapter.TestRemoteVisualTime = 0.033f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            Vector3 before = model.position;
            Quaternion beforeRot = model.rotation;

            SetDead(combat, true);
            adapter.TestRemoteVisualTime = 0.066f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");

            Assert.That((bool)Private<object>(adapter, "_remoteVisualValid"), Is.False, "死亡即作废追随状态");
            Assert.That(model.position, Is.EqualTo(before), "死亡期间不得写 TP_Model（倒地/贴地是唯一写者）");
            Assert.That(model.rotation, Is.EqualTo(beforeRot));

            // 复活（远端 _dead=false）：从当前位姿重新起算并直接对位目标（不做穿场插值）
            SetDead(combat, false);
            adapter.TestRemoteVisualTime = 0.1f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            Vector3 target = root.TransformPoint((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"));
            Assert.That(Vector3.Distance(model.position, target), Is.LessThan(0.001f),
                "复活后首帧直接对位到 根∘作者基准");
        }

        /// <summary>瞬移级跳变（>5m）不得做穿场插值——直接对位。</summary>
        [Test]
        public void Smoothing_TeleportSizeJump_SnapsDirectly()
        {
            var (adapter, root, model, _) = Build();
            adapter.TestRemoteVisualTime = 0f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            root.SetPositionAndRotation(root.position + new Vector3(0f, 0f, 20f), root.rotation);

            adapter.TestRemoteVisualTime = 0.033f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");

            Vector3 target = root.TransformPoint((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"));
            Assert.That(Vector3.Distance(model.position, target), Is.LessThan(0.001f),
                "大位移（重生/传送）必须直接对位");
        }

        /// <summary>作者基准每会话只捕一次：死亡→复活不会把倒地姿态存成新基准。</summary>
        [Test]
        public void Baseline_IsCapturedOncePerSession_EvenAcrossDeath()
        {
            var (adapter, root, model, combat) = Build();
            adapter.TestRemoteVisualTime = 0f;
            Invoke(adapter, "EnsureRemoteVisualBaseline");
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            Vector3 baseLocal = (Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition");

            SetDead(combat, true);
            model.localPosition = new Vector3(0f, -0.9f, 0.5f); // 倒地/贴地写过 TP_Model
            adapter.TestRemoteVisualTime = 0.033f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            SetDead(combat, false);
            adapter.TestRemoteVisualTime = 0.066f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");

            Assert.That((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"), Is.EqualTo(baseLocal),
                "死亡期间模型被倒地表现改写，也不得污染作者基准");
            Vector3 target = root.TransformPoint(baseLocal);
            Assert.That(Vector3.Distance(model.position, target), Is.LessThan(0.001f),
                "复活后回到作者基准（不会带着倒地偏移永远歪着）");
        }

        /// <summary>2026-09-18 实机问题6（侧/背射击不掉血）：DS 钉根接缝——TP_Model 直接写到
        /// 根∘作者基准（服务器侧 hitbox 与权威根恒等），缓冲清空、追随作废。</summary>
        [Test]
        public void PinTpModelToRootForServer_AlignsModelToRootBaseline()
        {
            var (adapter, root, model, _) = Build();
            root.SetPositionAndRotation(new Vector3(3f, 0.15f, -2f), Quaternion.Euler(0f, 130f, 0f));

            adapter.PinTpModelToRootForServer();

            Vector3 expectedPos = root.TransformPoint((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"));
            Quaternion expectedRot = root.rotation * (Quaternion)Private<object>(adapter, "_tpModelBaseLocalRotation");
            Assert.That(Vector3.Distance(model.position, expectedPos), Is.LessThan(1e-4f),
                "DS 上 TP_Model 必须钉在 根∘作者基准（hitbox 与权威根恒等）");
            Assert.That(Quaternion.Angle(model.rotation, expectedRot), Is.LessThan(0.01f));
            Assert.That((bool)Private<object>(adapter, "_remoteVisualValid"), Is.False,
                "钉根后追随状态作废（不维护插值语义）");
            Assert.That(Private<object>(adapter, "_remoteVisualBuffer").GetType()
                    .GetProperty("HasSamples").GetValue(Private<object>(adapter, "_remoteVisualBuffer")),
                Is.EqualTo(false), "钉根后缓冲必须为空（LagComp 之外无任何滞后样本）");

            // 再次移动根后重复调用：恒等保持（服务器每帧调用语义）
            root.position += new Vector3(0.5f, 0f, 0.25f);
            adapter.PinTpModelToRootForServer();
            expectedPos = root.TransformPoint((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"));
            Assert.That(Vector3.Distance(model.position, expectedPos), Is.LessThan(1e-4f));
        }

        /// <summary>2026-09-18 实机问题1 兜底：缓冲失效后下一帧必走对位分支（幂等）。</summary>
        [Test]
        public void InvalidateRemoteVisualSmoothing_ForcesNextFrameSnap()
        {
            var (adapter, root, model, _) = Build();
            adapter.TestRemoteVisualTime = 0f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing"); // 首帧对位 + 首样本
            root.position += new Vector3(0.3f, 0f, 0.2f);     // 小于 5m 的跳变（正常不会触发 snap）
            adapter.TestRemoteVisualTime = 0.033f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");   // 走插值路径
            Assert.That((bool)Private<object>(adapter, "_remoteVisualValid"), Is.True);

            adapter.InvalidateRemoteVisualSmoothing();
            Assert.That((bool)Private<object>(adapter, "_remoteVisualValid"), Is.False);

            adapter.TestRemoteVisualTime = 0.066f;
            Invoke(adapter, "UpdateRemoteVisualSmoothing");
            Vector3 target = root.TransformPoint((Vector3)Private<object>(adapter, "_tpModelBaseLocalPosition"));
            Assert.That(Vector3.Distance(model.position, target), Is.LessThan(0.001f),
                "失效后下一帧必须直接对位到 根∘作者基准（无穿场插值）");
        }

        /// <summary>2026-09-17 修复（远端 TP 俯仰瞬回）：服务器侧去激活本地表现——
        /// CameraPivot 整树失活 + ownerOnly 组件禁用（FPMouseLook 不再每帧清零权威俯仰）。</summary>
        [Test]
        public void ServerDeactivation_DisablesCameraPivotAndOwnerComponents()
        {
            var rootGo = new GameObject("SrvPlayer");
            _spawned.Add(rootGo);
            var pivotGo = new GameObject("CameraPivot");
            pivotGo.transform.SetParent(rootGo.transform, false);
            var input = rootGo.AddComponent<Game.Gameplay.Player.InputReader>();
            var adapter = rootGo.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("localOnlyRoot", NonPublic)
                .SetValue(adapter, pivotGo);
            typeof(PlayerNetworkAdapter).GetField("ownerOnlyComponents", NonPublic)
                .SetValue(adapter, new Behaviour[] { input });

            Invoke(adapter, "DeactivateLocalPresentationForServer");

            Assert.That(pivotGo.activeSelf, Is.False, "DS 上远端玩家的 CameraPivot 必须失活");
            Assert.That(input.enabled, Is.False, "ownerOnly 组件（InputReader 等）必须禁用");
        }
    }
}
