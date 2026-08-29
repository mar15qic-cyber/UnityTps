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
