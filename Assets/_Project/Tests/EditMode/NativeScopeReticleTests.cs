using System.Linq;
using Game.Gameplay.Settings;
using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class NativeScopeReticleTests
    {
        private static readonly string[] Guns={"weapon.m4","weapon.ak","weapon.rifle03","weapon.smg01","weapon.smg02",
            "weapon.smg03","weapon.smg04","weapon.smg05","weapon.shotgun01"};
        private static System.Collections.IEnumerable BasicCombos()
        {
            foreach(var gun in Guns)
            foreach(var optic in new[]{"attach.rifle.optic","attach.lpfp.optic.02"})
                yield return new TestCaseData(gun,optic);
        }

        [TestCaseSource(nameof(BasicCombos))]
        public void ProductionMotionKeepsBasicWindowCenteredThroughRepeatedRecoil(string id,string opticId)
        {
            var wc=Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");
            var ac=Resources.Load<Game.Gameplay.Weapon.AttachmentAssetCatalog>("AttachmentAssetCatalog");
            var definition=wc.Entries.First(e=>e.itemId==id).definition;
            var view=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
            var host=new GameObject("ReticleConstraintTest");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host,view.scene);
            try
            {
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var weapon=host.AddComponent<Game.Gameplay.Weapon.WeaponController>();
                typeof(Game.Gameplay.Weapon.WeaponController).GetField("definition",flags).SetValue(weapon,definition);
                var source=(Game.Gameplay.Weapon.AttachmentStatModifierSource)typeof(Game.Gameplay.Weapon.WeaponController)
                    .GetField("_attachmentSource",flags).GetValue(weapon);
                var entry=ac.Find(opticId);source.Reset(new[]{entry});
                var aim=host.AddComponent<Game.Gameplay.Player.PlayerAimState>();
                typeof(Game.Gameplay.Player.PlayerAimState).GetProperty("Ads01").SetValue(aim,1f);
                var rig=host.AddComponent<FPCameraRig>();
                typeof(FPCameraRig).GetField("aimState",flags).SetValue(rig,aim);
                var cameraObject=new GameObject("FP Test Camera");cameraObject.transform.SetParent(host.transform,false);
                var camera=cameraObject.AddComponent<UnityEngine.Camera>();camera.enabled=false;camera.nearClipPlane=.01f;
                camera.transform.localPosition=new Vector3(.01f,.08f,-.2f);
                var root=new GameObject("FP Weapon Root");root.transform.SetParent(host.transform,false);
                view.transform.SetParent(root.transform,false);
                var motion=root.AddComponent<FPWeaponMotion>();
                // EditMode does not run the normal scene Awake/binding lifecycle.
                typeof(FPWeaponMotion).GetField("_weapon",flags).SetValue(motion,weapon);
                typeof(FPWeaponMotion).GetField("_rig",flags).SetValue(motion,rig);
                typeof(FPWeaponMotion).GetField("_viewCamera",flags).SetValue(motion,camera);
                var attachments=view.GetComponent<Game.Gameplay.Weapon.WeaponAttachmentView>()??view.AddComponent<Game.Gameplay.Weapon.WeaponAttachmentView>();
                attachments.ApplyAttachments(ac,id,new[]{entry},false);
                ac.Calibration.TryGetOpticAim(id,entry.itemId,out var data);
                var socket=attachments.GetSocketTransform(Game.Gameplay.Weapon.AttachmentSlotType.Optic);
                for(int frame=0;frame<32;frame++)
                {
                    definition.FirstPersonAnimations.AimIdle.SampleAnimation(view,0);
                    host.transform.rotation=Quaternion.Euler(frame-15,frame*3,0);
                    typeof(FPWeaponMotion).GetField("_recoilRotation",flags).SetValue(motion,new Vector3(-frame%6,frame%3,frame%2));
                    typeof(FPWeaponMotion).GetField("_recoilPosition",flags).SetValue(motion,new Vector3(.004f,0,-.02f));
                    typeof(FPWeaponMotion).GetMethod("LateUpdate",flags).Invoke(motion,null);
                    Vector3 viewport=camera.WorldToViewportPoint(socket.TransformPoint(data.WindowCenterLocal));
                    // Unity's CPU viewport API includes a subpixel raster-center offset in
                    // this editor. Compare with the same camera's actual forward-ray projection.
                    Vector3 aimViewport=camera.WorldToViewportPoint(camera.transform.position+camera.transform.forward);
                    Assert.That(viewport.z,Is.GreaterThan(camera.nearClipPlane));
                    Assert.That(viewport.x,Is.EqualTo(aimViewport.x).Within(.0001f),id+" frame="+frame);
                    Assert.That(viewport.y,Is.EqualTo(aimViewport.y).Within(.0001f),id+" frame="+frame);
                    Assert.That(root.transform.localPosition.magnitude,Is.LessThan(1f));
                }
            }
            finally {view.transform.SetParent(null);PrefabUtility.UnloadPrefabContents(view);if(host!=null)Object.DestroyImmediate(host);}
        }

        [Test]
        public void EverySelectablePairUsesAnOriginalTextureAndAdditiveMaterial()
        {
            var catalog = NativeScopeReticleCatalog.Load();
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Entries.Length, Is.EqualTo(8));
            Assert.That(catalog.AdditiveMaterial.shader.name, Is.EqualTo("Game/UI/NativeScopeReticle"));
            Assert.That(ShaderUtil.ShaderHasError(catalog.AdditiveMaterial.shader), Is.False);
            int count=0;
            for(int i=0;i<SettingsModel.ReticleStyleCount;i++)
            foreach(var color in SettingsModel.ReticleColors((OpticReticleStyle)i))
            {
                var entry = catalog.Find((OpticReticleStyle)i, color);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry.texture, Is.Not.Null);
                Assert.That(entry.color, Is.EqualTo(color));
                Assert.That(AssetDatabase.GetAssetPath(entry.texture), Does.StartWith("Assets/Low Poly FPS Pack/Components/Textures_&_Sprites/Scope_Textures/"));
                Assert.That(entry.aimUv.x, Is.InRange(0f,1f));
                Assert.That(entry.aimUv.y, Is.InRange(0f,1f));
                count++;
            }
            Assert.That(count, Is.EqualTo(8));
        }

        [Test]
        public void LegacyColorsAndStyleChangesResolveToAvailableNativePairs()
        {
            Assert.That(SettingsModel.NormalizeReticleColor(OpticReticleStyle.Chevron,OpticReticleColor.Cyan),Is.EqualTo(OpticReticleColor.Blue));
            Assert.That(SettingsModel.NormalizeReticleColor(OpticReticleStyle.CircleDot,OpticReticleColor.Green),Is.EqualTo(OpticReticleColor.Red));
            Assert.That(SettingsModel.NormalizeReticleColor(OpticReticleStyle.Dot,OpticReticleColor.White),Is.EqualTo(OpticReticleColor.Red));
            Assert.That(SettingsModel.NormalizeReticleColor(OpticReticleStyle.ThreePost,OpticReticleColor.Red),Is.EqualTo(OpticReticleColor.Blue));
            Assert.That(SettingsModel.NextReticleColor(OpticReticleStyle.Diamond,OpticReticleColor.Blue),Is.EqualTo(OpticReticleColor.Orange));
        }

        [TestCase(0f,0f)]
        [TestCase(35f,140f)]
        [TestCase(-60f,-90f)]
        public void FinalWindowConstraintPreservesDepthAndCentersAfterRecoil(float pitch,float yaw)
        {
            var camera = new CameraProjection { Position = new Vector3(10,2,-4), Rotation = Quaternion.Euler(pitch,yaw,0),
                FovDegrees=60,Aspect=16f/9f,NearClip=.01f,FarClip=1000 };
            var window = camera.Position + camera.Rotation * new Vector3(.012f,-.008f,.35f);
            var delta = OpticAimGeometry.CenterWindowOnAimRay(window,camera);
            var viewport = camera.ProjectToViewport(window+delta);
            Assert.That(viewport.x,Is.EqualTo(.5f).Within(1e-5f));
            Assert.That(viewport.y,Is.EqualTo(.5f).Within(1e-5f));
            Assert.That(Vector3.Dot(delta,camera.Forward),Is.EqualTo(0).Within(1e-5f),"must not move arms towards the near clip");
            Assert.That(OpticAimGeometry.CenterWindowOnAimRay(window+delta,camera).magnitude,Is.LessThan(1e-5f));
            Assert.That(OpticAimGeometry.CenterWindowOnAimRay(camera.Position-camera.Forward,camera),Is.EqualTo(Vector3.zero));
        }

        [TestCaseSource(nameof(BasicCombos))]
        public void EveryBasicCalibrationUsesItsActualPrefabApertureCenter(string id,string opticId)
        {
            var wc=Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");
            var ac=Resources.Load<Game.Gameplay.Weapon.AttachmentAssetCatalog>("AttachmentAssetCatalog");
            var w=wc.Entries.First(e=>e.itemId==id);
            var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.FirstPersonViewPrefab));
            try
            {
                var view=root.GetComponent<Game.Gameplay.Weapon.WeaponAttachmentView>()??root.AddComponent<Game.Gameplay.Weapon.WeaponAttachmentView>();
                view.ApplyAttachments(ac,w.itemId,new[]{ac.Find(opticId)},false);
                Assert.That(ac.Calibration.TryGetOpticAim(w.itemId,opticId,out var data),Is.True);
                var optic=view.FindSpawned(opticId);
                var socket=view.GetSocketTransform(Game.Gameplay.Weapon.AttachmentSlotType.Optic);
                Vector3 actual=optic.InverseTransformPoint(socket.TransformPoint(data.WindowCenterLocal));
                var expected=opticId=="attach.rifle.optic"?new Vector3(0,.02353f,.02811f):new Vector3(0,.01897f,-.02876f);
                Assert.That(Vector3.Distance(actual,expected),Is.LessThan(.00001f));
            }
            finally {PrefabUtility.UnloadPrefabContents(root);}
        }
    }
}
