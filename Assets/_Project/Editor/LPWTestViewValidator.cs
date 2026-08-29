using System;
using System.Linq;
using System.Text;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// LPW 测试视图结构校验（LPW spike，Docs/15 前置可行性验证）：
    /// 断言 3 把 LPW 测试武器的 FP/TP 视图、WeaponDefinition 与数值接线完整。
    /// 触发：Tools/LPW Test View Validator。
    /// </summary>
    public static class LPWTestViewValidator
    {
        private sealed class Spec
        {
            public string Name;      // 测试武器短名（文件名段）
            public string GunToken;  // 底座视图中原枪节点名片段
            public bool HasMag;      // 是否有独立弹匣部件（挂 mag 骨骼）
            public string WeaponId;  // balance/definition weaponId
        }

        private static readonly Spec[] Specs =
        {
            new Spec { Name = "Rifle2_02", GunToken = "assault_rifle_01", HasMag = true, WeaponId = "lpw.rifle.02" },
            new Spec { Name = "SMG1_01", GunToken = "smg_01", HasMag = true, WeaponId = "lpw.smg.01" },
            new Spec { Name = "Pistol5_06", GunToken = "handgun_01", HasMag = false, WeaponId = "lpw.pistol.05" },
        };

        private const string PrefabFolder = "Assets/_Project/Prefabs/Weapons/LPWTest";
        private const string DefFolder = "Assets/_Project/ScriptableObjects/Weapons/LPWTest";
        private const string BalancePath = "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset";

        private static int _pass, _fail;
        private static readonly StringBuilder Sb = new StringBuilder();

        private static void Check(bool ok, string label)
        {
            if (ok) { _pass++; Sb.AppendLine("  [PASS] " + label); }
            else { _fail++; Sb.AppendLine("  [FAIL] " + label); }
        }

        [MenuItem("Tools/LPW Test View Validator")]
        public static void Run()
        {
            _pass = 0; _fail = 0; Sb.Clear();
            Sb.AppendLine("===== LPW Test View Validator =====");
            foreach (var s in Specs) ValidateFp(s);
            foreach (var s in Specs) ValidateTp(s);
            foreach (var s in Specs) ValidateDef(s);
            Sb.AppendLine("LPWTestViewValidator: " + _pass + " PASS / " + _fail + " FAIL");
            Debug.Log(Sb.ToString());
            if (_fail > 0) Debug.LogError("[LPWTestViewValidator] " + _fail + " 项失败，详见上方日志");
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        private static void ValidateFp(Spec s)
        {
            Sb.AppendLine("== FP " + s.Name);
            string path = PrefabFolder + "/FP_LPW_" + s.Name + "_View.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Check(false, "prefab 存在: " + path); return; }
            var inst = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var root = inst.transform;
                var armature = FindDeep(root, "Armature");
                var weaponBone = FindDeep(armature, "weapon");
                var wrapper = weaponBone != null ? weaponBone.Find("LPW_Gun") : null;
                Check(wrapper != null, "LPW_Gun wrapper 挂于 Armature/weapon");

                var poseIk = inst.GetComponent<Game.Presentation.Animation.FPLeftHandIK>();
                var magazineView = inst.GetComponent<Game.Presentation.Animation.DetachableMagazineView>();
                if (s.HasMag)
                {
                    var profile = inst.GetComponent<Game.Presentation.Animation.FPWeaponPoseProfile>();
                    Check(profile != null, "FPWeaponPoseProfile 已配置");
                    if (profile != null)
                    {
                        Check(profile.HasCompleteInterfaceLayout,
                            "RightHandGrip/LeftSupportGrip/Trigger/MagazineWell/MagazineGrip 接口完整");
                        Check(profile.WeaponRoot == wrapper, "RightHandGrip 根校准指向 LPW_Gun");
                        Check(profile.RightHandGrip != profile.LeftSupportGrip
                            && profile.RightHandGrip != profile.MagazineGrip
                            && profile.MagazineGrip != profile.MagazineWell,
                            "握持、抓匣、插匣目标互不复用");
                        Check(profile.ValidateInterfaceLayout(0.35f),
                            "右手握把/扳机接口可验证（Grip误差="
                            + profile.RightHandGripError.ToString("F3") + "m, Rotation误差="
                            + profile.RightHandGripRotationError.ToString("F1") + "°）");
                    }
                    Check(poseIk != null && poseIk.LeftHandTarget != null,
                        "FPLeftHandIK 与武器专属 LeftHandTarget 已配置");
                    Check(magazineView != null, "DetachableMagazineView 已配置");
                }

                var wv = inst.GetComponentInChildren<Game.Presentation.Weapon.WeaponView>(true);
                Check(wv != null, "WeaponView 组件存在");
                Check(inst.GetComponentInChildren<Game.Presentation.Animation.FPWeaponAnimator>(true) != null, "FPWeaponAnimator 组件存在");
                Check(inst.GetComponentInChildren<Animancer.AnimancerComponent>(true) != null, "Animancer 组件存在");

                if (wv != null)
                {
                    var so = new SerializedObject(wv);
                    var muzzle = so.FindProperty("muzzle").objectReferenceValue as Transform;
                    var shellPort = so.FindProperty("shellPort").objectReferenceValue as Transform;
                    var sightRef = so.FindProperty("sightReference").objectReferenceValue as Transform;
                    Check(muzzle != null && muzzle.IsChildOf(wrapper), "muzzle 指向 LPW 枪");
                    Check(shellPort != null && shellPort.IsChildOf(wrapper), "shellPort 指向 LPW 枪");
                    Check(sightRef != null && sightRef.IsChildOf(wrapper), "sightReference 指向 LPW 枪");

                    if (muzzle != null && wrapper != null)
                    {
                        float angle = Vector3.Angle(muzzle.forward, -wrapper.right);
                        Check(angle < 3f, "膛线方向 Muzzle.forward 与枪轴夹角 " + angle.ToString("F2") + "° < 3°");
                    }
                }

                int fpLayer = LayerMask.NameToLayer("FirstPersonView");
                int badLayer = wrapper != null
                    ? wrapper.GetComponentsInChildren<Transform>(true).Count(t => t.gameObject.layer != fpLayer) : 1;
                Check(badLayer == 0, "wrapper 子树全部 FirstPersonView 层");
                int colliders = wrapper != null ? wrapper.GetComponentsInChildren<Collider>(true).Length : 1;
                Check(colliders == 0, "wrapper 子树无碰撞体");

                if (s.HasMag)
                {
                    var magBone = FindDeep(armature, "mag");
                    var magPart = magazineView != null ? magazineView.MagazinePart : null;
                    Check(magPart != null && magazineView.InstalledParent != null,
                        "弹匣安装态与左手携带态均有显式引用");
                    Check(magPart != null && !magPart.IsChildOf(magBone),
                        "替换弹匣不再直接继承原包 mag 骨骼的错误 bind pose");
                }

                var armsNode = root.Find("arms");
                Transform gunNode = null;
                if (armsNode != null)
                    foreach (Transform c in armsNode)
                        if (c.name.IndexOf(s.GunToken, StringComparison.OrdinalIgnoreCase) >= 0 && !c.name.StartsWith("arms_"))
                        { gunNode = c; break; }
                Check(gunNode != null && !gunNode.gameObject.activeSelf, "原 LPFP 枪械蒙皮已禁用");

                if (s.Name == "Rifle2_02")
                {
                    var lpwGun = wrapper != null ? wrapper.GetChild(0) : null;
                    var anchor = lpwGun != null ? lpwGun.Find("Attachment_Muzzle") : null;
                    Check(anchor != null && anchor.Find("Muffler_01_1") != null, "消音器演示（Attachment_Muzzle/Muffler_01_1）");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(inst); }
        }

        private static void ValidateTp(Spec s)
        {
            Sb.AppendLine("== TP " + s.Name);
            string path = PrefabFolder + "/TP_LPW_" + s.Name + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Check(false, "prefab 存在: " + path); return; }
            var inst = PrefabUtility.LoadPrefabContents(path);
            try
            {
                // TPWeaponMeshSwapper 用 transform.Find 直查根子节点
                Check(inst.transform.Find("Muzzle") != null, "Muzzle 为根直接子节点");
                Check(inst.transform.Find("LeftHandTarget") != null, "LeftHandTarget 为根直接子节点");
                Check(inst.GetComponentsInChildren<Collider>(true).Length == 0, "无碰撞体");
                Check(inst.transform.localPosition != Vector3.zero, "根握把偏移已写入（非零）");
                var muzzle = inst.transform.Find("Muzzle");
                var wrapper = inst.transform.Find("LPW_Gun");
                if (muzzle != null && wrapper != null)
                {
                    float angle = Vector3.Angle(muzzle.forward, -wrapper.right);
                    Check(angle < 3f, "膛线方向 Muzzle.forward 与枪轴夹角 " + angle.ToString("F2") + "° < 3°");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(inst); }
        }

        private static void ValidateDef(Spec s)
        {
            Sb.AppendLine("== Def " + s.Name);
            string path = DefFolder + "/LPW_" + s.Name + ".asset";
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
            if (def == null) { Check(false, "WeaponDefinition 存在: " + path); return; }
            Check(def.FirstPersonViewPrefab != null, "FirstPersonViewPrefab 接线");
            Check(def.ThirdPersonViewPrefab != null, "ThirdPersonViewPrefab 接线");
            var anim = def.FirstPersonAnimations;
            Check(anim.Idle != null && anim.Fire != null && anim.Draw != null && anim.Holster != null, "动画集 Idle/Fire/Draw/Holster 完整");
            Check(anim.ReloadAmmoLeft != null && anim.ReloadOutOfAmmo != null, "换弹双 clip 完整");
            if (s.WeaponId.IndexOf("rifle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Check(def.FirstPersonAnimationFamily == FirstPersonAnimationFamily.Rifle02
                    && def.RifleHasVerticalGrip,
                    "带垂直握把 Rifle 使用 Rifle02 动画族");
                Check(anim.Idle.name.IndexOf("assault_rifle_02", StringComparison.OrdinalIgnoreCase) >= 0
                    && anim.ReloadAmmoLeft.name.IndexOf("assault_rifle_02", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Resolved FP clip 实际来自 Low Poly FPS Pack rifle02");
            }
            else
            {
                Check(def.FirstPersonAnimationFamily == FirstPersonAnimationFamily.Native,
                    "SMG/Pistol 不参与 Rifle FP 动画族规则");
            }
            Check(def.AudioProfile != null, "audioProfile 接线");

            var balance = AssetDatabase.LoadAssetAtPath<Game.Core.DemoBalanceConfig>(BalancePath);
            if (balance == null) { Check(false, "DemoBalanceConfig 加载"); return; }
            bool missing = false;
            Application.LogCallback onLog = (cond, stackTrace, type) =>
            { if (type == LogType.Error && cond.Contains("Missing weapon stat")) missing = true; };
            Application.logMessageReceived += onLog;
            var stat = balance.GetWeaponStat(s.WeaponId);
            Application.logMessageReceived -= onLog;
            Check(!missing && stat.Damage >= 1, "balance 条目 " + s.WeaponId + " 可解析（Damage=" + stat.Damage + "）");
        }
    }
}
