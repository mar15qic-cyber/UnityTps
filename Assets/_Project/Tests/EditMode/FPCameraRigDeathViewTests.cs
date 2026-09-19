using System.Collections.Generic;
using System.Reflection;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Network;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using NUnit.Framework;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 本人死亡倒地视角回归（2026-09-15 死亡重生票据；2026-09-16 审计 §3.3-1 修订）：
    /// FPCameraRig 只负责相机位置/侧倾/FOV——**不再**保存/恢复 FP Renderer。
    /// 旧实现与 FPWeaponRig 各自快照同一批 Renderer.enabled，双写互相覆盖造成"死亡后当前枪永久隐形"
    /// （相机先关→Rig 把 false 存成原始状态→复活相机恢复→Rig 下一帧又写回 false）。
    /// Renderer 显隐唯一写者 = FPWeaponRig.FPViewModelVisibility（原因合成），由 FPVisibilityAndLaserGateTests
    /// 覆盖；本套件锁相机自身不变量 + "相机不得再碰 Renderer"的单一写者边界。
    /// 死亡态经 NetworkCombatAuthority 的 _dead SyncVar 注入（离线 IsOwnerPlayer=true，同 authored player 语义）。
    /// </summary>
    public sealed class FPCameraRigDeathViewTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private const float StandingY = 1.62f;
        private readonly List<GameObject> _spawned = new();
        private CinemachineCamera _vcam;

        private (FPCameraRig rig, NetworkCombatAuthority combat, Renderer visible, Renderer alreadyOff, Transform pivot, FPWeaponRig weaponRig)
            Build()
        {
            var player = new GameObject("Player");
            _spawned.Add(player);
            var combat = player.AddComponent<NetworkCombatAuthority>();

            var pivot = new GameObject("CameraPivot");
            pivot.transform.SetParent(player.transform, false);
            pivot.transform.localPosition = new Vector3(0f, StandingY, 0f);
            var rig = pivot.AddComponent<FPCameraRig>();

            var cameraGo = new GameObject("CM_FP_Camera");
            cameraGo.transform.SetParent(pivot.transform, false);
            var vcam = cameraGo.AddComponent<CinemachineCamera>();
            var lens = vcam.Lens;
            lens.FieldOfView = 44f; // 站立 FOV 与腰射 FOV(33) 区分开，便于断言死亡分支
            vcam.Lens = lens;
            _vcam = vcam;

            // FP 武器视图（可见性唯一写者的宿主）+ 两块作者状态不同的网格
            var weaponRigGo = new GameObject("FPWeaponRig");
            weaponRigGo.transform.SetParent(pivot.transform, false);
            var weaponRig = weaponRigGo.AddComponent<FPWeaponRig>();
            var viewGo = new GameObject("FP_View");
            viewGo.transform.SetParent(weaponRigGo.transform, false);
            var visible = NewRenderer(viewGo.transform, "WeaponView", enabled: true);
            var alreadyOff = NewRenderer(viewGo.transform, "DisabledByOtherSystem", enabled: false);
            typeof(FPWeaponRig).GetField("_activeView", NonPublic)?.SetValue(weaponRig, viewGo);
            foreach (var renderer in viewGo.GetComponentsInChildren<Renderer>(true))
                weaponRig.RegisterViewRenderer(renderer);

            // Awake 在本工程 EditMode 下不触发：直接注入私有引用（避免 Awake 覆盖注入的 vcam）
            typeof(FPCameraRig).GetField("cinemachineCamera", NonPublic)?.SetValue(rig, vcam);
            typeof(FPCameraRig).GetField("combat", NonPublic)?.SetValue(rig, combat);
            typeof(FPCameraRig).GetField("hipFov", NonPublic)?.SetValue(rig, 33f);
            return (rig, combat, visible, alreadyOff, pivot.transform, weaponRig);
        }

        private Renderer NewRenderer(Transform parent, string name, bool enabled)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.enabled = enabled;
            return renderer;
        }

        /// <summary>注入死亡态：FishNet weaver 把 NetworkBehaviour 上的 SyncVar 字段改写成 public
        /// 实例字段（实测 isPublic=true），SyncVar 内部的 _value 仍是私有字段。</summary>
        private static void SetDead(NetworkCombatAuthority combat, bool dead)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var syncVar = (SyncVar<bool>)typeof(NetworkCombatAuthority).GetField("_dead", any).GetValue(combat);
            typeof(SyncVar<bool>).GetField("_value", any).SetValue(syncVar, dead);
        }

        private static void Drive(FPCameraRig rig, int frames = 1)
        {
            var update = typeof(FPCameraRig).GetMethod("Update", NonPublic);
            for (int i = 0; i < frames; i++) update.Invoke(rig, null);
        }

        private static T Private<T>(object target, string field)
            => (T)target.GetType().GetField(field, NonPublic).GetValue(target);

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        /// <summary>
        /// 死亡：相机下沉/腰射 FOV/侧倾；**渲染器一概不碰**（单一写者边界——那是 FPWeaponRig 的职责）。
        /// 重复死亡帧幂等。
        /// </summary>
        [Test]
        public void DeathView_LowersCamera_AndDoesNotTouchRenderers()
        {
            var (rig, combat, visible, alreadyOff, pivot, _) = Build();

            SetDead(combat, true);
            Drive(rig);

            Assert.That(Private<bool>(rig, "deathActive"), Is.True, "死亡态已激活");
            Assert.That(visible.enabled, Is.True, "相机死亡分支不得写 Renderer（唯一写者=FPWeaponRig）");
            Assert.That(alreadyOff.enabled, Is.False, "作者关闭的渲染器保持原状");
            Assert.That(_vcam.Lens.FieldOfView, Is.EqualTo(33f).Within(1e-3f), "倒地使用腰射 FOV");

            Drive(rig, 20); // 重复死亡帧（服务器可能重复广播 / 逐帧执行）
            Assert.That(pivot.localPosition.y, Is.LessThanOrEqualTo(StandingY + 1e-4f), "倒地视角不高于站立高度");
        }

        /// <summary>复活：相机位置/侧倾精确还原；渲染器仍不受相机影响。</summary>
        [Test]
        public void RespawnView_RestoresCameraOnly()
        {
            var (rig, combat, visible, alreadyOff, pivot, _) = Build();

            SetDead(combat, true);
            Drive(rig, 10);
            SetDead(combat, false);
            Drive(rig);

            Assert.That(Private<bool>(rig, "deathActive"), Is.False, "复活后死亡态解除");
            Assert.That(pivot.localPosition, Is.EqualTo(new Vector3(0f, StandingY, 0f)), "相机位置精确还原");
            Assert.That(visible.enabled, Is.True, "相机不得改写渲染器（作者状态保持）");
            Assert.That(alreadyOff.enabled, Is.False);
        }

        /// <summary>组件禁用（场景卸载/返回大厅）也必须还原——否则下一局相机停在倒地高度。</summary>
        [Test]
        public void OnDisable_WhileDead_RestoresView()
        {
            var (rig, combat, visible, _, pivot, _) = Build();
            SetDead(combat, true);
            Drive(rig, 10);

            typeof(FPCameraRig).GetMethod("OnDisable", NonPublic).Invoke(rig, null);

            Assert.That(Private<bool>(rig, "deathActive"), Is.False);
            Assert.That(pivot.localPosition, Is.EqualTo(new Vector3(0f, StandingY, 0f)));
        }

        /// <summary>连续三轮死亡→复活：每轮相机都下沉与还原（用户要求"连续至少三轮"）。</summary>
        [Test]
        public void ThreeDeathRespawnCycles_ViewTogglesEachRound()
        {
            var (rig, combat, _, _, pivot, _) = Build();
            for (int round = 1; round <= 3; round++)
            {
                SetDead(combat, true);
                Drive(rig, 5);
                Assert.That(Private<bool>(rig, "deathActive"), Is.True, $"第 {round} 轮死亡态激活");

                SetDead(combat, false);
                Drive(rig);
                Assert.That(pivot.localPosition.y, Is.EqualTo(StandingY).Within(1e-4f), $"第 {round} 轮位置还原");
            }
        }

        /// <summary>无 NetworkCombatAuthority 引用（离线 authored 场景）时不得抛异常。</summary>
        [Test]
        public void MissingCombatReference_IsSafe()
        {
            var (rig, _, visible, _, _, _) = Build();
            typeof(FPCameraRig).GetField("combat", NonPublic)?.SetValue(rig, null);
            Assert.DoesNotThrow(() => Drive(rig, 3), "缺失战斗权威引用时相机更新必须安全");
            Assert.That(visible.enabled, Is.True, "无死亡来源时不隐藏任何渲染器");
        }

        /// <summary>
        /// §7.1 双写时序反例（集成版）：相机死亡分支与 FPWeaponRig 死亡入口**任意先后顺序**下，
        /// 复活 + 复活准备释放后，作者可见网格必须显示、作者隐藏网格继续隐藏。
        /// </summary>
        [Test]
        public void DeathDoubleWrite_CombinedOrdering_RestoresAuthorVisibility()
        {
            // 顺序一：相机先处理死亡，Rig 后处理
            var (rig, combat, visible, alreadyOff, _, weaponRig) = Build();
            SetDead(combat, true);
            Drive(rig);                    // 相机分支
            weaponRig.ApplyDeathState();   // Rig 分支（后到）
            Assert.That(visible.enabled, Is.False, "Rig 死亡入口隐藏");

            SetDead(combat, false);
            Drive(rig);                    // 相机先恢复
            weaponRig.ApplyRespawnState(); // Rig 后恢复（进入 RespawnPreparing）
            var visibility = (FPViewModelVisibility)typeof(FPWeaponRig).GetField("_visibility", NonPublic).GetValue(weaponRig);
            visibility.SetHidden(FPViewHideReason.RespawnPreparing, false); // 协程到点（EditMode 直驱原因位）

            Assert.That(visible.enabled, Is.True, "复活完成后作者可见网格必须显示（双写覆盖已消除）");
            Assert.That(alreadyOff.enabled, Is.False, "作者关闭的网格继续隐藏");

            // 顺序二：Rig 先处理死亡，相机后处理
            var (rig2, combat2, visible2, alreadyOff2, _, weaponRig2) = Build();
            SetDead(combat2, true);
            weaponRig2.ApplyDeathState();  // Rig 分支（先到）
            Drive(rig2);                   // 相机分支
            Assert.That(visible2.enabled, Is.False);

            SetDead(combat2, false);
            weaponRig2.ApplyRespawnState();
            Drive(rig2);
            var visibility2 = (FPViewModelVisibility)typeof(FPWeaponRig).GetField("_visibility", NonPublic).GetValue(weaponRig2);
            visibility2.SetHidden(FPViewHideReason.RespawnPreparing, false);

            Assert.That(visible2.enabled, Is.True);
            Assert.That(alreadyOff2.enabled, Is.False);
        }
    }
}
