using System.Linq;
using System.Reflection;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    public sealed class LPWDualLayerCalibrationTests
    {
        private const string ManifestPath = "Assets/_Project/ScriptableObjects/Weapons/LPW/LPWWeaponManifest.asset";

        [Test]
        public void Manifest_IsSchemaSevenAndKeepsAllTwentyNineRows()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.SchemaVersion, Is.EqualTo(7));
            Assert.That(manifest.Weapons.Count, Is.EqualTo(29));

            Assert.That(manifest.Weapons.Select(x => x.definitionId).Distinct().Count(), Is.EqualTo(29));
            Assert.That(manifest.Weapons.All(x => x.schemaVersion == 7), Is.True);
        }

        [Test]
        public void IncompleteAnchoredRowsAreNeverAdvertisedAsComplete()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            foreach (LPWWeaponSpec spec in manifest.Weapons.Where(x => x.poseCalibrationMode == LPWPoseCalibrationMode.AnchoredDualPoseV2))
            {
                Assert.That(spec.hasGripCalibration, Is.True, spec.definitionId);
                Assert.That(spec.hasAdsCalibration, Is.True, spec.definitionId);
                Assert.That(spec.hasSightCalibration && spec.hasElbowPoleHints, Is.True, spec.definitionId);
                Assert.That(spec.adsFirePresentationMode, Is.EqualTo(LPWAdsFirePresentationMode.ProceduralOnly), spec.definitionId);
            }
        }

        [Test]
        public void EveryFormalPrefabUsesTheDynamicSightAxisContract()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            foreach (LPWWeaponSpec spec in manifest.Weapons)
            {
                string token = System.IO.Path.GetFileNameWithoutExtension(spec.sourcePrefabPath);
                string path = "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_" + token + "_View.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, spec.definitionId);
                FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
                Assert.That(profile, Is.Not.Null, spec.definitionId);
                Assert.That(profile.HasRootCalibration && profile.HasCompleteInterfaceLayout, Is.True, spec.definitionId);
                if (profile.IsAnchoredDualPoseV2)
                {
                    Assert.That(profile.HasCompleteAnchoredDualPoseV2, Is.True, spec.definitionId);
                    Assert.That(prefab.GetComponent<LPWGunPoseDriver>(), Is.Not.Null, spec.definitionId);
                    Assert.That(prefab.GetComponent<FPRightHandIK>(), Is.Not.Null, spec.definitionId);
                }
                Game.Presentation.Weapon.WeaponView view = prefab.GetComponent<Game.Presentation.Weapon.WeaponView>();
                Assert.That(view, Is.Not.Null, spec.definitionId);
                Assert.That(view.AlignAdsToSightAxis && view.SightReference != null, Is.True, spec.definitionId);
            }
        }

        [Test]
        public void AnimationFamilyRulesRemainExplicit()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            LPWWeaponSpec g36 = manifest.Weapons.Single(x => x.definitionId == "lpw.rifle.03");
            Assert.That(g36.animationFamily, Is.EqualTo(FirstPersonAnimationFamily.Rifle01));

            foreach (LPWWeaponSpec sniper in manifest.Weapons.Where(x => x.category == WeaponCatalogCategory.Sniper))
                Assert.That(sniper.animationDefinitionId,
                    Is.EqualTo(sniper.tier == 1 || sniper.tier == 3 ? "sniper.01" : "sniper.02"));
        }

        [Test]
        public void AugRearMagazine_PersistsRifle03ReferenceAndContactWindows()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            LPWWeaponSpec aug = manifest.Weapons.Single(x => x.definitionId == "lpw.rifle.02");

            Assert.That(aug.animationFamily, Is.EqualTo(FirstPersonAnimationFamily.Rifle03));
            Assert.That(aug.fpAnimationFamilyContactOffsets.Any(x => x.configured
                && x.family == FPAnimationReferenceFamily.Rifle03), Is.True);
            Assert.That(aug.magazineReachWindow, Is.GreaterThan(0f));
            Assert.That(aug.magazineInsertWindow, Is.GreaterThan(0f));
            Assert.That(aug.hasMagazineInsertGuide, Is.True);
            Assert.That(aug.magazineAlignWindow, Is.GreaterThan(0f));

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_AssaultRifle2_01_View.prefab");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero,
                "Missing scripts make PrefabUtility reject Save Idle + Explicit Interfaces.");
            FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
            Assert.That(profile.MagazineInsertGuide, Is.Not.Null);
            Assert.That(profile.MagazineAlignWindow, Is.EqualTo(aug.magazineAlignWindow).Within(.0001f));
            Assert.That(profile.MagazineInsertWindow, Is.EqualTo(aug.magazineInsertWindow).Within(.0001f));
        }

        [Test]
        public void AdsPoseMath_AlignsSightAxisAndAnchorsRightGripWithRollFreedom()
        {
            GameObject owner = new("AdsSolverConstraintTest");
            owner.SetActive(false);
            try
            {
                owner.AddComponent<PlayerAimState>();
                GameObject cameraObject = new("FP View Camera");
                cameraObject.transform.SetParent(owner.transform, false);
                UnityEngine.Camera camera = cameraObject.AddComponent<UnityEngine.Camera>();
                camera.cullingMask = ~0;

                GameObject viewObject = new("TestView");
                viewObject.transform.SetParent(owner.transform, false);
                Transform weaponRoot = NewBone("LPW_Gun", viewObject.transform,
                    new Vector3(0.25f, -0.15f, 2f));
                Transform rightHand = NewBone("hand_R", viewObject.transform,
                    new Vector3(-0.10f, 0.05f, 2f));
                rightHand.localRotation = Quaternion.Euler(0f, 0f, 35f);
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot,
                    new Vector3(0.20f, 0f, 0f));
                Transform rear = NewBone("RearSight", weaponRoot, new Vector3(0f, 0.1f, 0f));
                Transform front = NewBone("FrontSight", weaponRoot, new Vector3(0f, 0.1f, 1f));
                var profile = viewObject.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "rearSight", rear);
                SetField(profile, "frontSight", front);

                owner.SetActive(true);
                Transform parent = weaponRoot.parent;
                Quaternion gripLocalRotation = Quaternion.Inverse(weaponRoot.rotation)
                    * rightGrip.rotation;
                Quaternion solvedWorldRotation = FPWeaponPoseMath.SolveSightAxisWithGripRoll(
                    front.position - rear.position,
                    camera.transform.forward,
                    weaponRoot.rotation,
                    gripLocalRotation,
                    rightHand.rotation,
                    true,
                    out float gripRotationError);
                Quaternion localRotation = Quaternion.Inverse(parent.rotation) * solvedWorldRotation;
                Vector3 gripInWeapon = weaponRoot.InverseTransformPoint(rightGrip.position);
                Vector3 handInParent = parent.InverseTransformPoint(rightHand.position);
                Vector3 localPosition = handInParent - localRotation * gripInWeapon;
                weaponRoot.localPosition = localPosition;
                weaponRoot.localRotation = localRotation;
                Assert.That(Vector3.Distance(rightGrip.position, rightHand.position),
                    Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(rightGrip.rotation, rightHand.rotation),
                    Is.LessThan(0.6f));
                Assert.That(gripRotationError, Is.LessThan(0.6f));
                Assert.That(Vector3.Angle(front.position - rear.position,
                    camera.transform.forward), Is.LessThan(0.001f));
                // The returned translation is a grip solve.  Forward depth is
                // intentionally left for the separate root camera-plane correction.
                Assert.That(Mathf.Abs(localPosition.z), Is.GreaterThan(0.1f));
                Assert.That(parent, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        private static Transform NewBone(string name, Transform parent, Vector3 localPosition)
        {
            GameObject child = new(name);
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            return child.transform;
        }

        private static void SetField(Object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }
    }
}
