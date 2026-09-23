using System.Linq;
using System.Reflection;
using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay.Tests
{
    public sealed class TacticalLobbyAssetTests
    {
        [Test]
        public void EveryProductionWeaponHasPackagedIconAndThirdPersonModel()
        {
            var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(catalog, Is.Not.Null);
            foreach (var entry in catalog.Entries.Where(x => x.IsLpfp))
            {
                Assert.That(entry.icon, Is.Not.Null, entry.itemId);
                Assert.That(entry.definition?.ThirdPersonViewPrefab, Is.Not.Null, entry.itemId);
            }
            var clip = Resources.Load<AnimationClip>("UI/LobbyRelaxed");
            Assert.That(clip, Is.Not.Null);
            Assert.That(clip.isLooping, Is.True);
            var model = Resources.Load<GameObject>("UI/LobbyCharacter");
            Assert.That(model.GetComponentsInChildren<MonoBehaviour>(true), Is.Empty, "Display source must not contain combat/network behaviours");
        }

        [Test]
        public void RoomMaterialsHavePackagedShaderDependencies()
        {
            foreach (var kind in new[] { "Lit", "Unlit" })
            {
                var material = Resources.Load<Material>("UI/LobbyRoom" + kind);
                Assert.That(material, Is.Not.Null, kind);
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/" + kind));
                var dependencies = UnityEditor.AssetDatabase.GetDependencies(UnityEditor.AssetDatabase.GetAssetPath(material));
                Assert.That(dependencies, Does.Contain(UnityEditor.AssetDatabase.GetAssetPath(material.shader)));
            }
        }

        [Test]
        public void MissingMaterialDisposesPartialStageWithoutLeavingWhiteImage_AndCanRetry()
        {
            var go = new GameObject("FailedPreviewTest", typeof(RectTransform), typeof(RawImage));
            try
            {
                var preview = go.AddComponent<LobbyCharacterPreview>();
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Missing or unsupported lobby room material: Unlit"));
                typeof(LobbyCharacterPreview).GetMethod("InitializeWithMaterials", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(preview, new object[] { true, Resources.Load<Material>("UI/LobbyRoomLit"), null });
                Assert.That(preview.Error, Is.Not.Empty);
                Assert.That(go.GetComponent<RawImage>().texture, Is.Null);
                Assert.That(go.GetComponent<RawImage>().color.a, Is.Zero);
                Assert.That(GameObject.Find("LobbyDisplayStage"), Is.Null);
                preview.Initialize(true);
                Assert.That(preview.Error, Is.Null);
                Assert.That(go.GetComponent<RawImage>().texture, Is.Not.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase("weapon.m4")]
        [TestCase("weapon.smg01")]
        [TestCase("weapon.shotgun01")]
        [TestCase("weapon.sniper01")]
        [TestCase("weapon.ak")]
        [TestCase("weapon.smg03")]
        [TestCase("weapon.smg05")]
        public void RelaxedIdleAlwaysPreservesAuthoredLeftArm(string weaponId)
        {
            var go = new GameObject("RelaxedGripTest", typeof(RectTransform), typeof(RawImage));
            try
            {
                var preview = go.AddComponent<LobbyCharacterPreview>();
                preview.Initialize();
                preview.ApplyLoadout(new LoadoutDto { primaryWeaponId = weaponId }, Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog"));
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var animator = (Animator)typeof(LobbyCharacterPreview).GetField("animator", flags).GetValue(preview);
                Assert.That(animator.GetComponentsInChildren<Game.Presentation.Animation.TPLeftHandIK>(true), Is.Empty);
                var apply = typeof(LobbyCharacterPreview).GetMethod("LateUpdate", flags);
                var clip = Resources.Load<AnimationClip>("UI/LobbyRelaxed");
                var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                foreach (var time in new[] { 1f, 5f, 9f })
                {
                    clip.SampleAnimation(animator.gameObject, time);
                    var authoredPosition = hand.position;
                    var authoredRotation = hand.rotation;
                    var authoredElbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm).rotation;
                    apply.Invoke(preview, null);
                    Assert.That(Quaternion.Angle(hand.rotation, authoredRotation), Is.LessThan(0.2f), "Do not force combat grip rotation onto relaxed wrist");
                    Assert.That(preview.LeftHandGripWeight, Is.Zero);
                    Assert.That(Vector3.Distance(hand.position, authoredPosition), Is.LessThan(0.001f));
                    Assert.That(Quaternion.Angle(animator.GetBoneTransform(HumanBodyBones.LeftLowerArm).rotation, authoredElbow), Is.LessThan(0.2f));
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase("weapon.m4")]
        [TestCase("weapon.smg01")]
        [TestCase("weapon.shotgun01")]
        [TestCase("weapon.sniper01")]
        public void PreviewPreservesMountPoseAndReleasesItsStage(string weaponId)
        {
            var go = new GameObject("PreviewTest", typeof(RectTransform), typeof(RawImage));
            GameObject stage = null;
            RenderTexture texture = null;
            try
            {
                var preview = go.AddComponent<LobbyCharacterPreview>();
                preview.Initialize(true);
                texture = (RenderTexture)go.GetComponent<RawImage>().texture;
                Assert.That(texture.IsCreated(), Is.True);
                var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
                var loadout = new LoadoutDto { primaryWeaponId = weaponId, version = 1 };
                preview.ApplyLoadout(loadout, catalog);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var weapon = (GameObject)typeof(LobbyCharacterPreview).GetField("weapon", flags).GetValue(preview);
                stage = (GameObject)typeof(LobbyCharacterPreview).GetField("stage", flags).GetValue(preview);
                Assert.That(weapon, Is.Not.Null);
                catalog.TryGet(weaponId, out var entry);
                Assert.That(weapon.transform.localPosition, Is.EqualTo(entry.definition.ThirdPersonViewPrefab.transform.localPosition));
                preview.ApplyLoadout(loadout, catalog);
                Assert.That(typeof(LobbyCharacterPreview).GetField("weapon", flags).GetValue(preview), Is.SameAs(weapon));
            }
            finally { Object.DestroyImmediate(go); }
            Assert.That(texture == null, Is.True, "The page must release its render texture");
            Assert.That(stage == null, Is.True, "The display stage must be disposed with its owning page");
        }

        [Test]
        public void SavedForegripDoesNotInstallLobbyIK()
        {
            var go = new GameObject("ForegripPreviewTest", typeof(RectTransform), typeof(RawImage));
            try
            {
                var preview = go.AddComponent<LobbyCharacterPreview>();
                preview.Initialize();
                preview.ApplyLoadout(new LoadoutDto { primaryWeaponId = "weapon.smg03", attachments = new[] {
                    new LoadoutAttachmentDto { weaponSlot = "Primary", attachmentItemId = "attach.lpw.grip.01" } } },
                    Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog"));
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var animator = (Animator)typeof(LobbyCharacterPreview).GetField("animator", flags).GetValue(preview);
                var clip = Resources.Load<AnimationClip>("UI/LobbyRelaxed");
                var apply = typeof(LobbyCharacterPreview).GetMethod("LateUpdate", flags);
                clip.SampleAnimation(animator.gameObject, 1f);
                apply.Invoke(preview, null);
                Assert.That(animator.GetComponentsInChildren<Game.Presentation.Animation.TPLeftHandIK>(true), Is.Empty);
                clip.SampleAnimation(animator.gameObject, 5f);
                apply.Invoke(preview, null);
                Assert.That(preview.LeftHandGripWeight, Is.Zero);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
