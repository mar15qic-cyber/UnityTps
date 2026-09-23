using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Game.Core;
using Game.Account;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.Presentation.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class EveningPlaytestRegressionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static WeaponDefinition[] Definitions() => Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog")
            .Entries.Where(e => e.IsLpfp && e.definition != null).Select(e => e.definition).Distinct().ToArray();

        [Test]
        public void AllSixteenWeaponsUseOnlyNativeAudio()
        {
            var definitions = Definitions();
            Assert.AreEqual(16, definitions.Length);
            foreach (var d in definitions)
            {
                foreach (var entry in d.AudioProfile.FireVariants)
                    Assert.That(AssetDatabase.GetAssetPath(entry.Clip), Does.EndWith("Low Poly FPS Pack/Components/Audio/Shoot/shoot.wav"), d.name);
                foreach (var entry in d.AudioProfile.FireVariantsSuppressed)
                    Assert.That(AssetDatabase.GetAssetPath(entry.Clip), Does.EndWith("Low Poly FPS Pack/Components/Audio/Shoot/shoot_silencer.wav"), d.name);
                var serialized = new SerializedObject(d.AudioProfile);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is AudioClip clip)
                        Assert.That(AssetDatabase.GetAssetPath(clip), Does.StartWith("Assets/Low Poly FPS Pack/"), d.name + "/" + property.propertyPath);
            }
        }

        [TestCase("01"), TestCase("02"), TestCase("03"), TestCase("04"), TestCase("05")]
        public void SmgMuzzleMatchesNativeAnimatedWeaponBone(string number)
        {
            var d = Definitions().Single(x => x.name == "Day3_SMG" + number);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Arms/SMG_" + number + "_Example_Prefab/SMG_" + number + "_FPSController.prefab");
            var flash = source.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Muzzleflash Particles");
            var expected = flash.parent.parent.InverseTransformPoint(flash.position);
            var prefab = d.FirstPersonViewPrefab;
            var actual = prefab.transform.Find("Armature/weapon").InverseTransformPoint(prefab.GetComponent<WeaponView>().Muzzle.position);
            Assert.Less(Vector3.Distance(actual, expected), .0001f);
        }

        [TestCase("weapon.ak", "assault_rifle_02")]
        [TestCase("weapon.smg01", "smg_01")]
        [TestCase("weapon.smg02", "smg_02")]
        [TestCase("weapon.smg03", "smg_03")]
        [TestCase("weapon.smg04", "smg_04")]
        [TestCase("weapon.smg05", "smg_05")]
        public void OwnerTracerStartsAtVisibleBarrelRatherThanForwardFxMarker(string itemId, string meshName)
        {
            var d = Definitions().Single(x => x.CatalogItemId == itemId);
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
            try
            {
                var view = root.GetComponent<WeaponView>();
                var inset = (float)typeof(WeaponView).GetField("tracerMuzzleInsetMeters", Flags).GetValue(view);
                Assert.Greater(inset, 0f, itemId);
                var start = WeaponView.ResolveVisualTracerStart(view.Muzzle, Vector3.zero, inset);
                var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(x => x.name == meshName);
                var mesh = new Mesh();
                try
                {
                    renderer.BakeMesh(mesh);
                    var nearest = mesh.vertices.Min(p => Vector3.Distance(renderer.transform.TransformPoint(p), start));
                    Assert.Less(nearest, .026f, itemId);
                    Assert.Less(inset, .06f, itemId);
                }
                finally { Object.DestroyImmediate(mesh); }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [TestCase("sniper.01")]
        [TestCase("sniper.03")]
        public void NonM82SnipersHaveNativeReloadAnimation(string weaponId)
        {
            var d = Definitions().Single(x => x.WeaponId == weaponId);
            var clips = d.FirstPersonAnimations;
            Assert.IsTrue(clips.ReloadAmmoLeft != null || clips.ReloadOpen != null, weaponId);
            Assert.IsTrue(clips.ReloadOutOfAmmo != null || clips.ReloadInsert != null, weaponId);
        }

        [Test]
        public void ShotgunHasThreeAuthoredReloadSegmentsAndNinePellets()
        {
            var d = Definitions().Single(x => x.WeaponId == "shotgun.01");
            var clips = d.FirstPersonAnimations;
            foreach (var clip in new[] { clips.ReloadOpen, clips.ReloadInsert, clips.ReloadClose })
            {
                Assert.NotNull(clip);
                Assert.False(clip.name.StartsWith("__preview__"));
                Assert.Greater(AnimationUtility.GetCurveBindings(clip).Length, 10);
            }
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>("Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            var host = new GameObject("ShotgunBalanceCheck");
            try
            {
                var weapon = host.AddComponent<WeaponController>();
                weapon.Initialize(d, balance);
                Assert.AreEqual(9, weapon.Stat.Ballistic.PelletCount);
                Assert.Greater(weapon.Stat.Ballistic.PelletSpread, 0f);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void SameOpticReapplicationRebuildsDestroyedLensAcrossAllWeapons()
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var optic = catalog.Find("attach.lpfp.optic.01");
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>("Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            foreach (var d in Definitions())
            {
                var view = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
                var host = new GameObject("ScopeLifetimeCheck");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, view.scene);
                try
                {
                    var weapon = host.AddComponent<WeaponController>();
                    weapon.Initialize(d, balance);
                    weapon.SetAttachments(new[] { optic });
                    var aim = host.AddComponent<PlayerAimState>();
                    var rig = host.AddComponent<FPWeaponRig>();
                    typeof(FPWeaponRig).GetField("_activeView", Flags).SetValue(rig, view);
                    var scope = host.GetComponent<PhysicalScopeView>() ?? host.AddComponent<PhysicalScopeView>();
                    typeof(PhysicalScopeView).GetField("_rig", Flags).SetValue(scope, rig);
                    typeof(PhysicalScopeView).GetField("_controller", Flags).SetValue(scope, weapon);
                    typeof(PhysicalScopeView).GetField("_aimState", Flags).SetValue(scope, aim);
                    var attachments = view.GetComponent<WeaponAttachmentView>() ?? view.AddComponent<WeaponAttachmentView>();
                    for (int round = 0; round < 3; round++)
                    {
                        attachments.ApplyAttachments(catalog, d.CatalogItemId, new[] { optic }, false);
                        typeof(PhysicalScopeView).GetMethod("Update", Flags).Invoke(scope, null);
                        var reticle = (GameObject)typeof(PhysicalScopeView).GetField("_reticleNode", Flags).GetValue(scope);
                        Assert.IsTrue(reticle != null && reticle.activeInHierarchy, d.name + " round=" + round);
                        Assert.That(reticle.transform.IsChildOf(attachments.FindSpawned(optic.itemId)), Is.True);
                        Assert.IsTrue(scope.HandlesOptic(optic.itemId), d.name + " round=" + round);
                        var material = reticle.GetComponent<MeshRenderer>().sharedMaterial;
                        Assert.AreEqual("Game/UI/NativeSniperScope", material.shader.name);
                        Assert.AreEqual((int)UnityEngine.Rendering.CompareFunction.Always, material.GetInt("_ZTest"));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(host);
                    PrefabUtility.UnloadPrefabContents(view);
                }
            }
        }

        [Test]
        public async Task FailedReturnAckStillStopsBattleBeforeNavigation()
        {
            var calls = new System.Collections.Generic.List<string>();
            await new MatchReturnSequence().RunAsync("room", "match", 7, new MatchReturnSequence.Deps
            {
                SnapshotSession = () => new MatchReturnSequence.SessionSnapshot("room", 7),
                QueryResultOnce = () => Task.FromResult((true, new RoomMatchResultViewDto { status = "Final" })),
                AckReturn = () => Task.FromResult(false),
                Delay = () => Task.CompletedTask,
                StopBattleConnection = () => calls.Add("stop"),
                ClearLaunchContext = () => calls.Add("clear"),
                NavigateToLobby = () => calls.Add("navigate"),
            });
            CollectionAssert.AreEqual(new[] { "stop", "clear", "navigate" }, calls);
        }

        [Test]
        public async Task ThrowingReturnRequestsStillCleanUpAfterBoundedRetries()
        {
            int queries = 0;
            var calls = new System.Collections.Generic.List<string>();
            await new MatchReturnSequence().RunAsync("room", "match", 7, new MatchReturnSequence.Deps
            {
                SnapshotSession = () => new MatchReturnSequence.SessionSnapshot("room", 7),
                QueryResultOnce = () => { queries++; throw new System.OperationCanceledException(); },
                AckReturn = () => throw new System.OperationCanceledException(),
                Delay = () => Task.CompletedTask,
                StopBattleConnection = () => calls.Add("stop"),
                ClearLaunchContext = () => calls.Add("clear"),
                NavigateToLobby = () => calls.Add("navigate"),
            });
            Assert.AreEqual(3, queries);
            CollectionAssert.AreEqual(new[] { "stop", "clear", "navigate" }, calls);
        }

        [Test]
        public void NinePelletShotCreatesNineDistinctWallImpactsAndCarriesNormals()
        {
            var host = new GameObject("PelletFxCheck");
            var template = new GameObject("EveningPelletImpact");
            try
            {
                var view = host.AddComponent<WeaponView>();
                typeof(WeaponView).GetField("_muzzleLight", Flags).SetValue(view, host.AddComponent<Light>());
                typeof(WeaponView).GetField("impactPrefab", Flags).SetValue(view, template);
                var pellets = Enumerable.Range(0, 9).Select(i => new HitscanResult(true, false,
                    new Vector3(i * .1f, 1f, 10f), Vector3.back, null)).ToArray();
                var shot = new WeaponShot(Vector3.zero, Vector3.forward, Vector3.forward,
                    pellets[0], 3f, default, 0, 123, pellets);
                typeof(WeaponView).GetMethod("HandleShot", Flags).Invoke(view, new object[] { shot, 0u });
                var impacts = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                    .Where(t => t.name == "EveningPelletImpact(Clone)").OrderBy(t => t.position.x).ToArray();
                Assert.AreEqual(9, impacts.Length);
                var packet = RemoteShotPresentation.FromShot(shot, 42);
                for (int i = 0; i < 9; i++)
                {
                    Assert.Less(Vector3.Distance(impacts[i].position, pellets[i].Point + Vector3.back * .01f), .0001f);
                    Assert.AreEqual(pellets[i].Normal, packet.PelletNormals[i]);
                    Assert.AreEqual(pellets[i].Point, packet.PelletPoints[i]);
                    Assert.IsTrue(packet.PelletHits[i]);
                    Assert.IsFalse(packet.PelletCharacters[i]);
                }
            }
            finally
            {
                foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                    .Where(t => t.name == "EveningPelletImpact(Clone)")) Object.DestroyImmediate(t.gameObject);
                var view = host.GetComponent<WeaponView>();
                var field = typeof(WeaponView).GetField("_tracerMaterial", Flags);
                var material = (Material)field.GetValue(view);
                field.SetValue(view, null);
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(template);
            }
        }
    }
}
