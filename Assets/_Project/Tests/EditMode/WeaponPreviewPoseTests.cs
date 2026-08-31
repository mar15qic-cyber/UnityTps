using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Locks the Docs/20 weapon-preview pose contract: model initializes to a level side-on view
    /// (barrel horizontal, no inherited TP prefab root tilt), with drag-to-orbit preserved.
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
        public void Initialize_ZeroesPrefabRootTilt()
        {
            // Use any real TP prefab (they carry non-zero root rotations that previously leaked in).
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.transform.eulerAngles.magnitude, Is.GreaterThan(10f),
                "fixture prefab should carry a non-trivial root rotation to prove we zero it");

            var controller = CreateController();
            controller.Initialize(prefab);

            var modelRoot = GetField<GameObject>(controller, "modelRoot");
            Assert.That(modelRoot, Is.Not.Null);
            var instance = modelRoot.transform.GetChild(0);
            Assert.That(instance, Is.Not.Null);
            var euler = instance.localEulerAngles;
            // Every axis should be ~0 after the fix (allow 360 wrap).
            foreach (var angle in new[] { euler.x, euler.y, euler.z })
            {
                var wrapped = angle > 180f ? 360f - angle : angle;
                Assert.That(wrapped, Is.LessThan(0.5f), $"instance local rotation should be identity, got {euler}");
            }
        }

        [Test]
        public void FrameModel_CentersOnBoundsAndSetsDistance()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab");
            Assert.That(prefab, Is.Not.Null);
            var controller = CreateController();
            controller.Initialize(prefab);

            var modelRoot = GetField<GameObject>(controller, "modelRoot");
            var instance = modelRoot.transform.GetChild(0);
            // After framing, the model's world bounds center should sit at the origin (stage space).
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            Assert.That(bounds.center.magnitude, Is.LessThan(0.05f), $"model should be centered at origin, got {bounds.center}");

            var distance = GetField<float>(controller, "distance");
            Assert.That(distance, Is.InRange(0.45f, 8f));
        }

        [Test]
        public void Drag_ChangesYawPitch_ButStaysClamped()
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
                position = new Vector2(100f, 40f)
            };
            controller.OnDrag(dragData);

            var yaw1 = GetField<float>(controller, "yaw");
            var pitch1 = GetField<float>(controller, "pitch");
            Assert.That(yaw1, Is.Not.EqualTo(yaw0), "drag should change yaw");
            Assert.That(pitch1, Is.InRange(-55f, 55f), "pitch stays clamped");
        }
    }
}
