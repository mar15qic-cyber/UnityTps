using System.Reflection;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// TP 左手 IK 与手枪持枪位姿回归（2026-09-21 用户实测两问题）：
    /// ①TP 手枪歪斜 = 4 把 TP_Weapon_Handgun 根位姿沿用步枪基准，与 @handgun 动画
    /// 手骨系不匹配（实测悬浮拳头前上方 ~11cm 且带 roll）——本套件锁定按 @handgun idle
    /// 实测重标定的根位姿（握把中心落拳头、枪口水平向前，4 把枪逐把截图目检通过）。
    /// ②左手不随武器变化 = TPLeftHandIK 从未挂上正式玩家 prefab（只存在于已废弃的
    /// Arena_LPWTest 场景）+ 握把配件没有独立持握点——锁定：组件在位、swapper/controller
    /// 引用接线、Mod_04 握把带 LeftHandGrip 标记、目标决策"有握把标记优先握把"。
    /// </summary>
    public sealed class TPGripIkTests
    {
        private static readonly string PlayerPrefabPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";
        private static readonly string GripPrefabPath = "Assets/LowPolyWeapons/Prefabs/Attachments/Mod_04.prefab";

        /// <summary>重标定后的手枪根位姿（@handgun idle 实测，见 TPGripIk 校准记录）。</summary>
        private static readonly (string Path, Vector3 Pos)[] HandgunPoses =
        {
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab", new Vector3(0.0130f, 0.0398f, 0.0455f)),
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_02.prefab", new Vector3(0.0097f, 0.0280f, 0.0366f)),
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_03.prefab", new Vector3(0.0115f, 0.0338f, 0.0422f)),
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_04.prefab", new Vector3(0.0123f, 0.0292f, 0.0576f)),
        };

        /// <summary>重标定后的手枪根旋转（四把共用：手枪 stance 手骨系 × 步枪持枪关系传递）。</summary>
        private static readonly Quaternion HandgunRootRotation = Quaternion.Euler(283.1f, 62.8f, 123.1f);

        /// <summary>rifle_tpc 动画中自然握持时的左腕局部旋转。</summary>
        private static readonly Quaternion RifleNaturalWristRotation =
            new Quaternion(0.07523985f, 0.6240461f, 0.7569626f, -0.1786448f);

        private static readonly (string Path, Vector3 Position, string Meaning)[] RifleGripTargets =
        {
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab",
                new Vector3(0.0002f, -0.0241f, 0.1242f), "护木自然握持"),
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_02.prefab",
                new Vector3(-0.0003f, -0.0612f, 0.1772f), "M4A1 原厂垂直握把"),
            ("Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab",
                new Vector3(0.0002f, -0.0633f, 0.1445f), "SCAR 原厂垂直握把"),
        };

        [Test]
        public void ResolveLeftHandTarget_NoAttachments_ReturnsWeaponBakedTarget()
        {
            var fallback = new GameObject("LeftHandTarget").transform;
            var resolved = TPWeaponMeshSwapper.ResolveLeftHandTarget(null, fallback);
            Assert.That(resolved, Is.EqualTo(fallback));
            Object.DestroyImmediate(fallback.gameObject);
        }

        [Test]
        public void ResolveLeftHandTarget_SpawnedGripWithMarker_PrefersGripMarker()
        {
            var host = new GameObject("weapon");
            var view = host.AddComponent<WeaponAttachmentView>();
            var spawned = new GameObject("Att_attach.lpw.grip.01");
            spawned.transform.SetParent(host.transform);
            var marker = new GameObject("LeftHandGrip");
            marker.transform.SetParent(spawned.transform);
            InjectSpawned(view, spawned);

            var fallback = new GameObject("LeftHandTarget").transform;
            var resolved = TPWeaponMeshSwapper.ResolveLeftHandTarget(view, fallback);
            Assert.That(resolved, Is.EqualTo(marker.transform), "装了带 LeftHandGrip 标记的握把时，左手目标必须切到握把标记");
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(fallback.gameObject);
        }

        [Test]
        public void ResolveLeftHandTarget_SpawnedWithoutMarker_FallsBackToWeaponTarget()
        {
            var host = new GameObject("weapon");
            var view = host.AddComponent<WeaponAttachmentView>();
            var spawned = new GameObject("Att_attach.lpfp.optic.01");
            spawned.transform.SetParent(host.transform);
            InjectSpawned(view, spawned);

            var fallback = new GameObject("LeftHandTarget").transform;
            var resolved = TPWeaponMeshSwapper.ResolveLeftHandTarget(view, fallback);
            Assert.That(resolved, Is.EqualTo(fallback), "非握把配件（无 LeftHandGrip 标记）不得劫持左手目标");
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(fallback.gameObject);
        }

        [Test]
        public void GripAttachmentPrefab_ContainsLeftHandGripMarker()
        {
            var gripPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GripPrefabPath);
            Assert.That(gripPrefab, Is.Not.Null, $"握把配件 prefab 缺失：{GripPrefabPath}");
            var marker = gripPrefab.transform.Find("LeftHandGrip");
            Assert.That(marker, Is.Not.Null,
                "垂直握把 prefab 必须带 LeftHandGrip 标记（TP 左手 IK 握把持握点）");
            Assert.That(Quaternion.Angle(marker.localRotation, RifleNaturalWristRotation), Is.LessThan(0.2f),
                "可装垂直握把的左腕旋转必须与 rifle_tpc 自然握持姿态一致");
        }

        [Test]
        public void PlayerPrefab_MountsTpLeftHandIkOnTheWeaponFrameDriver()
        {
            var playerPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.That(playerPrefab, Is.Not.Null, $"玩家 prefab 缺失：{PlayerPrefabPath}");
            var tpModel = playerPrefab.transform.Find("TP_Model");
            Assert.That(tpModel, Is.Not.Null, "玩家 prefab 缺 TP_Model");
            var ik = tpModel.GetComponent<TPLeftHandIK>();
            Assert.That(ik, Is.Not.Null, "TP_Model 必须挂 TPLeftHandIK（左手随武器/握把贴合）");
            var frameDriver = tpModel.GetComponent<TPWeaponMeshSwapper>();
            Assert.That(frameDriver, Is.Not.Null, "TP_Model 必须由 TPWeaponMeshSwapper 驱动武器与左手 IK");

            var swapper = typeof(TPLeftHandIK)
                .GetField("swapper", BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(ik) as TPWeaponMeshSwapper;
            Assert.That(swapper, Is.Not.Null, "TPLeftHandIK.swapper 未接线");
            var controller = typeof(TPLeftHandIK)
                .GetField("controller", BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(ik);
            Assert.That(controller, Is.Not.Null, "TPLeftHandIK.controller 未接线（换弹闸依赖）");
        }

        [Test]
        public void TpWeaponSwapper_DrivesIkAfterAimInLateUpdate()
        {
            var order = typeof(TPWeaponMeshSwapper)
                .GetCustomAttribute<DefaultExecutionOrder>();
            Assert.That(order, Is.Not.Null);
            Assert.That(order.order, Is.GreaterThan(30),
                "TPWeaponMeshSwapper 必须晚于 TPAimDriver(30) 才能提交最终左手姿态");
            var frameMethod = typeof(TPWeaponMeshSwapper)
                .GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(frameMethod, Is.Not.Null,
                "TP 武器写者必须在同一 LateUpdate 生命周期驱动左手 IK");
        }

        [Test]
        public void TpWeaponSwapper_LateUpdateActuallyMovesWristToCurrentWeaponTarget()
        {
            var host = new GameObject("TP_Model");
            var swapper = host.AddComponent<TPWeaponMeshSwapper>();
            InvokePrivate(swapper, "Awake");
            var ik = host.GetComponent<TPLeftHandIK>();
            Assert.That(ik, Is.Not.Null, "武器帧驱动必须自愈补齐左手 IK");

            var upper = new GameObject("upper").transform;
            var lower = new GameObject("lower").transform;
            var hand = new GameObject("hand").transform;
            upper.SetParent(host.transform, false);
            lower.SetParent(upper, false);
            hand.SetParent(lower, false);
            upper.position = Vector3.zero;
            lower.position = new Vector3(0f, 0f, 0.5f);
            hand.position = new Vector3(0f, 0f, 1f);
            var target = new GameObject("LeftHandTarget").transform;
            target.SetParent(host.transform, false);
            target.position = new Vector3(0.2f, 0f, 0.8f);
            target.rotation = Quaternion.identity;

            SetPrivate(ik, "_upperArm", upper);
            SetPrivate(ik, "_lowerArm", lower);
            SetPrivate(ik, "_hand", hand);
            SetPrivate(ik, "blendSeconds", 0f);
            SetPrivate(swapper, "_baseLeftHandTarget", target);

            InvokePrivate(swapper, "LateUpdate");
            Assert.That(Vector3.Distance(hand.position, target.position), Is.LessThan(0.0001f),
                "可见 TP 武器存在时，同一帧写者必须把左腕提交到当前枪械握点");
            Object.DestroyImmediate(host);
        }

        [TestCaseSource(nameof(RifleGripTargets))]
        public void RifleTpPrefabs_BakeGripSpecificWristRotation(
            (string Path, Vector3 Position, string Meaning) spec)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(spec.Path);
            Assert.That(prefab, Is.Not.Null, $"步枪 TP prefab 缺失：{spec.Path}");
            var target = prefab.transform.Find("LeftHandTarget");
            Assert.That(target, Is.Not.Null, $"{spec.Path} 缺 LeftHandTarget");
            Assert.That(Quaternion.Angle(target.localRotation, RifleNaturalWristRotation), Is.LessThan(0.2f),
                $"{spec.Path} 左手腕旋转未使用{spec.Meaning}");
            Assert.That(Vector3.Distance(target.localPosition, spec.Position), Is.LessThan(0.001f),
                $"{spec.Path} 左手目标未落在{spec.Meaning}的实际接触位置");
        }

        [Test]
        public void RifleDefinitions_ResolveBuiltInGripFromLegacyAnimationFamily()
        {
            var plain = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_AssaultRifle.asset");
            var m4 = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Rifle02.asset");
            var scar = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Rifle03.asset");

            Assert.That(plain, Is.Not.Null);
            Assert.That(m4, Is.Not.Null);
            Assert.That(scar, Is.Not.Null);
            Assert.That(plain.RifleHasVerticalGrip, Is.False, "AssaultRifle_01 应握护木");
            Assert.That(m4.RifleHasVerticalGrip, Is.True, "M4A1(Rifle02) 的旧资产迁移回退也必须识别原厂握把");
            Assert.That(scar.RifleHasVerticalGrip, Is.True, "SCAR(Rifle03) 的旧资产迁移回退也必须识别原厂握把");
        }

        [Test]
        public void FactoryGripRifles_RemoveStaleUnderbarrelAttachment()
        {
            var m4 = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_Rifle02.asset");
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            Assert.That(m4, Is.Not.Null);
            Assert.That(catalog.TryGet("attach.lpw.grip.01", out var grip), Is.True);
            var entries = new System.Collections.Generic.List<AttachmentAssetEntry> { grip };

            Assert.That(AttachmentCompatibilityPolicy.IsAllowed(m4, grip), Is.False);
            Assert.That(AttachmentCompatibilityPolicy.RemoveUnsupported(m4, entries), Is.EqualTo(1));
            Assert.That(entries, Is.Empty,
                "old account snapshots must not override M4's baked LeftHandTarget with a duplicate grip marker");
        }

        [Test]
        public void ShoulderReachAssist_UnreachableGripBecomesReachableWithinBound()
        {
            var clavicle = Vector3.zero;
            var upperArm = new Vector3(-0.15f, 0f, 0f);
            var target = new Vector3(0.12f, 0f, 0.55f);
            const float armReach = 0.567f;

            Assert.That(Vector3.Distance(upperArm, target), Is.GreaterThan(armReach),
                "fixture must begin outside the two-bone reach");
            var delta = TPLeftHandIK.ComputeShoulderReachAssistDelta(
                clavicle, upperArm, target, armReach, 30f, 1f);
            var assistedUpperArm = clavicle + delta * (upperArm - clavicle);

            Assert.That(Quaternion.Angle(Quaternion.identity, delta), Is.InRange(0.1f, 30.01f));
            Assert.That(Vector3.Distance(assistedUpperArm, target), Is.LessThanOrEqualTo(armReach + 0.0002f));
        }

        [Test]
        public void ShoulderReachAssist_ReachableHandguardLeavesShoulderUntouched()
        {
            var delta = TPLeftHandIK.ComputeShoulderReachAssistDelta(
                Vector3.zero,
                new Vector3(-0.15f, 0f, 0f),
                new Vector3(0.05f, 0f, 0.30f),
                0.567f,
                30f,
                1f);
            Assert.That(Quaternion.Angle(Quaternion.identity, delta), Is.LessThan(0.001f));
        }

        [TestCaseSource(nameof(HandgunPoses))]
        public void HandgunTpPrefabs_UseCalibratedRootPose((string Path, Vector3 Pos) spec)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(spec.Path);
            Assert.That(prefab, Is.Not.Null, $"手枪 TP prefab 缺失：{spec.Path}");
            var t = prefab.transform;
            Assert.That(Quaternion.Angle(t.localRotation, HandgunRootRotation), Is.LessThan(0.2f),
                $"{spec.Path} 根旋转偏离 @handgun 实测标定值（回退即复现手枪歪斜）");
            Assert.That((t.localPosition - spec.Pos).magnitude, Is.LessThan(0.0015f),
                $"{spec.Path} 根位置偏离标定值（握把不再落在拳头内）");
        }

        /// <summary>向 WeaponAttachmentView 注入已实例化配件（绕过运行时 ApplyAttachments 的
        /// 目录/socket 依赖；_spawned 为 private readonly List，实例本身可变）。</summary>
        private static void InjectSpawned(WeaponAttachmentView view, GameObject spawned)
        {
            var field = typeof(WeaponAttachmentView)
                .GetField("_spawned", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "WeaponAttachmentView._spawned 字段名变更须同步测试");
            var list = field.GetValue(view) as System.Collections.Generic.List<GameObject>;
            list.Add(spawned);
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"missing field: {target.GetType().Name}.{fieldName}");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, $"missing method: {target.GetType().Name}.{methodName}");
            method.Invoke(target, null);
        }
    }
}
