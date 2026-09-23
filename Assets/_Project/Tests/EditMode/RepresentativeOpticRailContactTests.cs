using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Mount-only gate for the four representative weapons.  The rail datum is
    /// measured from the weapon mesh after SkinnedMeshRenderer.BakeMesh, in the
    /// actual Attach_Optic frame.  It intentionally does not read or change any
    /// OpticAim/ADS row.
    /// Exclusion matches the renderer's own name only: walking the parent chain
    /// nuked every mesh under "arms"/"Armature" (substring "arm") and produced
    /// false "no rail vertices" failures.  Expected tops are locked to 0:
    /// 2026-09-21 the sockets were re-seated onto the measured rail tops
    /// (user-approved "全部按实测校准"; OpticSocketRailContactCalibrator), so
    /// every representative weapon must hold rail contact within 2 mm.
    /// </summary>
    public sealed class RepresentativeOpticRailContactTests
    {
        private sealed class Spec
        {
            public readonly string Id;
            public readonly string Fp;
            public readonly string Tp;
            public readonly float FpRailTop;
            public readonly float TpRailTop;

            public Spec(string id, string fp, string tp, float fpRailTop, float tpRailTop)
            {
                Id = id;
                Fp = fp;
                Tp = tp;
                FpRailTop = fpRailTop;
                TpRailTop = tpRailTop;
            }
        }

        private static readonly Spec[] Representatives =
        {
            new Spec("weapon.rifle03", "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab", 0f, 0f),
            new Spec("weapon.m4", "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab", 0f, 0f),
            new Spec("weapon.service_pistol", "Assets/_Project/Prefabs/Weapons/FP_ServicePistol_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab", 0f, 0f),
            new Spec("weapon.sniper01", "Assets/_Project/Prefabs/Weapons/FP_Sniper01_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab", 0f, 0f)
        };

        [Test]
        public void RepresentativeFpAndTpSockets_ContactBakedWeaponRail()
        {
            foreach (var spec in Representatives)
            {
                AssertRail(spec.Id + " FP", spec.Fp, true, spec.FpRailTop);
                AssertRail(spec.Id + " TP", spec.Tp, false, spec.TpRailTop);
            }
        }

        private static void AssertRail(string label, string path, bool fp, float expectedTop)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, label + " prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var socket = FindSocket(instance);
                Assert.That(socket, Is.Not.Null, label + " Attach_Optic");
                if (socket.GetComponent<AttachmentSocket>().GeometryVerified)
                {
                    // Repaired mounts use real clamp footprints (including receiver adapters),
                    // not the old broad highest-vertex proxy which missed floating bases.
                    new NativeAttachmentMountTests().AllFourOpticsSeatTheirClampOnAnActualSurfaceInBothViews(label.Split(' ')[0]);
                    return;
                }
                var railTop = MeasureBakedRailTop(instance, socket, fp);
                Assert.That(float.IsNaN(railTop) || float.IsInfinity(railTop), Is.False,
                    label + " rail top finite");
                Assert.That(railTop, Is.EqualTo(expectedTop).Within(0.002f),
                    label + " socket-to-rail top drifted from the locked measurement; measured "
                    + railTop.ToString("F6") + " m, expected " + expectedTop.ToString("F6") + " m");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static Transform FindSocket(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "Attach_Optic") return t;
            return null;
        }

        private static float MeasureBakedRailTop(GameObject view, Transform socket, bool fp)
        {
            var top = float.NegativeInfinity;
            var measured = false;
            foreach (var smr in view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null || IsExcluded(smr.transform)) continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked, true);
                    foreach (var vertex in baked.vertices)
                    {
                        var local = socket.InverseTransformPoint(smr.transform.TransformPoint(vertex));
                        if (!InRailWindow(local, fp)) continue;
                        measured = true;
                        if (local.y > top) top = local.y;
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(baked); }
            }

            if (!measured)
            {
                foreach (var mf in view.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null || IsExcluded(mf.transform)) continue;
                    foreach (var vertex in mf.sharedMesh.vertices)
                    {
                        var local = socket.InverseTransformPoint(mf.transform.TransformPoint(vertex));
                        if (!InRailWindow(local, fp)) continue;
                        measured = true;
                        if (local.y > top) top = local.y;
                    }
                }
            }

            Assert.That(measured, Is.True, "no baked weapon rail vertices found for " + view.name);
            return top;
        }

        private static bool InRailWindow(Vector3 local, bool fp)
        {
            return fp
                ? local.x >= -0.30f && local.x <= 0.30f && Mathf.Abs(local.z) <= 0.08f
                : local.z >= -0.30f && local.z <= 0.30f && Mathf.Abs(local.x) <= 0.08f;
        }

        /// <summary>仅按渲染器自身节点名排除装饰件/活动件（scope/机瞄/弹/消音器/刀/滑套/
        /// 弹匣/握把/脚架/手臂）；不得沿父链上溯——"arms"/"Armature" 含子串 "arm"
        /// 会把整条骨骼下的枪体一并误杀（2026-09-21 SCAR 误报根因）。</summary>
        private static bool IsExcluded(Transform renderer)
        {
            if (renderer == null) return true;
            var name = renderer.name.ToLowerInvariant();
            return name.Contains("scope") || name.Contains("iron") || name.Contains("sight")
                || name.Contains("bullet") || name.Contains("silencer") || name.Contains("knife")
                || name.Contains("slider") || name.Contains("mag") || name.Contains("grip")
                || name.Contains("bipod") || name.Contains("arms");
        }
    }
}
