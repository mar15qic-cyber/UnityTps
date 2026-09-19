using System.Linq;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// ADS 开火路由测试（抖动修复回归锁）：LPW 资产模式 ProceduralOnly 本身决定路由，
    /// 且完成 AnchoredDualPoseV2 迁移后 29 把 LPW 都必须走 ProceduralFire。
    /// </summary>
    public sealed class FPWeaponAdsFireRoutingTests
    {
        private const string ManifestPath = "Assets/_Project/ScriptableObjects/Weapons/LPW/LPWWeaponManifest.asset";
        private const string FpPrefabDir = "Assets/_Project/Prefabs/Weapons/LPW/FP/";

        [Test]
        public void EveryProceduralOnlyProfile_RoutesToProceduralFire_RegardlessOfV2Completeness()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);

            int proceduralOnly = 0, legacyRoutingCount = 0, v2RoutingCount = 0;
            foreach (LPWWeaponSpec spec in manifest.Weapons)
            {
                string token = System.IO.Path.GetFileNameWithoutExtension(spec.sourcePrefabPath);
                string path = FpPrefabDir + "FP_" + token + "_View.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, spec.definitionId + " prefab missing at " + path);

                FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
                Assert.That(profile, Is.Not.Null, spec.definitionId + " 缺 FPWeaponPoseProfile");
                Assert.That(profile.AdsFirePresentationMode, Is.EqualTo(LPWAdsFirePresentationMode.ProceduralOnly),
                    spec.definitionId + " 生产资产必须是 ProceduralOnly");

                bool routes = FPWeaponAnimator.RoutesToProceduralAdsFire(profile);
                Assert.That(routes, Is.True,
                    spec.definitionId + " (mode=" + profile.AdsFirePresentationMode
                    + ", v2Complete=" + profile.HasCompleteAnchoredDualPoseV2 + ") 必须路由到 ProceduralFire");

                proceduralOnly++;
                if (profile.HasCompleteAnchoredDualPoseV2) v2RoutingCount++;
                else legacyRoutingCount++;
            }

            Assert.That(proceduralOnly, Is.EqualTo(29), "29 把 LPW 全部应为 ProceduralOnly");
            // D4-A.4（Gate A 复审 §2.3）：本测试属 LPW 历史范围——V2 完整性是资产状态普查，
            // 不再作主线硬断言（Docs/25：LPW 冻结遗产不修不扩，8 把 Legacy 视图是既成历史）。
            // 主线契约只锁行为：29 把全部路由 ProceduralFire（RoutesToProceduralAdsFire 与
            // V2 完整性解耦，正是本测试名语义）；分区计数作为不变式 + 诊断日志保留。
            Assert.That(v2RoutingCount + legacyRoutingCount, Is.EqualTo(proceduralOnly),
                "V2/Legacy 分区必须完备覆盖 29 把");
            Debug.Log($"[FPWeaponAdsFireRouting] LPW V2 census: v2={v2RoutingCount} legacy={legacyRoutingCount}（冻结遗产现状，非主线门禁）");
        }

        [Test]
        public void Aug_IsCompleteV2_AndAllOtherLpwWeaponsAreCompleteV2()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);

            FPWeaponPoseProfile aug = LoadProfile("AssaultRifle2_01");
            Assert.That(aug.HasCompleteAnchoredDualPoseV2, Is.True, "AUG (lpw.rifle.02) 应为完整 V2");

            foreach (string token in new[] { "AssaultRifle1_01", "AssaultRifle3_01", "Pistol1_01", "SMG1_01" })
            {
                FPWeaponPoseProfile profile = LoadProfile(token);
                Assert.That(profile.HasCompleteAnchoredDualPoseV2, Is.True, token + " 应为完整 V2");
                Assert.That(FPWeaponAnimator.RoutesToProceduralAdsFire(profile), Is.True,
                    token + " 必须路由 ProceduralFire");
            }
        }

        [Test]
        public void LpwSharedAdsDepth_StaysWithinAuthoredCompositionEnvelope()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);

            foreach (LPWWeaponSpec spec in manifest.Weapons)
            {
                string token = System.IO.Path.GetFileNameWithoutExtension(spec.sourcePrefabPath);
                FPWeaponPoseProfile profile = LoadProfile(token);
                Vector3 shared = profile.AdsViewmodelLocalPosition;
                Assert.That(float.IsFinite(shared.x) && float.IsFinite(shared.y)
                    && float.IsFinite(shared.z), Is.True, spec.definitionId + " shared depth must be finite");
                Assert.That(Mathf.Abs(shared.z), Is.LessThanOrEqualTo(.12f),
                    spec.definitionId + " shared depth must stay within the authored arms envelope");
                if (spec.definitionId == "lpw.rifle.02")
                    Assert.That(shared.z, Is.EqualTo(.1134f).Within(.00001f), "AUG reference depth changed");
            }
        }

        [Test]
        public void NativeWeapons_WithoutProfile_KeepLegacyAimFire()
        {
            // 原生 LPFP 武器没有 FPWeaponPoseProfile：作者 aim_fire 动画即反馈，保持 LegacyAimFire。
            Assert.That(FPWeaponAnimator.RoutesToProceduralAdsFire(null), Is.False);

            string[] nativeViews =
            {
                "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab",
                "Assets/_Project/Prefabs/Weapons/FP_Handgun02_View.prefab",
            };
            foreach (string path in nativeViews)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
                Assert.That(profile, Is.Null, path + " 原生武器不应带 FPWeaponPoseProfile");
            }
        }

        private static FPWeaponPoseProfile LoadProfile(string token)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FpPrefabDir + "FP_" + token + "_View.prefab");
            Assert.That(prefab, Is.Not.Null, token);
            FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
            Assert.That(profile, Is.Not.Null, token);
            return profile;
        }
    }
}
