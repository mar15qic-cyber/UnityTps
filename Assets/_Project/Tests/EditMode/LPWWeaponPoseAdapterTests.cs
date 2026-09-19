using System.Reflection;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class LPWWeaponPoseAdapterTests
    {
        [Test]
        public void FirstPersonLeftHandIk_ReachesWeaponSpecificTarget()
        {
            var root = new GameObject("FPLeftHandIK_TestRoot");
            root.SetActive(false);

            try
            {
                Transform upper = NewBone("arm_L", root.transform, Vector3.zero);
                Transform lower = NewBone("lower_arm_L", upper, new Vector3(1f, 0.5f, 0f));
                Transform hand = NewBone("hand_L", lower, new Vector3(1f, -0.5f, 0f));
                Transform target = NewBone("LeftHandTarget", root.transform, new Vector3(1.5f, 0.5f, 0f));
                target.rotation = Quaternion.Euler(10f, 20f, 30f);

                var ik = root.AddComponent<FPLeftHandIK>();
                SetField(ik, "leftHandTarget", target);
                SetField(ik, "upperArm", upper);
                SetField(ik, "lowerArm", lower);
                SetField(ik, "hand", hand);
                SetField(ik, "positionWeight", 1f);
                SetField(ik, "rotationWeight", 1f);
                SetField(ik, "blendSeconds", 0f);

                root.SetActive(true);
                Invoke(ik, "LateUpdate");

                Assert.That(Vector3.Distance(hand.position, target.position), Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(hand.rotation, target.rotation), Is.LessThan(0.1f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DetachableMagazine_DisableAlwaysRestoresInstalledPose()
        {
            var root = new GameObject("MagazineView_TestRoot");
            root.SetActive(false);

            try
            {
                Transform gun = NewBone("Gun", root.transform, Vector3.zero);
                Transform magazine = NewBone("Magazine", gun, new Vector3(0.2f, -0.1f, 0.03f));
                magazine.localRotation = Quaternion.Euler(5f, 10f, 15f);
                Transform hand = NewBone("hand_L", root.transform, new Vector3(-1f, 1f, 0f));
                Vector3 installedPosition = magazine.localPosition;
                Quaternion installedRotation = magazine.localRotation;

                var view = root.AddComponent<DetachableMagazineView>();
                SetField(view, "magazinePart", magazine);
                SetField(view, "installedParent", gun);
                SetField(view, "leftHand", hand);
                SetField(view, "heldLocalPosition", new Vector3(0.04f, 0.02f, 0.01f));
                Invoke(view, "Awake");

                root.SetActive(true);
                Invoke(view, "AttachToHand");
                Assert.That(object.ReferenceEquals(magazine.parent, hand), Is.True,
                    "The extracted magazine must follow the animated left hand.");

                Invoke(view, "OnDisable");

                Assert.That(object.ReferenceEquals(magazine.parent, gun), Is.True,
                    "Disabling the view must put the magazine back under the gun.");
                Assert.That(Vector3.Distance(magazine.localPosition, installedPosition), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(magazine.localRotation, installedRotation), Is.LessThan(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WeaponPoseProfile_UsesWeaponSpecificInterfacesAndReloadPhases()
        {
            var root = new GameObject("PoseProfile_TestRoot");
            root.SetActive(false);

            try
            {
                Transform weaponRoot = NewBone("LPW_Gun", root.transform, Vector3.zero);
                Transform rightHand = NewBone("hand_R", root.transform, new Vector3(0.25f, 0f, 0f));
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot, new Vector3(0.25f, 0f, 0f));
                Transform support = NewBone("LeftSupportGrip", weaponRoot, new Vector3(-0.2f, 0f, 0f));
                Transform trigger = NewBone("Trigger", weaponRoot, new Vector3(0.2f, 0f, 0f));
                Transform well = NewBone("MagazineWell", weaponRoot, new Vector3(0.1f, -0.1f, 0f));
                Transform magazineGrip = NewBone("MagazineGrip", weaponRoot, new Vector3(0.1f, -0.2f, 0f));

                var profile = root.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "leftSupportGrip", support);
                SetField(profile, "trigger", trigger);
                SetField(profile, "magazineWell", well);
                SetField(profile, "magazineGrip", magazineGrip);
                SetField(profile, "magazineOutNormalized", 0.2f);
                SetField(profile, "magazineInNormalized", 0.7f);
                Invoke(profile, "Awake");

                Assert.That(profile.RightHandGrip, Is.SameAs(rightGrip));
                Assert.That(profile.Trigger, Is.SameAs(trigger));
                Assert.That(profile.HasCompleteInterfaceLayout, Is.True);
                Assert.That(profile.ValidateInterfaceLayout(), Is.True);

                profile.BeginReload(false);
                Assert.That(profile.GetReloadHandPhase(0.1f), Is.EqualTo(FPWeaponPoseProfile.ReloadHandPhase.Support));
                Assert.That(profile.GetLeftHandTarget(true, 0.4f), Is.SameAs(magazineGrip));
                Assert.That(profile.GetReloadHandPhase(0.9f), Is.EqualTo(FPWeaponPoseProfile.ReloadHandPhase.MagazineInsert));
                Assert.That(profile.GetLeftHandTarget(true, 0.9f), Is.SameAs(well));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WeaponPoseProfile_RightHandGripConsumesRootCalibration()
        {
            var root = new GameObject("PoseProfile_RootCalibrationTest");
            root.SetActive(false);

            try
            {
                Transform weaponRoot = NewBone("LPW_Gun", root.transform, Vector3.zero);
                Transform rightHand = NewBone("hand_R", root.transform, new Vector3(0.4f, 0.2f, 0f));
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot, Vector3.zero);
                var profile = root.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "hasRootCalibration", true);
                Invoke(profile, "Awake");

                Assert.That(profile.RightHandGripError, Is.LessThan(0.0001f));
                Assert.That(profile.RightHandGripRotationError, Is.LessThan(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PoseMath_ComposesContactToWristOffsetInContactSpace()
        {
            Quaternion contactRotation = Quaternion.Euler(0f, 90f, 0f);
            FPContactPose result = FPWeaponPoseMath.ComposeContactToWrist(
                new Vector3(1f, 2f, 3f), contactRotation,
                new Vector3(0f, 0f, .2f), Quaternion.Euler(0f, 15f, 0f));

            Assert.That(result.Position.x, Is.EqualTo(1.2f).Within(.0001f));
            Assert.That(result.Position.y, Is.EqualTo(2f).Within(.0001f));
            Assert.That(result.Position.z, Is.EqualTo(3f).Within(.0001f));
            Assert.That(Quaternion.Angle(result.Rotation,
                contactRotation * Quaternion.Euler(0f, 15f, 0f)), Is.LessThan(.01f));
        }

        [Test]
        public void AugRearMagazine_LocksRotationAndKeepsInsertionOnOneAxis()
        {
            var root = new GameObject("AUG_RearMagazinePoseTest");
            root.SetActive(false);

            try
            {
                Transform weaponRoot = NewBone("LPW_Gun", root.transform, Vector3.zero);
                Transform rightHand = NewBone("hand_R", root.transform, new Vector3(.25f, 0f, 0f));
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot, new Vector3(.25f, 0f, 0f));
                Transform support = NewBone("LeftSupportGrip", weaponRoot, new Vector3(.2f, .1f, 0f));
                Transform trigger = NewBone("Trigger", weaponRoot, new Vector3(.2f, 0f, 0f));
                Transform well = NewBone("MagazineWell", weaponRoot, new Vector3(1.2f, .1f, 0f));
                Transform grip = NewBone("MagazineGrip", weaponRoot, new Vector3(1.2f, -.1f, 0f));
                Transform insertGuide = NewBone("MagazineInsertGuide", weaponRoot, new Vector3(1.2f, 0f, 0f));
                Transform extracted = NewBone("MagazineExtracted", weaponRoot, new Vector3(1.2f, -.2f, 0f));

                var profile = root.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "leftSupportGrip", support);
                SetField(profile, "trigger", trigger);
                SetField(profile, "magazineWell", well);
                SetField(profile, "magazineGrip", grip);
                SetField(profile, "magazineInsertGuide", insertGuide);
                SetField(profile, "magazineExtracted", extracted);
                SetField(profile, "hasMagazineCalibration", true);
                SetField(profile, "lockMagazineToWellAxis", true);
                SetField(profile, "magazineInsertionAxisLocal", Vector3.up);
                SetField(profile, "magazineOutNormalized", .2f);
                SetField(profile, "magazineInNormalized", .7f);
                SetField(profile, "magazineAlignWindow", .1f);
                SetField(profile, "magazineInsertWindow", .1f);
                SetField(profile, "magazineHeldLocalPosition", new Vector3(.1f, 0f, 0f));
                SetField(profile, "magazineHeldLocalEulerAngles", Vector3.zero);
                Invoke(profile, "Awake");

                profile.BeginReload(false);
                Assert.That(profile.GetReloadContactPhase(.4f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell));

                Assert.That(profile.TryGetMagazinePose(.2f, out FPContactPose tacticalOut), Is.True);
                Assert.That(Vector3.Distance(tacticalOut.Position, well.position), Is.LessThan(.0001f),
                    "Tactical MagOut must begin at the installed well pose without a position jump.");
                Assert.That(profile.TryGetMagazinePose(.3f, out FPContactPose tacticalExtracted), Is.True);
                Assert.That(Vector3.Distance(tacticalExtracted.Position, extracted.position), Is.LessThan(.0001f),
                    "Tactical extractEnd must reach the axis-aligned extracted marker.");

                foreach (float normalized in new[] { .2f, .25f, .3f, .4f, .53f, .59f, .6f, .65f, .7f })
                {
                    Assert.That(profile.TryGetMagazinePose(normalized, out FPContactPose magazine), Is.True,
                        "AUG stable magazine pose missing at " + normalized);
                    Assert.That(Quaternion.Angle(magazine.Rotation, well.rotation), Is.LessThan(.01f),
                        "AUG magazine must never rotate during detached carry at " + normalized);
                    Assert.That(magazine.Position.x, Is.EqualTo(well.position.x).Within(.0001f));
                    Assert.That(magazine.Position.z, Is.EqualTo(well.position.z).Within(.0001f));
                    Assert.That(profile.TryGetReloadHandTarget(normalized, out FPContactPose hand), Is.True);
                    FPContactPose recomposed = FPWeaponPoseMath.ComposeHeldMagazine(
                        hand.Position, hand.Rotation,
                        new Vector3(.1f, 0f, 0f), Quaternion.identity);
                    Assert.That(Vector3.Distance(recomposed.Position, magazine.Position), Is.LessThan(.0001f));
                    Assert.That(Quaternion.Angle(recomposed.Rotation, magazine.Rotation), Is.LessThan(.01f));
                }

                Assert.That(profile.GetReloadContactPhase(.2001f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell));
                Assert.That(profile.GetReloadContactPhase(.55f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell));

                Assert.That(profile.GetReloadContactPhase(.2f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell));

                Assert.That(profile.GetReloadContactPhase(.7f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell));
                Assert.That(profile.TryGetReloadHandTarget(.7f, out FPContactPose insertion), Is.True);
                FPContactPose expected = FPWeaponPoseMath.ResolveHandFromHeldMagazine(
                    well, new Vector3(.1f, 0f, 0f), Quaternion.identity);
                Assert.That(Vector3.Distance(insertion.Position, expected.Position), Is.LessThan(.0001f));
                Assert.That(profile.GetReloadContactPhase(.7001f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.None),
                    "The fixed well marker must release the hand immediately after MagIn.");

                profile.BeginReload(true);
                Assert.That(profile.TryGetMagazinePose(.12f, out FPContactPose emptyOut), Is.True);
                Assert.That(Vector3.Distance(emptyOut.Position, well.position), Is.LessThan(.0001f),
                    "Empty MagOut must begin at the installed well pose without a position jump.");
                Assert.That(profile.TryGetMagazinePose(.22f, out FPContactPose emptyExtracted), Is.True);
                Assert.That(Vector3.Distance(emptyExtracted.Position, extracted.position), Is.LessThan(.0001f),
                    "Empty extractEnd must reach the axis-aligned extracted marker.");

                foreach (float normalized in new[] { .12f, .17f, .22f, .28f, .35f, .4f, .45f })
                {
                    Assert.That(profile.TryGetMagazinePose(normalized, out FPContactPose emptyPose), Is.True,
                        "AUG stable magazine pose missing during empty reload at " + normalized);
                    Assert.That(Quaternion.Angle(emptyPose.Rotation, well.rotation), Is.LessThan(.01f));
                    Assert.That(emptyPose.Position.x, Is.EqualTo(well.position.x).Within(.0001f));
                    Assert.That(emptyPose.Position.z, Is.EqualTo(well.position.z).Within(.0001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LegacyMagazineProfile_LeavesCarryUnconstrained()
        {
            var root = new GameObject("LegacyMagazineCarryTest");
            root.SetActive(false);

            try
            {
                Transform weaponRoot = NewBone("LPW_Gun", root.transform, Vector3.zero);
                Transform rightHand = NewBone("hand_R", root.transform, new Vector3(.25f, 0f, 0f));
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot, new Vector3(.25f, 0f, 0f));
                Transform support = NewBone("LeftSupportGrip", weaponRoot, new Vector3(.2f, .1f, 0f));
                Transform trigger = NewBone("Trigger", weaponRoot, new Vector3(.2f, 0f, 0f));
                Transform well = NewBone("MagazineWell", weaponRoot, new Vector3(.1f, .1f, 0f));
                Transform grip = NewBone("MagazineGrip", weaponRoot, new Vector3(.1f, -.1f, 0f));

                var profile = root.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "leftSupportGrip", support);
                SetField(profile, "trigger", trigger);
                SetField(profile, "magazineWell", well);
                SetField(profile, "magazineGrip", grip);
                SetField(profile, "hasMagazineCalibration", true);
                SetField(profile, "magazineOutNormalized", .2f);
                SetField(profile, "magazineInNormalized", .7f);
                Invoke(profile, "Awake");

                profile.BeginReload(false);
                Assert.That(profile.UsesStableMagazineCarry, Is.False);
                Assert.That(profile.GetReloadContactPhase(.4f),
                    Is.EqualTo(FPWeaponPoseProfile.ReloadContactPhase.CarryMagazine));
                Assert.That(profile.TryGetReloadHandTarget(.4f, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SupportReferenceOffset_IsAppliedToContactInsteadOfTreatingMarkerAsWrist()
        {
            var root = new GameObject("SupportReferenceOffsetTest");
            root.SetActive(false);

            try
            {
                Transform weaponRoot = NewBone("LPW_Gun", root.transform, Vector3.zero);
                Transform rightHand = NewBone("hand_R", root.transform, new Vector3(.25f, 0f, 0f));
                Transform rightGrip = NewBone("RightHandGrip", weaponRoot, new Vector3(.25f, 0f, 0f));
                Transform support = NewBone("LeftSupportGrip", weaponRoot, new Vector3(1f, 0f, 0f));
                Transform trigger = NewBone("Trigger", weaponRoot, new Vector3(.2f, 0f, 0f));
                Transform well = NewBone("MagazineWell", weaponRoot, new Vector3(.1f, -.1f, 0f));
                Transform grip = NewBone("MagazineGrip", weaponRoot, new Vector3(.1f, -.2f, 0f));

                var profile = root.AddComponent<FPWeaponPoseProfile>();
                SetField(profile, "weaponRoot", weaponRoot);
                SetField(profile, "rightHand", rightHand);
                SetField(profile, "rightHandGrip", rightGrip);
                SetField(profile, "leftSupportGrip", support);
                SetField(profile, "trigger", trigger);
                SetField(profile, "magazineWell", well);
                SetField(profile, "magazineGrip", grip);
                var offsets = new System.Collections.Generic.List<FPWeaponPoseProfile.AnimationFamilyContactOffset>
                {
                    new FPWeaponPoseProfile.AnimationFamilyContactOffset
                    {
                        family = FPAnimationReferenceFamily.Rifle03,
                        configured = true,
                        supportContactToWristLocalPosition = new Vector3(0f, .05f, 0f),
                        supportContactToWristLocalEulerAngles = new Vector3(0f, 10f, 0f)
                    }
                };
                SetField(profile, "referenceFamily", FPAnimationReferenceFamily.Rifle03);
                SetField(profile, "animationFamilyContactOffsets", offsets);
                Invoke(profile, "Awake");

                Assert.That(profile.TryGetSupportHandTarget(out FPContactPose target), Is.True);
                Assert.That(Vector3.Distance(target.Position,
                    support.position + support.rotation * new Vector3(0f, .05f, 0f)), Is.LessThan(.0001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WeaponDefinition_RifleFamilyResolverSelectsVerticalGripOnly()
        {
            var rifle = ScriptableObject.CreateInstance<WeaponDefinition>();
            var smg = ScriptableObject.CreateInstance<WeaponDefinition>();
            var rifle01 = new AnimationClip { name = "rifle01_idle" };
            var rifle02 = new AnimationClip { name = "rifle02_idle" };

            try
            {
                var fallback = new WeaponAnimationSet { Idle = rifle01 };
                var m4 = new WeaponAnimationSet { Idle = rifle02 };

                SetField(rifle, "weaponId", "lpw.rifle.aug");
                SetField(rifle, "firstPersonAnimations", fallback);
                SetField(rifle, "rifleHasVerticalGrip", true);
                SetField(rifle, "rifleAnimationFamily", FirstPersonAnimationFamily.Rifle02);
                SetField(rifle, "rifle02Animations", m4);

                Assert.That(rifle.FirstPersonAnimationFamily, Is.EqualTo(FirstPersonAnimationFamily.Rifle02));
                Assert.That(rifle.FirstPersonAnimations.Idle, Is.SameAs(rifle02));

                SetField(rifle, "rifleHasVerticalGrip", false);
                Assert.That(rifle.FirstPersonAnimationFamily, Is.EqualTo(FirstPersonAnimationFamily.Rifle01));
                Assert.That(rifle.FirstPersonAnimations.Idle, Is.SameAs(rifle01));

                SetField(smg, "weaponId", "lpw.smg.mac10");
                SetField(smg, "firstPersonAnimations", fallback);
                SetField(smg, "rifleHasVerticalGrip", true);
                SetField(smg, "rifleAnimationFamily", FirstPersonAnimationFamily.Rifle02);
                SetField(smg, "rifle02Animations", m4);

                Assert.That(smg.FirstPersonAnimationFamily, Is.EqualTo(FirstPersonAnimationFamily.Native));
                Assert.That(smg.FirstPersonAnimations.Idle, Is.SameAs(rifle01));
            }
            finally
            {
                Object.DestroyImmediate(rifle);
                Object.DestroyImmediate(smg);
                Object.DestroyImmediate(rifle01);
                Object.DestroyImmediate(rifle02);
            }
        }

        private static Transform NewBone(string name, Transform parent, Vector3 localPosition)
        {
            var transform = new GameObject(name).transform;
            transform.SetParent(parent, false);
            transform.localPosition = localPosition;
            return transform;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, target.GetType().Name + "." + name);
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, target.GetType().Name + "." + name);
            method.Invoke(target, null);
        }
    }
}
