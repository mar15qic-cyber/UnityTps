using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    public sealed class OpticViewCatalogTests
    {
        [TestCase(OpticAimTier.RedDot, OpticPresentationMode.Physical1x)]
        [TestCase(OpticAimTier.Holo, OpticPresentationMode.Physical1x)]
        [TestCase(OpticAimTier.LowZoom, OpticPresentationMode.MagnifiedOverlay)]
        [TestCase(OpticAimTier.HighZoom, OpticPresentationMode.MagnifiedOverlay)]
        public void MissingProfileFallsBackByTier(OpticAimTier tier, OpticPresentationMode expected)
        {
            var catalog = UnityEngine.ScriptableObject.CreateInstance<OpticViewCatalog>();
            try
            {
                Assert.IsTrue(catalog.TryGet("test.optic", tier, out var profile));
                Assert.AreEqual(expected, profile.mode);
                Assert.Greater(profile.reticleSize, 0f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void NoneTierHasNoProfile()
        {
            var catalog = UnityEngine.ScriptableObject.CreateInstance<OpticViewCatalog>();
            try { Assert.IsFalse(catalog.TryGet(null, OpticAimTier.None, out _)); }
            finally { UnityEngine.Object.DestroyImmediate(catalog); }
        }

        [Test]
        public void PassOpticAliasesHaveVisibleModelsAndStableIds()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(
                "Assets/_Project/Resources/AttachmentAssetCatalog.asset");
            Assert.NotNull(catalog, "配件目录缺失");
            var rifle = catalog.Find("attach.rifle.optic");
            var pistol = catalog.Find("attach.pistol.optic");
            Assert.NotNull(rifle);
            Assert.NotNull(pistol);
            Assert.IsTrue(rifle.HasModel, "步枪通行证瞄具必须有模型");
            Assert.IsTrue(pistol.HasModel, "手枪通行证瞄具必须有模型");
            Assert.AreEqual(OpticAimTier.Holo, rifle.aimTier);
            Assert.AreEqual(OpticAimTier.RedDot, pistol.aimTier);
        }

        [Test]
        public void BuiltInSnipersExposeFixedHighZoomProfiles()
        {
            foreach (var path in new[]
            {
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Sniper01.asset",
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Sniper02.asset",
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Sniper03.asset"
            })
            {
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                Assert.NotNull(definition, path);
                Assert.IsTrue(definition.BuiltInOptic.IsValid, path);
                Assert.AreEqual(OpticAimTier.HighZoom, definition.BuiltInOptic.aimTier, path);
                Assert.AreEqual(12f, definition.BuiltInOptic.adsFovOverride, path);
            }
        }

        [Test]
        public void OpticAdsViewBuildsValidCanvasGraphicHierarchy()
        {
            var hud = new GameObject("WeaponHudCanvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var view = hud.AddComponent<OpticAdsView>();
                if (hud.transform.Find("OpticScopeVignette") == null)
                {
                    var build = typeof(OpticAdsView).GetMethod("BuildRuntimeUi",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Assert.NotNull(build);
                    build.Invoke(view, null);
                }

                var scope = hud.transform.Find("OpticScopeVignette");
                var reticle = hud.transform.Find("OpticReticle");
                Assert.NotNull(scope);
                Assert.NotNull(reticle);
                Assert.NotNull(scope.GetComponent<CanvasRenderer>());
                Assert.NotNull(scope.Find("Lens")?.GetComponent<CanvasRenderer>());
                Assert.NotNull(reticle.Find("Tactical")?.GetComponent<CanvasRenderer>());
                Assert.NotNull(reticle.Find("Tactical")?.GetComponent<TacticalOpticReticleGraphic>());
                Assert.AreEqual(1, reticle.GetComponentsInChildren<TacticalOpticReticleGraphic>(true).Length);
                Assert.IsFalse(scope.GetComponent<CanvasGroup>().blocksRaycasts);
                Assert.IsFalse(reticle.GetComponent<CanvasGroup>().blocksRaycasts);
                Assert.IsFalse(scope.GetComponent<OpticVignetteGraphic>().raycastTarget);
            }
            finally
            {
                Object.DestroyImmediate(hud);
            }
        }

    }
}
