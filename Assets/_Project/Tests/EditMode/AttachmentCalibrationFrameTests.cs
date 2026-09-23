using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 配件校准跨帧换算（2026-09-05 激光错位修正）：校准 delta 存于作者帧（枪匠预览挂点）
    /// 局部系，FP 挂点局部朝向与预览不同（实测 SMG_01 FP/TP 挂点差一俯仰分量）——
    /// 应用时按"挂点相对视图根"的帧做换算，保证跨视图复现枪匠校准的世界位移/朝向。
    /// 旧行（无作者帧）保持直通兼容。
    /// </summary>
    public sealed class AttachmentCalibrationFrameTests
    {
        private AttachmentCalibration _calibration;
        private AttachmentAssetCatalog _catalog;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _calibration = ScriptableObject.CreateInstance<AttachmentCalibration>();
            // ApplyAttachments 从 catalog.Calibration 读校准；属性只读，测试经反射注入
            _catalog = ScriptableObject.CreateInstance<AttachmentAssetCatalog>();
            var calField = typeof(AttachmentAssetCatalog).GetField("calibration",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            calField.SetValue(_catalog, _calibration);
            _prefab = new GameObject("Laser_01");
        }

        [TearDown]
        public void TearDown()
        {
            if (_calibration != null) Object.DestroyImmediate(_calibration);
            if (_catalog != null) Object.DestroyImmediate(_catalog);
            if (_prefab != null) Object.DestroyImmediate(_prefab);
        }

        private (WeaponAttachmentView view, Transform socket, GameObject clone) BuildWeapon(
            string name, Quaternion socketLocalRotation)
        {
            var root = new GameObject(name);
            var view = root.AddComponent<WeaponAttachmentView>();
            var socketGo = new GameObject("Attach_Tactical");
            socketGo.transform.SetParent(root.transform, false);
            socketGo.transform.localRotation = socketLocalRotation;
            var socket = socketGo.AddComponent<AttachmentSocket>();
            var slotField = typeof(AttachmentSocket).GetField("slot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            slotField.SetValue(socket, AttachmentSlotType.Tactical);
            return (view, socketGo.transform, null);
        }

        private static AttachmentAssetEntry LaserEntry()
            => new AttachmentAssetEntry { itemId = WeaponAttachmentView.LaserItemId, slot = AttachmentSlotType.Tactical, prefab = null };

        [Test]
        public void VerifiedGunLocalSockets_DoNotConjugateThroughUnrelatedViewRoots()
        {
            var delta = new Vector3(.01f, .02f, .03f);
            _calibration.Set("weapon.test", "attach.test", delta, new Vector3(4, 5, 6), Quaternion.Euler(0, 90, 0));
            var a = BuildWeapon("TP", Quaternion.Euler(0, 90, 0));
            var b = BuildWeapon("FP", Quaternion.Euler(-33, 90, 0));
            try
            {
                foreach (var view in new[] { a.view, b.view })
                {
                    var socket = view.GetComponentInChildren<AttachmentSocket>();
                    var so = new UnityEditor.SerializedObject(socket);
                    so.FindProperty("geometryVerified").boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    var entry = new AttachmentAssetEntry { itemId = "attach.test", slot = AttachmentSlotType.Tactical,
                        prefab = _prefab, mountOffset = new Vector3(.05f, 0, 0) };
                    view.ApplyAttachments(_catalog, "weapon.test", new[] { entry }, false);
                    var mounted = view.FindSpawned(entry.itemId);
                    Assert.That(Vector3.Distance(mounted.localPosition, entry.mountOffset + delta), Is.LessThan(.00001f));
                    Assert.That(Quaternion.Angle(mounted.localRotation, Quaternion.Euler(4, 5, 6)), Is.LessThan(.001f));
                }
            }
            finally { Object.DestroyImmediate(a.view.gameObject); Object.DestroyImmediate(b.view.gameObject); }
        }

        [Test]
        public void Calibration_FromAuthorFrame_LandsAtSameViewSpaceOffset_OnDifferentSocketFrame()
        {
            var rotA = Quaternion.Euler(0f, 90f, 0f);            // 预览/TP 型挂点（实测 Y90）
            var rotB = Quaternion.Euler(30f, 90f, 0f);           // FP 型挂点（实测多一俯仰分量）
            var dragDelta = new Vector3(0.02f, -0.03f, 0.01f);   // 用户在枪匠里拖出的贴合微调

            // —— 枪匠（视图 A）：自动挂载 P + 拖拽 D，保存 delta 与作者帧 ——
            var (viewA, socketA, cloneA) = BuildWeapon("Preview", rotA);
            var autoOffset = new Vector3(0.05f, 0f, 0f);
            viewA.ApplyAttachments(null, "weapon.x", new[] { LaserEntry() }, laserBeamEnabled: false);
            // 模拟自动挂载位姿：把克隆手动放到 autoOffset + 拖拽（等价枪匠预览里的所见）
            var aClone = new GameObject("Att_laser");
            aClone.transform.SetParent(socketA, false);
            aClone.transform.localPosition = autoOffset + dragDelta;
            var authorFrame = Quaternion.Inverse(viewA.transform.rotation) * socketA.rotation;
            _calibration.Set("weapon.x", WeaponAttachmentView.LaserItemId, dragDelta, Vector3.zero, authorFrame);
            Object.DestroyImmediate(aClone);

            // —— FP（视图 B）：不同挂点帧，应用同一行校准 ——
            var (viewB, socketB, _) = BuildWeapon("FirstPerson", rotB);
            // 手动放置“自动挂载位姿”（ApplyAttachments 无模型配件不实例化，这里直接验证换算公式作用于校准行）
            var probe = new GameObject("Probe");
            probe.transform.SetParent(socketB, false);
            probe.transform.localPosition = autoOffset;

            viewB.ApplyAttachments(_catalog, "weapon.x", new[] { LaserEntry() }, laserBeamEnabled: false);

            // 无模型配件不生成克隆——换算行为经由带模型路径验证：改用带模型行重放
            Object.DestroyImmediate(probe);
            var entry = LaserEntry();
            entry.prefab = _prefab;
            entry.mountOffset = autoOffset; // 自动挂载偏移（与预览侧一致）
            viewB.ApplyAttachments(_catalog, "weapon.x", new[] { entry }, laserBeamEnabled: false);

            var expectedLocal = autoOffset + Quaternion.Inverse(rotB) * rotA * dragDelta;
            var spawnedB = viewB.Spawned;
            Assert.That(spawnedB.Count, Is.EqualTo(1));
            var actualLocal = spawnedB[0].transform.localPosition;
            Assert.That((actualLocal - expectedLocal).magnitude, Is.LessThan(0.005f),
                $"跨帧换算精度：actual={actualLocal:F6} expected={expectedLocal:F6} diff={(actualLocal - expectedLocal).magnitude:F6}");
        }

        [Test]
        public void Calibration_SameFrame_AppliesAsIs()
        {
            var rot = Quaternion.Euler(0f, 90f, 0f);
            var (viewA, socketA, _) = BuildWeapon("Preview", rot);
            var authorFrame = Quaternion.Inverse(viewA.transform.rotation) * socketA.rotation;
            var dragDelta = new Vector3(0.02f, -0.03f, 0.01f);
            _calibration.Set("weapon.x", WeaponAttachmentView.LaserItemId, dragDelta, Vector3.zero, authorFrame);

            var (viewB, socketB, _) = BuildWeapon("SameFrameView", rot);
            var entry = LaserEntry();
            entry.prefab = _prefab;
            viewB.ApplyAttachments(_catalog, "weapon.x", new[] { entry }, laserBeamEnabled: false);

            Assert.That(viewB.Spawned[0].transform.localPosition, Is.EqualTo(dragDelta).Within(0.0001f),
                "同帧（挂点朝向一致）时 delta 直通，不做多余换算");
        }

        [Test]
        public void Calibration_LegacyRowWithoutAuthorFrame_PassesThrough()
        {
            var dragDelta = new Vector3(0.02f, 0f, 0f);
            _calibration.Set("weapon.x", WeaponAttachmentView.LaserItemId, dragDelta, Vector3.zero); // 旧行（无作者帧）

            var (viewB, _, _) = BuildWeapon("AnyView", Quaternion.Euler(30f, 90f, 0f));
            var entry = LaserEntry();
            entry.prefab = _prefab;
            viewB.ApplyAttachments(_catalog, "weapon.x", new[] { entry }, laserBeamEnabled: false);

            Assert.That(viewB.Spawned[0].transform.localPosition, Is.EqualTo(dragDelta).Within(0.0001f),
                "旧行（无作者帧）直通，保持既有行为");
        }

        [Test]
        public void Calibration_RotationDelta_ConjugatesAcrossFrames()
        {
            var rotA = Quaternion.Euler(0f, 90f, 0f);
            var rotB = Quaternion.Euler(30f, 90f, 0f);
            var rotDrag = new Vector3(0f, 15f, 0f);
            var (viewA, socketA, _) = BuildWeapon("Preview", rotA);
            var authorFrame = Quaternion.Inverse(viewA.transform.rotation) * socketA.rotation;
            _calibration.Set("weapon.x", WeaponAttachmentView.LaserItemId, Vector3.zero, rotDrag, authorFrame);

            var (viewB, socketB, _) = BuildWeapon("FirstPerson", rotB);
            var entry = LaserEntry();
            entry.prefab = _prefab;
            viewB.ApplyAttachments(_catalog, "weapon.x", new[] { entry }, laserBeamEnabled: false);

            var expectedRot = Quaternion.Inverse(rotB) * rotA * Quaternion.Euler(rotDrag) * Quaternion.Inverse(rotA) * rotB;
            Assert.That(Quaternion.Angle(viewB.Spawned[0].transform.localRotation, expectedRot), Is.LessThan(0.01f),
                "旋转 delta 按共轭换帧后与预期一致");
        }

        [Test]
        public void Calibration_AppliedWhileHolstered_UsesRestFrameCapturedAtCacheTime()
        {
            // 切枪配件错位回归（2026-09-07 用户录屏）：FP 视图实例以收枪冻结姿态被重新激活，
            // ApplyAttachments 又先于 PlayDraw 执行——校准换帧必须基于缓存静止帧（prefab
            // 授权姿态），收枪姿态不得共轭进 delta（否则瞄具/镭射严重偏位，消音器因无校准行幸免）。
            var restRotation = Quaternion.Euler(0f, 90f, 0f);        // 静止挂点帧（= 作者帧）
            var holsterRotation = Quaternion.Euler(70f, 130f, 25f);  // 收枪冻结姿态（动画骨骼旋转）

            // 挂点先于组件创建；EditMode 下 AddComponent 不跑 Awake，用一次空装配触发
            // CacheSockets 在静止姿态完成挂点与静止帧捕获（等价运行时 Awake 时序）。
            var root = new GameObject("CachedView");
            var socketGo = new GameObject("Attach_Tactical");
            socketGo.transform.SetParent(root.transform, false);
            socketGo.transform.localRotation = restRotation;
            var socket = socketGo.AddComponent<AttachmentSocket>();
            var slotField = typeof(AttachmentSocket).GetField("slot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            slotField.SetValue(socket, AttachmentSlotType.Tactical);
            var view = root.AddComponent<WeaponAttachmentView>();
            view.ApplyAttachments(null, "weapon.x", System.Array.Empty<AttachmentAssetEntry>(), laserBeamEnabled: false);

            var authorFrame = Quaternion.Inverse(root.transform.rotation) * socketGo.transform.rotation;
            var dragDelta = new Vector3(0.02f, -0.03f, 0.01f);
            _calibration.Set("weapon.x", WeaponAttachmentView.LaserItemId, dragDelta, Vector3.zero, authorFrame);

            // 模拟切枪回来：视图以收枪姿态被应用装配
            socketGo.transform.localRotation = holsterRotation;
            var entry = LaserEntry();
            entry.prefab = _prefab;
            entry.mountOffset = new Vector3(0.05f, 0f, 0f);
            view.ApplyAttachments(_catalog, "weapon.x", new[] { entry }, laserBeamEnabled: false);

            Assert.That(view.Spawned, Has.Count.EqualTo(1));
            // 静止帧 == 作者帧：delta 直通，与"静止姿态下应用"逐位一致
            var expected = entry.mountOffset + dragDelta;
            var actual = view.Spawned[0].transform.localPosition;
            Assert.That((actual - expected).magnitude, Is.LessThan(0.005f),
                $"收枪姿态下应用必须等价静止应用：actual={actual:F6} expected={expected:F6}");
        }
    }
}
