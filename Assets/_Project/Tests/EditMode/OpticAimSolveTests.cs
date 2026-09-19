using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// ADS 审计 A1 修复的数据锁（2026-09-19）：光轴完整姿态解的数学性质。
    /// 旧实现只对 x/y、z 恒 0、旋转不动 → 眼点停在腰射深度，镜窗又远又小（参考图一）。
    /// 本文件锁死：(1) 校准行完整读写/解析/版本失效链；(2) 姿态解的固定点性质——
    /// 应用解后眼点必须精确落在 FP 相机位、光轴必须对齐相机前向，且该性质
    /// 与相机（父链）旋转无关（真实 Transform 链上验证，非仅字段赋值断言）。
    /// </summary>
    public sealed class OpticAimSolveTests
    {
        private AttachmentCalibration _calibration;

        [SetUp]
        public void SetUp() => _calibration = ScriptableObject.CreateInstance<AttachmentCalibration>();

        [TearDown]
        public void TearDown()
        {
            if (_calibration != null) Object.DestroyImmediate(_calibration);
        }

        // ---------------- 校准数据：完整行读写 / 解析 / 版本 ----------------

        [Test]
        public void SetOpticAim_Roundtrip_KeepsAllFields()
        {
            var data = new OpticAimData
            {
                EyePointLocal = new Vector3(0.12f, 0.045f, 0f),
                AxisFrontPointLocal = new Vector3(-0.05f, 0.046f, 0.001f),
                WindowCenterLocal = new Vector3(-0.02f, 0.05f, 0f),
                WindowHalfWidthMeters = 0.021f,
                WindowHalfHeightMeters = 0.016f,
                HasAxisFront = true,
                HasWindow = true
            };
            _calibration.SetOpticAim("weapon.ak", "attach.lpfp.optic.02", data);

            Assert.That(_calibration.TryGetOpticAim("weapon.ak", "attach.lpfp.optic.02", out var read), Is.True);
            Assert.That(read.EyePointLocal, Is.EqualTo(data.EyePointLocal).Within(1e-5f));
            Assert.That(read.AxisFrontPointLocal, Is.EqualTo(data.AxisFrontPointLocal).Within(1e-5f));
            Assert.That(read.WindowCenterLocal, Is.EqualTo(data.WindowCenterLocal).Within(1e-5f));
            Assert.That(read.WindowHalfWidthMeters, Is.EqualTo(0.021f).Within(1e-6f));
            Assert.That(read.WindowHalfHeightMeters, Is.EqualTo(0.016f).Within(1e-6f));
            Assert.That(read.HasAxisFront, Is.True);
            Assert.That(read.HasWindow, Is.True);
            // 回归：旧 API 眼点读取不受扩展影响
            Assert.That(_calibration.TryGetOpticEyePoint("weapon.ak", "attach.lpfp.optic.02", out var eye), Is.True);
            Assert.That(eye, Is.EqualTo(data.EyePointLocal).Within(1e-5f));
            // 默认轴方向（无前点行）：挂点局部 -X
            _calibration.SetOpticEyePoint("weapon.other", "attach.lpfp.optic.02", new Vector3(0.1f, 0.04f, 0f));
            Assert.That(_calibration.TryGetOpticAim("weapon.other", "attach.lpfp.optic.02", out var noAxis), Is.True);
            Assert.That(noAxis.HasAxisFront, Is.False);
            Assert.That(noAxis.AxisDirectionLocal, Is.EqualTo(new Vector3(-1f, 0f, 0f)).Within(1e-5f));
        }

        [Test]
        public void OpticAim_ComboOverridesDefault_FallbackForOtherWeapons()
        {
            _calibration.SetOpticEyePoint(string.Empty, "attach.lpfp.optic.02", new Vector3(0.12f, 0.045f, 0f));
            _calibration.SetOpticEyePoint("weapon.ak", "attach.lpfp.optic.02", new Vector3(0.14f, 0.05f, 0f));

            Assert.That(_calibration.TryGetOpticAim("weapon.ak", "attach.lpfp.optic.02", out var combo), Is.True);
            Assert.That(combo.EyePointLocal.x, Is.EqualTo(0.14f).Within(1e-6f), "组合覆盖必须优先于默认行");
            Assert.That(_calibration.TryGetOpticAim("weapon.m4", "attach.lpfp.optic.02", out var fallback), Is.True);
            Assert.That(fallback.EyePointLocal.x, Is.EqualTo(0.12f).Within(1e-6f), "无组合行时回退瞄具默认");
        }

        [Test]
        public void EyeOnlyWrite_PreservesAxisAndWindowFields()
        {
            _calibration.SetOpticAim(string.Empty, "attach.lpfp.optic.02", new OpticAimData
            {
                EyePointLocal = new Vector3(0.12f, 0.045f, 0f),
                AxisFrontPointLocal = new Vector3(-0.05f, 0.046f, 0f),
                WindowCenterLocal = new Vector3(-0.02f, 0.05f, 0f),
                WindowHalfWidthMeters = 0.02f,
                WindowHalfHeightMeters = 0.015f,
                HasAxisFront = true,
                HasWindow = true
            });
            // RebuildOpticAimRowsFromCatalog 式的"只写眼点"维护路径不得清掉同行轴向/镜窗数据
            _calibration.SetOpticEyePoint(string.Empty, "attach.lpfp.optic.02", new Vector3(0.13f, 0.046f, 0f));
            Assert.That(_calibration.TryGetOpticAim(string.Empty, "attach.lpfp.optic.02", out var data), Is.True);
            Assert.That(data.HasAxisFront, Is.True, "眼点写入不得丢失光轴前点");
            Assert.That(data.HasWindow, Is.True, "眼点写入不得丢失镜窗数据");
            Assert.That(data.EyePointLocal.x, Is.EqualTo(0.13f).Within(1e-6f));
        }

        [Test]
        public void DataVersion_BumpsOnEveryWriteAndRemove()
        {
            int v0 = _calibration.DataVersion;
            _calibration.SetOpticEyePoint(string.Empty, "a.b", Vector3.right);
            int v1 = _calibration.DataVersion;
            Assert.That(v1, Is.GreaterThan(v0), "写入眼点必须递增版本（FPWeaponMotion 失效链依赖）");
            _calibration.SetOpticAim(string.Empty, "a.b", new OpticAimData { EyePointLocal = Vector3.right, HasWindow = false });
            int v2 = _calibration.DataVersion;
            Assert.That(v2, Is.GreaterThan(v1), "完整行写入必须递增版本");
            Assert.That(_calibration.RemoveOpticEyePoint(string.Empty, "a.b"), Is.True);
            Assert.That(_calibration.DataVersion, Is.GreaterThan(v2), "删除必须递增版本");
            Assert.That(_calibration.TryGetOpticAim(string.Empty, "a.b", out _), Is.False);
        }

        // ---------------- 姿态解固定点（真实 Transform 链） ----------------

        [Test]
        public void SolveAimPose_FixedPoint_EyeOnCamera_AxisOnForward_RegardlessOfCameraRotation()
        {
            // 场景：父链（带任意旋转）→ 相机（overlay 相机偏移）+ 枪根 → 挂点（含 mountEuler 式旋转）
            var parent = NewObject("Parent");
            parent.transform.position = new Vector3(10f, 2f, -3f);
            parent.transform.rotation = Quaternion.Euler(5f, 33f, 0f);

            var camera = NewObject("FPCamera", parent.transform);
            camera.transform.localPosition = new Vector3(-0.047f, 0.082f, -0.299f);
            camera.transform.localRotation = Quaternion.identity;

            var root = NewObject("FPWeaponRoot", parent.transform);
            root.transform.localPosition = new Vector3(0.05f, -0.12f, 0.31f);
            root.transform.localRotation = Quaternion.Euler(-8f, 14f, 3f);

            var socket = NewObject("Attach_Optic", root.transform);
            socket.transform.localPosition = new Vector3(0.02f, 0.1f, 0.05f);
            socket.transform.localRotation = Quaternion.Euler(10f, 20f, 30f) * Quaternion.AngleAxis(-90f, Vector3.up);

            Vector3 eyeLocalSocket = new Vector3(0.12f, 0.045f, 0f);
            Vector3 axisLocalSocket = new Vector3(-1f, 0f, 0f);

            // 测量（root 局部系，位姿不变量）——与 FPWeaponMotion.TryResolveOpticAimRoot 同一换算
            Vector3 eyeWorld = socket.transform.TransformPoint(eyeLocalSocket);
            Vector3 axisWorld = socket.transform.TransformDirection(axisLocalSocket).normalized;
            Vector3 rawUpWorld = socket.transform.TransformDirection(Vector3.up);
            Vector3 upOrtho = Vector3.ProjectOnPlane(rawUpWorld, axisWorld);
            Vector3 upWorld = upOrtho.sqrMagnitude > 1e-8f ? upOrtho.normalized : Vector3.up;
            Vector3 eyeRoot = root.transform.InverseTransformPoint(eyeWorld);
            Vector3 axisRoot = root.transform.InverseTransformDirection(axisWorld).normalized;
            Vector3 upRoot = root.transform.InverseTransformDirection(upWorld).normalized;
            Vector3 camParent = root.transform.parent.InverseTransformPoint(camera.transform.position);

            // 解与应用
            Quaternion solvedRotation = OpticAimGeometry.SolveAimLocalRotation(axisRoot, upRoot);
            Vector3 solvedPosition = OpticAimGeometry.SolveAimLocalPosition(solvedRotation, eyeRoot, camParent);
            root.transform.localPosition = solvedPosition;
            root.transform.localRotation = solvedRotation;

            // 固定点断言：眼点=相机位、光轴=相机前向、滚转=相机 up（与父链旋转无关）
            Vector3 eyeAfter = socket.transform.TransformPoint(eyeLocalSocket);
            Assert.That(Vector3.Distance(eyeAfter, camera.transform.position), Is.LessThan(1e-3f),
                "眼点必须精确落在 FP 相机位（z 对位修复的核心断言）");
            Vector3 axisAfter = socket.transform.TransformDirection(axisLocalSocket).normalized;
            Assert.That(Vector3.Angle(axisAfter, camera.transform.forward), Is.LessThan(0.05f),
                "光轴必须对齐相机前向（旋转解核心断言）");
            Vector3 upAfter = socket.transform.TransformDirection(Vector3.up);
            Vector3 upAfterOrtho = Vector3.ProjectOnPlane(upAfter, axisAfter).normalized;
            Assert.That(Vector3.Angle(upAfterOrtho, camera.transform.up), Is.LessThan(0.05f),
                "滚转必须对齐相机 up（分划直立）");
        }

        [Test]
        public void SolveAimPose_AlreadyAlignedAxis_YieldsIdentityRotation()
        {
            Vector3 axisRoot = Vector3.forward;
            Vector3 upRoot = Vector3.up;
            Quaternion rotation = OpticAimGeometry.SolveAimLocalRotation(axisRoot, upRoot);
            Assert.That(Quaternion.Angle(rotation, Quaternion.identity), Is.LessThan(0.01f),
                "光轴已对齐前向时旋转解应为 identity（不引入多余滚转）");
            Vector3 position = OpticAimGeometry.SolveAimLocalPosition(rotation, new Vector3(0.1f, 0.05f, 0.2f),
                new Vector3(-0.047f, 0.082f, -0.299f));
            Assert.That(position, Is.EqualTo(new Vector3(-0.147f, 0.032f, -0.499f)).Within(1e-5f));
        }

        private static GameObject NewObject(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }
    }
}
