using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Locks the weapon-preview pose contract: the orbit rig owns framing while the instantiated
    /// TP prefab keeps the exact authored hand-mounted pose used by Arena.
    /// </summary>
    public sealed class WeaponPreviewPoseTests
    {
        private readonly System.Collections.Generic.List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        private WeaponPreviewController CreateController()
        {
            var go = new GameObject("Preview", typeof(RectTransform), typeof(RawImage), typeof(WeaponPreviewController));
            created.Add(go);
            // Awake runs on AddComponent; ensure output wired.
            return go.GetComponent<WeaponPreviewController>();
        }

        private static T GetField<T>(object target, string name)
        {
            var field = typeof(WeaponPreviewController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return (T)field.GetValue(target);
        }

        [Test]
        public void InitialPose_IsLevelSideOn()
        {
            var controller = CreateController();
            // yaw/pitch initial constants: 90 (side-on for +Z barrel, camera at -Z), 0 (level).
            Assert.That(GetField<float>(controller, "yaw"), Is.EqualTo(90f), "initial yaw should be side-on");
            Assert.That(GetField<float>(controller, "pitch"), Is.EqualTo(0f), "initial pitch should be level");
        }

        [Test]
        public void Initialize_PreservesAuthoredTpRootPose()
        {
            // Handgun and rifle stances use different right-hand spaces; zeroing this transform
            // made the repository preview disagree with the exact same prefab in Arena.
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab");
            Assert.That(prefab, Is.Not.Null);
            var expectedPosition = prefab.transform.localPosition;
            var expectedRotation = prefab.transform.localRotation;
            var expectedScale = prefab.transform.localScale;

            var controller = CreateController();
            controller.Initialize(prefab);

            var instance = GetField<GameObject>(controller, "modelInstance").transform;
            Assert.That(Vector3.Distance(instance.localPosition, expectedPosition), Is.LessThan(0.00001f));
            Assert.That(Quaternion.Angle(instance.localRotation, expectedRotation), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(instance.localScale, expectedScale), Is.LessThan(0.00001f));
        }

        [Test]
        public void FrameModel_CentersOnBoundsAndSetsDistance()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab");
            Assert.That(prefab, Is.Not.Null);
            var controller = CreateController();
            controller.Initialize(prefab);

            var instance = GetField<GameObject>(controller, "modelInstance").transform;
            // After framing, the model's world bounds center should sit at the origin (stage space).
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            Assert.That(bounds.center.magnitude, Is.LessThan(0.05f), $"model should be centered at origin, got {bounds.center}");

            var distance = GetField<float>(controller, "distance");
            Assert.That(distance, Is.InRange(0.45f, 8f));
        }

        [Test]
        public void Drag_ChangesYawPitch_AndAllowsFullVerticalOrbit()
        {
            var controller = CreateController();
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab");
            controller.Initialize(prefab);

            var yaw0 = GetField<float>(controller, "yaw");
            var pitch0 = GetField<float>(controller, "pitch");

            // Simulate a drag via reflection of OnPointerDown/OnDrag with a fabricated event.
            var eventData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
            controller.OnPointerDown(eventData);

            var dragData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
            {
                position = new Vector2(100f, 1000f)
            };
            controller.OnDrag(dragData);

            var yaw1 = GetField<float>(controller, "yaw");
            var pitch1 = GetField<float>(controller, "pitch");
            Assert.That(yaw1, Is.Not.EqualTo(yaw0), "drag should change yaw");
            Assert.That(pitch1, Is.GreaterThan(55f), "vertical drag must pass the former +55 degree stop");
            Assert.That(pitch1, Is.LessThan(360f), "orbit angles stay normalized");
        }
    }
}
