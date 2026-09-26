using System.Linq;
using Game.Gameplay.Network;
using Game.Gameplay.Settings;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class VideoFollowupRegressionTests
    {
        [Test]
        public void CharacterBloodHasNoPersistentDecalAndExpiresAfterBurst()
        {
            const string path = "Assets/_Project/Art/FX/BloodImpact_LPFP.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var burst = prefab.GetComponentsInChildren<ParticleSystem>();
            Assert.IsNotEmpty(burst, "retain visible hit feedback");
            foreach (var particles in burst)
            {
                Assert.False(particles.main.loop);
                Assert.LessOrEqual(particles.main.startLifetime.constantMax, .25f);
            }
            var cleanup = new SerializedObject(prefab.GetComponents<MonoBehaviour>().Single(c => c.GetType().Name == "ImpactScript"));
            Assert.LessOrEqual(cleanup.FindProperty("despawnTimer").floatValue, 1f);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var active = instance.GetComponentsInChildren<ParticleSystem>();
                foreach (var particles in active) particles.Simulate(.05f, false, true, false);
                Assert.Greater(active.Sum(p => p.particleCount), 0, "the transient blood burst must still emit");
                foreach (var particles in active) particles.Simulate(.5f, false, true, false);
                Assert.AreEqual(0, active.Sum(p => p.particleCount), "no particle may remain at the standing hit position");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void EveryNativeWeaponUsesTheTransientCharacterBloodPrefab()
        {
            var expected = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/FX/BloodImpact_LPFP.prefab");
            var weapons = Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog").Entries.Where(e => e.IsLpfp).ToArray();
            Assert.AreEqual(16, weapons.Length);
            foreach (var weapon in weapons)
            {
                // Resolve through the definition so the test checks the actual equipped view.
                var go = weapon.definition.FirstPersonViewPrefab;
                var view = go.GetComponentInChildren<Game.Presentation.Weapon.WeaponView>(true);
                Assert.AreSame(expected, new SerializedObject(view).FindProperty("damagedImpactPrefab").objectReferenceValue, weapon.itemId);
            }
        }

        [Test]
        public void SniperMaterialPreservesTransparentPixelsAroundThinCrosshair()
        {
            var catalog = NativeScopeReticleCatalog.Load();
            var target = new RenderTexture(512, 512, 0);
            var pixels = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target; GL.Clear(true, true, new Color(.3f, .5f, .7f));
                Graphics.Blit(catalog.SniperTexture, target, catalog.SniperMaterial);
                RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); pixels.Apply();
                float background = pixels.GetPixel(32, 128).r;
                Assert.Greater(background, .1f);
                // The 1024px source is bilinearly reduced and read back in sRGB; edge coverage
                // remains visible without reaching 50% of the encoded background value.
                int dark = Enumerable.Range(0, 512).Count(x => pixels.GetPixel(x, 128).r < background * .9f);
                Assert.That(dark, Is.InRange(1, 4), "native crosshair must remain thin; imported transparent RGB must not become visible");
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); }
        }

        [TestCase("arena")]
        [TestCase("map_01")]
        [TestCase("map_02")]
        [TestCase("map_03")]
        [TestCase("map_04")]
        [TestCase("map_05")]
        public void RadarUsesChannelImageAndOriginalCameraProjection(string id)
        {
            var entry = Resources.Load<MapRadarCatalog>("MapRadarCatalog").Find(GameMapCatalog.ResolveSceneName(id));
            Assert.AreSame(Resources.Load<Sprite>("UI/Maps/" + id), entry.image);
            var go = new GameObject("ProjectionReference");
            var target = new RenderTexture((int)entry.image.rect.width, (int)entry.image.rect.height, 0);
            try
            {
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.targetTexture = target; // Match the preview capture, independent of editor viewport rounding.
                camera.orthographic = true; camera.orthographicSize = entry.halfHeight;
                camera.aspect = entry.image.rect.width / entry.image.rect.height;
                camera.transform.SetPositionAndRotation(entry.cameraPosition, entry.cameraRotation);
                foreach (var world in new[] { Vector3.zero, new Vector3(10, 2, -8), new Vector3(-10, 4, 12) })
                    Assert.Less(Vector2.Distance(entry.WorldToUv(world), camera.WorldToViewportPoint(world)), .00001f);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(target); }
        }

        [Test]
        public void SniperTextureIsLpfpSourceNotGeneratedCrosshair()
        {
            var catalog = NativeScopeReticleCatalog.Load();
            var source = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Low Poly FPS Pack/Components/Textures_&_Sprites/Scope_Textures/Sniper_01_Scope_Texture.png");
            Assert.AreSame(source, catalog.SniperTexture);
            Assert.AreSame(source, catalog.SniperMaterial.mainTexture);
            Assert.AreEqual("Game/UI/NativeSniperScope", catalog.SniperMaterial.shader.name);
        }

        [TestCase("attach.lpfp.optic.01")]
        [TestCase("attach.lpfp.optic.03")]
        public void AkLowPowerClampsAreCenteredAcrossTheRail(string id)
        {
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            var entry = catalog.Find(id);
            Assert.True(catalog.Calibration.TryGet("weapon.m4", id, out var offset, out var rotation, out var frame));
            var points = entry.prefab.GetComponentsInChildren<MeshFilter>(true).SelectMany(m => m.sharedMesh.vertices.Select(v =>
                entry.MountRotation * entry.prefab.transform.InverseTransformPoint(m.transform.TransformPoint(v)) + entry.mountOffset + offset)).ToArray();
            var foot = points.Where(p => p.y < points.Min(v => v.y) + .0016f).ToArray();
            Assert.That((foot.Min(p => p.z) + foot.Max(p => p.z)) * .5f, Is.EqualTo(0).Within(.0001f));
        }
    }
}
