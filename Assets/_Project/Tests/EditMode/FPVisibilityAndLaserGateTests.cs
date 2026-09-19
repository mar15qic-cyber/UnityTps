using System.Reflection;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-16 §7.1–§7.4 定向回归：FP 死亡可见性与激光持续写者。
    /// 核心反例（旧缺陷链）：FPCameraRig 与 FPWeaponRig 各自保存/恢复同一批 Renderer.enabled——
    /// 相机先关（基线该是 true）、Rig 把 false 存成"原始状态"、复活相机恢复 true 后下一帧 Rig 又
    /// 写回 false → 死亡后当前枪永久隐形。修复后 FP 可见性只有一个写者（FPViewModelVisibility，
    /// 原因合成 + 受控注册基线），本套件用真实 Renderer 与真实 LaserSightBeam.LateUpdate 锁死。
    /// </summary>
    public sealed class FPVisibilityAndLaserGateTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly System.Collections.Generic.List<Object> _spawned = new();

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _spawned)
                if (obj != null) Object.DestroyImmediate(obj);
            _spawned.Clear();
        }

        private GameObject Prim(string name, bool enabledRenderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.GetComponent<MeshRenderer>().enabled = enabledRenderer;
            _spawned.Add(go);
            return go;
        }

        // ---- FPViewModelVisibility：原因合成与受控基线 ----

        [Test]
        public void Reasons_Compose_ReleaseOneKeepsOthers()
        {
            var visibility = new FPViewModelVisibility();
            var mesh = Prim("Mesh", true);
            visibility.Register(mesh.GetComponent<MeshRenderer>());

            visibility.SetHidden(FPViewHideReason.Death, true);
            Assert.That(mesh.GetComponent<MeshRenderer>().enabled, Is.False, "死亡当帧必须隐藏");

            visibility.SetHidden(FPViewHideReason.ScopeOverlay, true);
            visibility.SetHidden(FPViewHideReason.Death, false);
            Assert.That(visibility.IsHidden, Is.True, "释放死亡后镜内原因仍在，不得恢复可见");
            Assert.That(mesh.GetComponent<MeshRenderer>().enabled, Is.False);

            visibility.SetHidden(FPViewHideReason.ScopeOverlay, false);
            Assert.That(mesh.GetComponent<MeshRenderer>().enabled, Is.True, "全部原因清空才恢复");
        }

        /// <summary>§7.1 核心反例：外部系统（旧相机链）先行把 Renderer 关掉，死亡→复活后必须恢复
        /// 作者可见状态——基线在受控注册时确定，不得把外部临时隐藏读成作者原始状态。</summary>
        [Test]
        public void Baseline_SurvivesExternalWrite_BeforeDeath()
        {
            var visibility = new FPViewModelVisibility();
            var visible = Prim("VisibleMesh", true);
            var hidden = Prim("AuthorOffMesh", false);
            var visibleRenderer = visible.GetComponent<MeshRenderer>();
            var hiddenRenderer = hidden.GetComponent<MeshRenderer>();
            visibility.Register(visibleRenderer);
            visibility.Register(hiddenRenderer);

            visibleRenderer.enabled = false; // 旧 FPCameraRig 死亡链的先行写入（作者状态是 true）

            visibility.SetHidden(FPViewHideReason.Death, true);
            visibility.SetHidden(FPViewHideReason.Death, false);

            Assert.That(visibleRenderer.enabled, Is.True, "作者可见网格必须恢复（基线=注册时的 true）");
            Assert.That(hiddenRenderer.enabled, Is.False, "作者本就关闭的网格不得被打开");
        }

        [Test]
        public void RegisterWhileHidden_DoesNotFlash()
        {
            var visibility = new FPViewModelVisibility();
            var mesh = Prim("LatePart", true);
            visibility.SetHidden(FPViewHideReason.Death, true);
            var renderer = mesh.GetComponent<MeshRenderer>();
            visibility.Register(renderer);
            Assert.That(renderer.enabled, Is.False, "隐藏期间挂载的新配件必须立即跟随合成结果");
        }

        [Test]
        public void ResetAll_ClearsReasons_AndRestoresBaseline()
        {
            var visibility = new FPViewModelVisibility();
            var mesh = Prim("Mesh", true);
            var renderer = mesh.GetComponent<MeshRenderer>();
            visibility.Register(renderer);
            visibility.SetHidden(FPViewHideReason.Death, true);
            visibility.ResetAll();
            Assert.That(visibility.IsHidden, Is.False);
            Assert.That(renderer.enabled, Is.True, "禁用/销毁边界必须按基线还原");
        }

        // ---- FPWeaponRig：死亡/复活入口 + 复活代际 ----

        private (FPWeaponRig rig, GameObject view, MeshRenderer visibleRenderer, MeshRenderer offRenderer) BuildRig()
        {
            var rootGo = new GameObject("PlayerRig");
            _spawned.Add(rootGo);
            var rigGo = new GameObject("FPWeaponRig");
            rigGo.transform.SetParent(rootGo.transform, false);
            _spawned.Add(rigGo);
            var rig = rigGo.AddComponent<FPWeaponRig>();

            var view = new GameObject("FP_View");
            view.transform.SetParent(rigGo.transform, false);
            _spawned.Add(view);
            var visibleRenderer = Prim("GunMesh", true).GetComponent<MeshRenderer>();
            visibleRenderer.transform.SetParent(view.transform, false);
            var offRenderer = Prim("AuthorOffMesh", false).GetComponent<MeshRenderer>();
            offRenderer.transform.SetParent(view.transform, false);

            typeof(FPWeaponRig).GetField("_activeView", NonPublic).SetValue(rig, view);
            foreach (var renderer in view.GetComponentsInChildren<Renderer>(true))
                rig.RegisterViewRenderer(renderer);
            return (rig, view, visibleRenderer, offRenderer);
        }

        [Test]
        public void Rig_DeathHides_RespawnReleasesDeath_KeepsRespawnPreparing()
        {
            var (rig, _, visibleRenderer, offRenderer) = BuildRig();

            rig.ApplyDeathState();
            Assert.That(visibleRenderer.enabled, Is.False, "死亡当帧隐藏");
            Assert.That(offRenderer.enabled, Is.False);

            int generationBefore = rig.RespawnGenerationForTests;
            rig.ApplyRespawnState();
            Assert.That(rig.RespawnGenerationForTests, Is.GreaterThan(generationBefore), "复活必须推进代际（作废旧协程）");
            Assert.That(visibleRenderer.enabled, Is.False, "复活准备帧内保持隐藏（不闪死亡前姿态）");
            Assert.That(offRenderer.enabled, Is.False);

            // 模拟协程到点释放 RespawnPreparing（EditMode 不泵协程，直接走原因位）
            var visibility = (FPViewModelVisibility)typeof(FPWeaponRig)
                .GetField("_visibility", NonPublic).GetValue(rig);
            visibility.SetHidden(FPViewHideReason.RespawnPreparing, false);

            Assert.That(visibleRenderer.enabled, Is.True, "复活完成后作者可见网格恢复");
            Assert.That(offRenderer.enabled, Is.False, "作者关闭的网格保持关闭");
        }

        /// <summary>§7.3 池化与异步：复活下一帧前再次死亡 → 旧协程代际过期，不得恢复错误视图。</summary>
        [Test]
        public void Rig_DeathBeforeRespawnFrame_InvalidatesPendingRespawn()
        {
            var (rig, _, visibleRenderer, _) = BuildRig();

            rig.ApplyDeathState();
            rig.ApplyRespawnState(); // 排队复活恢复（代际 N）
            rig.ApplyDeathState();   // 复活帧到来前再次死亡（代际 N+1）

            var visibility = (FPViewModelVisibility)typeof(FPWeaponRig)
                .GetField("_visibility", NonPublic).GetValue(rig);
            Assert.That(visibility.HasReason(FPViewHideReason.Death), Is.True, "再次死亡后必须处于死亡隐藏");
            Assert.That(visibleRenderer.enabled, Is.False, "过期复活协程不得让枪在死亡期间复现");

            visibility.SetHidden(FPViewHideReason.Death, false);
            Assert.That(visibleRenderer.enabled, Is.False, "旧协程的 RespawnPreparing 未被新死亡清掉 → 仍隐藏");
            visibility.SetHidden(FPViewHideReason.RespawnPreparing, false);
            Assert.That(visibleRenderer.enabled, Is.True);
        }

        // ---- LaserSightBeam：真实 LateUpdate 的持续写者闸门 ----

        private (LaserSightBeam beam, LineRenderer line, FPWeaponRig rig, MeshRenderer visibleRenderer) BuildLaser()
        {
            var (rig, view, visibleRenderer, _) = BuildRig();
            var beamGo = new GameObject("LaserSight");
            beamGo.transform.SetParent(view.transform, false);
            _spawned.Add(beamGo);
            var beam = beamGo.AddComponent<LaserSightBeam>();
            beam.Setup();
            var line = beamGo.GetComponentInChildren<LineRenderer>(true);
            return (beam, line, rig, visibleRenderer);
        }

        private static void LateUpdate(Object behaviour)
            => typeof(LaserSightBeam).GetMethod("LateUpdate", NonPublic).Invoke(behaviour, null);

        [Test]
        public void Laser_AliveHipfire_Beams()
        {
            var (beam, line, _, _) = BuildLaser();
            LateUpdate(beam);
            Assert.That(line.enabled, Is.True, "活着+腰射+闸门放行 → 出束（既有语义保持）");
        }

        /// <summary>§7.4：死亡后**连续多帧** LateUpdate 都必须维持关线（不是只断言死亡入口关过一次）。</summary>
        [Test]
        public void Laser_Dead_StaysOff_EveryFrame()
        {
            var (beam, line, rig, _) = BuildLaser();
            LateUpdate(beam);
            Assert.That(line.enabled, Is.True);

            rig.ApplyDeathState();
            for (int frame = 0; frame < 4; frame++)
            {
                LateUpdate(beam);
                Assert.That(line.enabled, Is.False, $"死亡后第 {frame + 1} 帧 LateUpdate 不得重新开线");
            }
        }

        /// <summary>§7.4：隐藏期间换配件/激活缓存枪不能闪线；复活释放后恢复出束。</summary>
        [Test]
        public void Laser_ReenabledAfterRespawnRelease()
        {
            var (beam, line, rig, _) = BuildLaser();
            rig.ApplyDeathState();
            LateUpdate(beam);
            Assert.That(line.enabled, Is.False);

            rig.ApplyRespawnState(); // 复活准备帧内仍隐藏
            LateUpdate(beam);
            Assert.That(line.enabled, Is.False, "复活准备帧内不得闪线");

            var visibility = (FPViewModelVisibility)typeof(FPWeaponRig)
                .GetField("_visibility", NonPublic).GetValue(rig);
            visibility.SetHidden(FPViewHideReason.RespawnPreparing, false);
            LateUpdate(beam);
            Assert.That(line.enabled, Is.True, "全部原因清空且活着 → 恢复出束");
        }

        /// <summary>§7.4：无 Owner 上下文（无闸门实现者）不得默认出束。</summary>
        [Test]
        public void Laser_WithoutGateContext_DoesNotBeam()
        {
            var beamGo = new GameObject("OrphanLaser");
            _spawned.Add(beamGo);
            var beam = beamGo.AddComponent<LaserSightBeam>();
            beam.Setup();
            var line = beamGo.GetComponentInChildren<LineRenderer>(true);
            LateUpdate(beam);
            Assert.That(line.enabled, Is.False, "TP 视图/预览等无闸门上下文不得出束");
        }
    }
}
