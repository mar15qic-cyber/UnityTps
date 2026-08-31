using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>Idempotent production pipeline for the 29 canonical LPW *_01 weapons.</summary>
    public static class LPWWeaponProductionPipeline
    {
        private const string SourceRoot = "Assets/LowPolyWeapons/Prefabs";
        private const string FpRoot = "Assets/_Project/Prefabs/Weapons/LPW/FP";
        private const string TpRoot = "Assets/_Project/Prefabs/Weapons/LPW/TP";
        private const string DefinitionRoot = "Assets/_Project/ScriptableObjects/Weapons/LPW";
        private const string ManifestPath = DefinitionRoot + "/LPWWeaponManifest.asset";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/Account/WeaponAssetCatalog.asset";
        private const string RuntimeRegistryPath = "Assets/_Project/Resources/LPWProductionRuntimeRegistry.asset";
        private const string BalancePath = "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset";
        private const string ArtifactRoot = "Assets/_Project/Artifacts/LPWProduction";
        private static readonly string[] StageOneDefinitionIds = { "lpw.rifle.02", "lpw.smg.01" };

        private static readonly float[] DamageFactors = { .90f, .96f, 1.02f, 1.08f, 1.15f, 1.22f };
        private static readonly float[] RpmFactors = { 1.12f, 1.06f, 1f, .95f, .90f, .85f };
        private static readonly float[] HandlingFactors = { 1.12f, 1.06f, 1f, .95f, .90f, .85f };
        private static readonly float[] ReloadFactors = { 1.08f, 1.04f, 1f, .97f, .94f, .91f };
        private static readonly float[] RangeFactors = { .90f, .96f, 1f, 1.05f, 1.10f, 1.15f };

        // Arena_LPWTest full-ADS measurements. Values are weapon-bone-local translations,
        // applied once to LPW_Gun in the generated prefab (never as a runtime ADS offset).
        private static readonly Dictionary<string, Vector3> AdsCenterOffsets = new(StringComparer.Ordinal)
        {
            ["lpw.rifle.01"] = new(-0.00709672645f, 0.026902508f, -0.0176532734f),
            ["lpw.rifle.02"] = new(-0.007203249f, 0.021839533f, -0.0143229961f),
            ["lpw.rifle.03"] = new(-0.00744907f, -0.00689503457f, 0.00452566938f),
            ["lpw.rifle.04"] = new(-0.00716905948f, 0.009624661f, -0.006312889f),
            ["lpw.rifle.05"] = new(-0.0071404255f, 0.00906890351f, -0.00594929047f),
            ["lpw.rifle.06"] = new(-0.007261397f, 0.008282381f, -0.005428978f),
            ["lpw.pistol.01"] = new(-0.011141805f, 0.0274923f, -0.0180239175f),
            ["lpw.pistol.02"] = new(-0.011142401f, 0.0278185718f, -0.018237872f),
            ["lpw.pistol.03"] = new(-0.0110884625f, 0.007913175f, -0.00518570142f),
            ["lpw.pistol.04"] = new(-0.0114465309f, 0.0296814125f, -0.0194422137f),
            ["lpw.pistol.05"] = new(-0.0114128459f, 0.0217011981f, -0.014212098f),
            ["lpw.pistol.06"] = new(-0.0114186676f, -0.009855288f, 0.00646967068f),
            ["lpw.shotgun.01"] = new(-0.014481782f, -0.06356f, 0.04170766f),
            ["lpw.shotgun.02"] = new(-0.0148476446f, -0.0228339732f, 0.0149801709f),
            ["lpw.shotgun.03"] = new(-0.0149791734f, -0.0235786829f, 0.0154676307f),
            ["lpw.shotgun.04"] = new(-0.0146823106f, 0.0101366807f, -0.006644414f),
            ["lpw.shotgun.05"] = new(-0.0147261592f, -0.0235016327f, 0.0154190045f),
            ["lpw.smg.01"] = new(-0.0130786123f, -0.02393478f, 0.0156952329f),
            ["lpw.smg.02"] = new(-0.0123052914f, -0.01765124f, 0.0115811834f),
            ["lpw.smg.03"] = new(-0.01213762f, -0.0257722456f, 0.0169115681f),
            ["lpw.smg.04"] = new(-0.0122092329f, 0.001664157f, -0.00108994136f),
            ["lpw.smg.05"] = new(-0.0125228344f, -0.00407646829f, 0.0026827862f),
            ["lpw.smg.06"] = new(-0.012365181f, -0.03387809f, 0.0222215671f),
            ["lpw.sniper.01"] = new(-0.009077851f, 0.00610472355f, -0.00399799831f),
            ["lpw.sniper.02"] = new(-0.004354446f, 0.0457377248f, -0.0299980827f),
            ["lpw.sniper.03"] = new(-0.009088043f, 0.003685093f, -0.002411804f),
            ["lpw.sniper.04"] = new(-0.00439165f, 0.0313020647f, -0.0205273721f),
            ["lpw.sniper.05"] = new(-0.00482531125f, 0.0407760665f, -0.026704926f),
            ["lpw.sniper.06"] = new(-0.00469668629f, 0.0564183f, -0.03696983f),
        };

        private sealed class Family
        {
            public string Folder;
            public string SourcePrefix;
            public string IdSegment;
            public WeaponCatalogCategory Category;
            public WeaponSlotType Slot;
            public WeaponFireMode FireMode;
            public int Count;
            public string BaseDefinitionId;
            public string FpTemplate;
            public string TpTemplate;
            public string[] Names;
            public long[] Prices;
            public int[] Levels;
            public int[] MagazineOffsets;
        }

        private static readonly Family[] Families =
        {
            new()
            {
                Folder = "AssaultRifle", SourcePrefix = "AssaultRifle", IdSegment = "rifle",
                Category = WeaponCatalogCategory.Rifle, Slot = WeaponSlotType.Primary,
                FireMode = WeaponFireMode.Automatic, Count = 6, BaseDefinitionId = "rifle.day3",
                FpTemplate = "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab",
                TpTemplate = "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab",
                Names = Enumerable.Range(1, 6).Select(x => $"现代突击步枪 {x:00}").ToArray(),
                Prices = new long[] { 3000, 4500, 6500, 8500, 11000, 14000 },
                Levels = new[] { 3, 5, 8, 11, 14, 18 }, MagazineOffsets = new[] { -5, 0, 0, 5, 5, 10 }
            },
            new()
            {
                Folder = "Pistols", SourcePrefix = "Pistol", IdSegment = "pistol",
                Category = WeaponCatalogCategory.Pistol, Slot = WeaponSlotType.Secondary,
                FireMode = WeaponFireMode.SemiAutomatic, Count = 6, BaseDefinitionId = "pistol.day2",
                FpTemplate = "Assets/_Project/Prefabs/Weapons/FP_ServicePistol_View.prefab",
                TpTemplate = "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab",
                Names = Enumerable.Range(1, 6).Select(x => $"战术半自动手枪 {x:00}").ToArray(),
                Prices = new long[] { 1000, 1800, 2800, 4000, 5500, 7500 },
                Levels = new[] { 2, 3, 5, 7, 9, 12 }, MagazineOffsets = new[] { -2, 0, 2, 3, 5, 6 }
            },
            new()
            {
                Folder = "Shotguns", SourcePrefix = "Shotgun", IdSegment = "shotgun",
                Category = WeaponCatalogCategory.Shotgun, Slot = WeaponSlotType.Primary,
                FireMode = WeaponFireMode.SemiAutomatic, Count = 5, BaseDefinitionId = "shotgun.01",
                FpTemplate = "Assets/_Project/Prefabs/Weapons/FP_Shotgun01_View.prefab",
                TpTemplate = "Assets/_Project/Prefabs/Weapons/TP_Weapon_Shotgun_01.prefab",
                Names = Enumerable.Range(1, 5).Select(x => $"战术霰弹枪 {x:00}").ToArray(),
                Prices = new long[] { 3000, 5000, 7500, 10000, 13500 },
                Levels = new[] { 4, 7, 10, 14, 18 }, MagazineOffsets = new[] { 0, 1, 2, 3, 4 }
            },
            new()
            {
                Folder = "SMG", SourcePrefix = "SMG", IdSegment = "smg",
                Category = WeaponCatalogCategory.Smg, Slot = WeaponSlotType.Primary,
                FireMode = WeaponFireMode.Automatic, Count = 6, BaseDefinitionId = "smg.01",
                FpTemplate = "Assets/_Project/Prefabs/Weapons/FP_SMG01_View.prefab",
                TpTemplate = "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_01.prefab",
                Names = Enumerable.Range(1, 6).Select(x => $"紧凑型冲锋枪 {x:00}").ToArray(),
                Prices = new long[] { 2500, 4000, 5500, 7500, 9500, 12000 },
                Levels = new[] { 3, 5, 7, 9, 12, 15 }, MagazineOffsets = new[] { -5, 0, 0, 5, 5, 10 }
            },
            new()
            {
                Folder = "SniperRifle", SourcePrefix = "SniperRifle", IdSegment = "sniper",
                Category = WeaponCatalogCategory.Sniper, Slot = WeaponSlotType.Primary,
                FireMode = WeaponFireMode.SemiAutomatic, Count = 6, BaseDefinitionId = "sniper.01",
                FpTemplate = "Assets/_Project/Prefabs/Weapons/FP_Sniper01_View.prefab",
                TpTemplate = "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab",
                Names = Enumerable.Range(1, 6).Select(x => $"精确射手步枪 {x:00}").ToArray(),
                Prices = new long[] { 4500, 7000, 10000, 14000, 19000, 25000 },
                Levels = new[] { 6, 10, 14, 18, 23, 28 }, MagazineOffsets = new[] { 0, 0, 1, 2, 3, 4 }
            }
        };

        [MenuItem("Tools/LPW Production/Generate 29 Canonical Weapons")]
        public static void GenerateAll()
        {
            EnsureFolders();
            LPWWeaponManifest manifest = LoadSchemaSixManifest();
            List<LPWWeaponSpec> specs = manifest.Weapons.ToList();
            if (specs.Count != 29) throw new InvalidOperationException("Expected 29 LPW specs, got " + specs.Count);
            List<string> legacy = specs.Where(x => x.poseCalibrationMode != LPWPoseCalibrationMode.DualLayerVerified)
                .Select(x => x.definitionId).ToList();
            if (legacy.Count > 0)
                throw new InvalidOperationException("Full generation is locked until every weapon is DualLayerVerified: "
                    + string.Join(", ", legacy));

            // Staging prefabs must be imported immediately: SaveAsPrefabAsset returns
            // null while asset importing is suspended, so StartAssetEditing cannot wrap
            // this loop.  The direct target overwrite performed after each staging save
            // still preserves every existing prefab GUID.
            foreach (LPWWeaponSpec spec in specs)
            {
                GenerateFp(spec);
                GenerateTp(spec);
            }
            AssetDatabase.Refresh();

            // Prefabs must be imported before definitions can hold stable direct references.
            foreach (LPWWeaponSpec spec in specs)
                GenerateDefinition(spec);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            UpdateBalance(specs);
            UpdateCatalog(specs);
            UpdateRuntimeRegistry();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[LPWProduction] Generated 29 canonical FP/TP/Definition assets, manifest, balance and catalog entries.");
        }

        [MenuItem("Tools/LPW Production/Generate Stage 1 AUG + MAC")]
        public static void GenerateStageOne()
        {
            EnsureFolders();
            LPWWeaponManifest manifest = LoadSchemaSixManifest();
            List<LPWWeaponSpec> specs = StageOneDefinitionIds.Select(id =>
            {
                LPWWeaponSpec match = manifest.Weapons.SingleOrDefault(x => x.definitionId == id);
                if (match == null) throw new InvalidOperationException("Manifest row missing: " + id);
                if (match.poseCalibrationMode != LPWPoseCalibrationMode.DualLayerVerified
                    || !match.hasGripCalibration || !match.hasSightCalibration || !match.hasAdsCalibration)
                    throw new InvalidOperationException("Stage-one calibration is incomplete: " + id);
                return match;
            }).ToList();

            foreach (LPWWeaponSpec spec in specs) GenerateFp(spec);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[LPWProduction] Regenerated only the calibrated AUG and MAC FP prefabs from schema 6.");
        }

        [MenuItem("Tools/LPW Production/Validate 29 Canonical Weapons")]
        public static void ValidateAll()
        {
            List<string> errors = new();
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null) errors.Add("Manifest missing");
            else
            {
                if (manifest.SchemaVersion != 6) errors.Add("Manifest schema=" + manifest.SchemaVersion + " (expected 6)");
                if (manifest.Weapons.Count != 29) errors.Add("Manifest count=" + manifest.Weapons.Count);
            }

            WeaponAssetCatalog catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            if (catalog == null) errors.Add("Catalog missing");
            else
            {
                int lpwCount = catalog.Entries.Count(x => x != null && x.itemId != null && x.itemId.StartsWith("weapon.lpw.", StringComparison.Ordinal));
                if (lpwCount != 29) errors.Add("Catalog LPW count=" + lpwCount);
                if (catalog.Entries.Count != 39) errors.Add("Catalog total count=" + catalog.Entries.Count);
                foreach (WeaponAssetEntry entry in catalog.Entries)
                {
                    if (entry.definition == null) errors.Add("Catalog direct definition missing " + entry.itemId);
                    else if (entry.definition.WeaponId != entry.definitionId) errors.Add("Catalog definition mismatch " + entry.itemId);
                    if (entry.previewPrefab == null) errors.Add("Catalog direct preview missing " + entry.itemId);
                }
            }

            if (manifest != null)
            {
                HashSet<string> ids = new(StringComparer.Ordinal);
                foreach (LPWWeaponSpec spec in manifest.Weapons)
                {
                    if (!ids.Add(spec.itemId)) errors.Add("Duplicate item id " + spec.itemId);
                    if (!spec.sourcePrefabPath.EndsWith("_01.prefab", StringComparison.Ordinal)) errors.Add("Non-canonical source " + spec.sourcePrefabPath);
                    if (spec.schemaVersion != 6) errors.Add("Spec schema " + spec.itemId + "=" + spec.schemaVersion);
                    if (!AdsCenterOffsets.ContainsKey(spec.definitionId)) errors.Add("ADS static offset missing " + spec.itemId);
                    if (spec.category == WeaponCatalogCategory.Rifle && spec.tier == 3
                        && spec.animationFamily != FirstPersonAnimationFamily.Rifle01)
                        errors.Add("G36C must use Rifle01 (no vertical grip) " + spec.itemId);
                    if (spec.category == WeaponCatalogCategory.Sniper)
                    {
                        string expected = spec.usesBoltAction ? "sniper.01" : "sniper.02";
                        if (spec.animationDefinitionId != expected)
                            errors.Add("Sniper action/template mismatch " + spec.itemId + " -> " + spec.animationDefinitionId);
                    }
                    ValidateSpecAssets(spec, errors);
                }
            }

            if (errors.Count > 0)
                throw new InvalidOperationException("[LPWProduction] Validation failed:\n" + string.Join("\n", errors));
            Debug.Log("[LPWProduction] VALIDATION PASS: 29 LPW + 10 legacy = 39 catalog weapons.");
        }

        [MenuItem("Tools/LPW Production/Generate 29 Front-Side Reference Sheets")]
        public static void GenerateReferenceSheets()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null || manifest.Weapons.Count != 29)
                throw new InvalidOperationException("Generate production assets before reference sheets.");
            string output = ArtifactRoot + "/ReferenceSheets";
            EnsureFolder(output);
            List<string> index = new() { "itemId,definitionId,displayName,assetKey,file" };
            foreach (LPWWeaponSpec spec in manifest.Weapons)
            {
                string fileName = spec.itemId.Replace('.', '_') + "_front-side.png";
                RenderReferenceSheet(AssetDatabase.LoadAssetAtPath<GameObject>(TpPath(spec)), output + "/" + fileName);
                index.Add($"{spec.itemId},{spec.definitionId},\"{spec.displayName.Replace("\"", "\"\"")}\",{spec.assetKey},{fileName}");
            }
            File.WriteAllLines(output + "/reference-index.csv", index);
            AssetDatabase.Refresh();
            Debug.Log("[LPWProduction] Generated 29 front-side reference sheets at " + output);
        }

        private static void RenderReferenceSheet(GameObject prefab, string outputPath)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            const int width = 1200, height = 600, layer = 31;
            GameObject instance = null, cameraObject = null, keyObject = null, fillObject = null;
            RenderTexture target = null;
            Texture2D image = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                SetLayerRecursive(instance, layer);
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) throw new InvalidOperationException("No renderers in " + prefab.name);
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                target.Create();
                RenderTexture.active = target;
                GL.Clear(true, true, new Color(.018f, .028f, .045f, 1f));

                cameraObject = new GameObject("LPWReferenceCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.targetTexture = target; camera.orthographic = true; camera.clearFlags = CameraClearFlags.Depth;
                camera.cullingMask = 1 << layer; camera.nearClipPlane = .01f; camera.farClipPlane = 100f;

                keyObject = ReferenceLight("LPWReferenceKey", Quaternion.Euler(35f, -35f, 0f), 1.15f, layer);
                fillObject = ReferenceLight("LPWReferenceFill", Quaternion.Euler(-25f, 145f, 0f), .55f, layer);

                Vector3 longAxis = bounds.size.x >= bounds.size.z ? Vector3.right : Vector3.forward;
                float longHalf = Mathf.Max(bounds.extents.x, bounds.extents.z);
                float crossHalf = longAxis == Vector3.right ? bounds.extents.z : bounds.extents.x;
                float distance = Mathf.Max(bounds.size.magnitude * 2f, 2f);

                camera.rect = new Rect(0f, 0f, .5f, 1f);
                camera.orthographicSize = Mathf.Max(bounds.extents.y, longHalf) * 1.25f;
                Vector3 sideDirection = longAxis == Vector3.right ? Vector3.forward : Vector3.right;
                camera.transform.position = bounds.center + sideDirection * distance;
                camera.transform.LookAt(bounds.center, Vector3.up);
                camera.Render();

                camera.rect = new Rect(.5f, 0f, .5f, 1f);
                camera.orthographicSize = Mathf.Max(bounds.extents.y, crossHalf) * 1.45f;
                camera.transform.position = bounds.center + longAxis * distance;
                camera.transform.LookAt(bounds.center, Vector3.up);
                camera.Render();

                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                File.WriteAllBytes(outputPath, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = null;
                if (image != null) Object.DestroyImmediate(image);
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                if (instance != null) Object.DestroyImmediate(instance);
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                if (keyObject != null) Object.DestroyImmediate(keyObject);
                if (fillObject != null) Object.DestroyImmediate(fillObject);
            }
        }

        private static GameObject ReferenceLight(string name, Quaternion rotation, float intensity, int layer)
        {
            GameObject value = new(name) { hideFlags = HideFlags.HideAndDontSave };
            value.transform.rotation = rotation;
            Light light = value.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = intensity; light.color = new Color(.82f, .9f, 1f);
            light.cullingMask = 1 << layer;
            return value;
        }

        private static List<LPWWeaponSpec> BuildSpecs(DemoBalanceConfig balance)
        {
            List<LPWWeaponSpec> specs = new(29);
            foreach (Family family in Families)
            {
                WeaponStat baseStat = balance.GetWeaponStat(family.BaseDefinitionId);
                for (int i = 0; i < family.Count; i++)
                {
                    int tier = i + 1;
                    string token = family.SourcePrefix + tier;
                    string source = $"{SourceRoot}/{family.Folder}/{token}_01.prefab";
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(source) == null)
                        throw new FileNotFoundException("Canonical LPW source missing", source);

                    // All six LPW assault rifles, including the G36C (tier 3), have a
                    // conventional handguard and no vertical foregrip.  Rifle02/03 pose
                    // the support hand on a vertical grip and therefore cannot be chosen
                    // from the model number alone.
                    FirstPersonAnimationFamily animationFamily = family.Category == WeaponCatalogCategory.Rifle
                        ? FirstPersonAnimationFamily.Rifle01
                        : FirstPersonAnimationFamily.Native;
                    // LPW sniper 1 and 3 are manually cycled bolt-action rifles.  The
                    // SVD/DMR/lever/self-loading models must use Sniper02 so the Fire clip
                    // does not play a nonexistent bolt cycle.
                    bool usesBoltAction = family.Category == WeaponCatalogCategory.Sniper
                        && (tier == 1 || tier == 3);
                    specs.Add(new LPWWeaponSpec
                    {
                        schemaVersion = 6,
                        poseCalibrationMode = LPWPoseCalibrationMode.LegacyUnverified,
                        itemId = $"weapon.lpw.{family.IdSegment}.{tier:00}",
                        definitionId = $"lpw.{family.IdSegment}.{tier:00}",
                        displayName = family.Names[i],
                        sourcePrefabPath = source,
                        assetKey = $"lpw/{token}_01",
                        category = family.Category,
                        slotType = family.Slot,
                        fireMode = family.FireMode,
                        animationFamily = animationFamily,
                        usesBoltAction = usesBoltAction,
                        animationDefinitionId = ResolveAnimationDefinitionId(family.Category, animationFamily, usesBoltAction),
                        firstPersonTemplatePath = ResolveFpTemplate(family.FpTemplate, animationFamily, family.Category, usesBoltAction),
                        thirdPersonTemplatePath = family.TpTemplate,
                        tier = tier,
                        priceCoins = family.Prices[i],
                        unlockLevel = family.Levels[i],
                        stat = ScaleStat(baseStat, i, family.MagazineOffsets[i], family.Category),
                        // LPW source meshes are authored about 33.27 degrees off the LPFP
                        // weapon-bone horizontal axis.  Omitting this roll is what made every
                        // generated gun stand upright/sideways in both hip and ADS poses.
                        fpRootEuler = new Vector3(0f, 90f, 326.73f),
                        fpAdsCenterOffset = AdsCenterOffsets.TryGetValue($"lpw.{family.IdSegment}.{tier:00}", out Vector3 adsOffset)
                            ? adsOffset : Vector3.zero,
                        fpSightReferenceEuler = new Vector3(0f, -90f, 0f),
                        tpRootEuler = new Vector3(0f, 90f, 326.73f),
                        supportsVerifiedAttachments = false
                    });
                }
            }
            return specs;
        }

        private static string ResolveAnimationDefinitionId(WeaponCatalogCategory category, FirstPersonAnimationFamily family, bool usesBoltAction)
        {
            if (category == WeaponCatalogCategory.Rifle)
            {
                if (family == FirstPersonAnimationFamily.Rifle02) return "rifle.02";
                if (family == FirstPersonAnimationFamily.Rifle03) return "rifle.03";
                return "rifle.day3";
            }
            if (category == WeaponCatalogCategory.Pistol) return "pistol.day2";
            if (category == WeaponCatalogCategory.Shotgun) return "shotgun.01";
            if (category == WeaponCatalogCategory.Smg) return "smg.01";
            return usesBoltAction ? "sniper.01" : "sniper.02";
        }

        private static string ResolveFpTemplate(string fallback, FirstPersonAnimationFamily family,
            WeaponCatalogCategory category, bool usesBoltAction)
        {
            if (category == WeaponCatalogCategory.Sniper)
                return usesBoltAction
                    ? "Assets/_Project/Prefabs/Weapons/FP_Sniper01_View.prefab"
                    : "Assets/_Project/Prefabs/Weapons/FP_Sniper02_View.prefab";
            if (family == FirstPersonAnimationFamily.Rifle02)
                return "Assets/_Project/Prefabs/Weapons/FP_Rifle02_View.prefab";
            if (family == FirstPersonAnimationFamily.Rifle03)
                return "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab";
            return fallback;
        }

        private static WeaponStat ScaleStat(WeaponStat stat, int index, int magazineOffset, WeaponCatalogCategory category)
        {
            stat.Damage = Mathf.Max(1, Mathf.RoundToInt(stat.Damage * DamageFactors[index]));
            stat.Rpm = Mathf.Max(1, Mathf.RoundToInt(stat.Rpm * RpmFactors[index] / 10f) * 10);
            stat.MagSize = Mathf.Max(1, stat.MagSize + magazineOffset);
            stat.ReserveAmmo = stat.MagSize * (category == WeaponCatalogCategory.Shotgun || category == WeaponCatalogCategory.Sniper ? 5 : 4);
            stat.ReloadTime = Round2(stat.ReloadTime * ReloadFactors[index]);
            stat.Spread = Round2(stat.Spread * HandlingFactors[index]);
            stat.MaxRange = Mathf.Round(stat.MaxRange * RangeFactors[index] / 5f) * 5f;
            stat.Recoil.PitchDeg = Round2(stat.Recoil.PitchDeg * HandlingFactors[index]);
            stat.Recoil.YawDeg = Round2(stat.Recoil.YawDeg * HandlingFactors[index]);
            stat.Accuracy.BaseHipSpread = Round2(stat.Accuracy.BaseHipSpread * HandlingFactors[index]);
            stat.Accuracy.BaseAdsSpread = Round2(stat.Accuracy.BaseAdsSpread * HandlingFactors[index]);
            return stat;
        }

        private static void GenerateFp(LPWWeaponSpec spec)
        {
            string path = FpPath(spec);
            // Work from the source template directly.  Saving the template into the
            // destination first caused Unity's atomic rename to collide with editor/file
            // watcher read handles before the actual LPW modifications were even applied.
            GameObject root = PrefabUtility.LoadPrefabContents(spec.firstPersonTemplatePath);
            try
            {
                root.name = "FP_" + Token(spec) + "_View";
                Transform weaponBone = FindDeep(root.transform, "weapon");
                if (weaponBone == null) throw new InvalidOperationException("weapon bone missing in " + path);
                Transform existing = weaponBone.Find("LPW_Gun");
                if (existing != null) Object.DestroyImmediate(existing.gameObject);

                GameObject wrapperGo = new("LPW_Gun");
                Transform wrapper = wrapperGo.transform;
                wrapper.SetParent(weaponBone, false);
                wrapper.localPosition = spec.fpRootPosition;
                wrapper.localRotation = Quaternion.Euler(spec.fpRootEuler);

                GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(spec.sourcePrefabPath);
                GameObject gun = (GameObject)PrefabUtility.InstantiatePrefab(source, wrapper);
                gun.name = "LPW_" + Token(spec);
                gun.transform.localPosition = Vector3.zero;
                gun.transform.localRotation = Quaternion.identity;
                gun.transform.localScale = Vector3.one;
                StripColliders(gun);
                DisableOriginalWeaponMeshes(root);
                int fpLayer = LayerMask.NameToLayer("FirstPersonView");
                SetLayerRecursive(wrapperGo, fpLayer >= 0 ? fpLayer : root.layer);

                Bounds bounds = CalculateLocalBounds(wrapper, gun);
                Transform rightHand = FindDeep(root.transform, "hand_R");
                Transform leftHand = FindDeep(root.transform, "hand_L");
                bool dualLayer = spec.poseCalibrationMode == LPWPoseCalibrationMode.DualLayerVerified;
                Vector3 rightHandGripPosition = dualLayer ? spec.fpRightHandGripPosition : EstimatedGrip(bounds, spec);
                Vector3 leftSupportGripPosition = EstimatedSupportGrip(bounds, spec, rightHandGripPosition);
                if (!dualLayer)
                {
                    AlignEstimatedGrips(wrapper, rightHandGripPosition, leftSupportGripPosition, rightHand, leftHand);
                    wrapper.localPosition += spec.fpAdsCenterOffset;
                }

                Transform muzzle = NewMarker(wrapper, "Muzzle", new Vector3(bounds.min.x, bounds.center.y, bounds.center.z), new Vector3(0f, -90f, 0f));
                Transform shell = NewMarker(wrapper, "ShellPort", new Vector3(bounds.center.x, bounds.center.y, bounds.max.z), Vector3.zero);
                Vector3 rearSightPosition = dualLayer
                    ? spec.fpRearSightPosition
                    : new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
                Vector3 rearSightEuler = dualLayer ? spec.fpRearSightEuler : spec.fpSightReferenceEuler;
                Transform rearSight = dualLayer
                    ? NewMarker(wrapper, "RearSight", rearSightPosition, rearSightEuler)
                    : null;
                Transform frontSight = dualLayer
                    ? NewMarker(wrapper, "FrontSight", spec.fpFrontSightPosition, spec.fpFrontSightEuler)
                    : null;
                Transform sight = NewMarker(wrapper, "SightReference", rearSightPosition, rearSightEuler);
                Transform rightGrip = NewMarker(wrapper, "RightHandGrip", rightHandGripPosition,
                    dualLayer ? spec.fpRightHandGripEuler : Vector3.zero);
                if (!dualLayer && rightHand != null) rightGrip.rotation = rightHand.rotation;
                Transform leftGrip = NewMarker(wrapper, "LeftSupportGrip", leftSupportGripPosition, Vector3.zero);
                Transform trigger = NewMarker(wrapper, "Trigger",
                    dualLayer ? spec.fpTriggerPosition : rightGrip.localPosition + new Vector3(-.04f, .01f, 0f),
                    dualLayer ? spec.fpTriggerEuler : Vector3.zero);
                Transform magWell = NewMarker(wrapper, "MagazineWell", new Vector3(bounds.center.x + bounds.size.x * .08f, bounds.min.y + bounds.size.y * .25f, bounds.center.z), Vector3.zero);
                Transform magGrip = NewMarker(wrapper, "MagazineGrip", magWell.localPosition + new Vector3(0f, -Mathf.Max(.08f, bounds.size.y * .25f), 0f), Vector3.zero);

                WeaponView view = root.GetComponent<WeaponView>();
                if (view == null) view = root.AddComponent<WeaponView>();
                SerializedObject viewSo = new(view);
                viewSo.FindProperty("muzzle").objectReferenceValue = muzzle;
                viewSo.FindProperty("shellPort").objectReferenceValue = shell;
                viewSo.FindProperty("sightReference").objectReferenceValue = rearSight != null ? rearSight : sight;
                viewSo.FindProperty("alignAdsToSightAxis").boolValue = true;
                viewSo.ApplyModifiedPropertiesWithoutUndo();

                foreach (FPWeaponPoseProfile old in root.GetComponents<FPWeaponPoseProfile>()) Object.DestroyImmediate(old);
                foreach (FPLeftHandIK old in root.GetComponents<FPLeftHandIK>()) Object.DestroyImmediate(old);
                foreach (DetachableMagazineView old in root.GetComponents<DetachableMagazineView>()) Object.DestroyImmediate(old);

                FPWeaponPoseProfile profile = root.AddComponent<FPWeaponPoseProfile>();
                SerializedObject profileSo = new(profile);
                SetObject(profileSo, "weaponRoot", wrapper);
                SetObject(profileSo, "rightHand", rightHand);
                SetObject(profileSo, "rightHandGrip", rightGrip);
                SetObject(profileSo, "leftSupportGrip", leftGrip);
                SetObject(profileSo, "trigger", trigger);
                SetObject(profileSo, "rearSight", rearSight);
                SetObject(profileSo, "frontSight", frontSight);
                SetObject(profileSo, "magazineWell", magWell);
                SetObject(profileSo, "magazineGrip", magGrip);
                profileSo.FindProperty("hasRootCalibration").boolValue = true;
                profileSo.FindProperty("calibratedRootLocalPosition").vector3Value = wrapper.localPosition;
                profileSo.FindProperty("calibratedRootLocalEulerAngles").vector3Value = wrapper.localEulerAngles;
                profileSo.FindProperty("manualRootTransform").boolValue = false;
                // The generated local transform is the source of truth.  Re-aligning the
                // wrapper to one hand in Awake undoes the two-hand static calibration and
                // is what made the model jump away from the support hand in Play Mode.
                profileSo.FindProperty("alignRootToRightHand").boolValue = false;
                profileSo.FindProperty("hasAdsCalibration").boolValue = dualLayer && spec.hasAdsCalibration;
                profileSo.FindProperty("adsViewmodelLocalPosition").vector3Value = spec.fpAdsViewmodelPosition;
                profileSo.FindProperty("adsViewmodelLocalEulerAngles").vector3Value = spec.fpAdsViewmodelEuler;
                profileSo.ApplyModifiedPropertiesWithoutUndo();

                FPLeftHandIK ik = root.AddComponent<FPLeftHandIK>();
                SerializedObject ikSo = new(ik);
                SetObject(ikSo, "leftHandTarget", leftGrip);
                SetObject(ikSo, "upperArm", FindDeep(root.transform, "arm_L"));
                SetObject(ikSo, "lowerArm", FindDeep(root.transform, "lower_arm_L"));
                SetObject(ikSo, "hand", leftHand);
                SetObject(ikSo, "poseProfile", profile);
                ikSo.FindProperty("reloadOnly").boolValue = true;
                ikSo.ApplyModifiedPropertiesWithoutUndo();

                SavePrefabToTarget(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void GenerateTp(LPWWeaponSpec spec)
        {
            GameObject template = AssetDatabase.LoadAssetAtPath<GameObject>(spec.thirdPersonTemplatePath);
            if (template == null) throw new FileNotFoundException("TP template missing", spec.thirdPersonTemplatePath);

            GameObject root = new("TP_" + Token(spec));
            root.transform.localPosition = template.transform.localPosition;
            root.transform.localRotation = template.transform.localRotation;
            root.transform.localScale = template.transform.localScale;
            try
            {
                Transform wrapper = new GameObject("LPW_Gun").transform;
                wrapper.SetParent(root.transform, false);
                wrapper.localPosition = spec.tpRootPosition;
                wrapper.localRotation = Quaternion.Euler(spec.tpRootEuler);
                GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(spec.sourcePrefabPath);
                GameObject gun = (GameObject)PrefabUtility.InstantiatePrefab(source, wrapper);
                gun.name = "LPW_" + Token(spec);
                gun.transform.localPosition = Vector3.zero;
                gun.transform.localRotation = Quaternion.identity;
                gun.transform.localScale = Vector3.one;
                StripColliders(gun);
                SetLayerRecursive(root, 0);
                Bounds bounds = CalculateLocalBounds(wrapper, gun);
                Transform muzzle = NewMarker(root.transform, "Muzzle", wrapper.TransformPoint(new Vector3(bounds.min.x, bounds.center.y, bounds.center.z)), Vector3.zero, true);
                muzzle.rotation = wrapper.rotation * Quaternion.Euler(0f, -90f, 0f);
                NewMarker(root.transform, "LeftHandTarget", wrapper.TransformPoint(new Vector3(bounds.center.x - bounds.size.x * .15f, bounds.center.y, bounds.center.z)), Vector3.zero, true);
                SavePrefabToTarget(root, TpPath(spec));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void GenerateDefinition(LPWWeaponSpec spec)
        {
            WeaponDefinition template = FindDefinition(spec.animationDefinitionId);
            if (template == null) throw new InvalidOperationException("Base definition missing for " + spec.definitionId);

            string path = DefinitionPath(spec);
            WeaponDefinition definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
            if (definition == null)
            {
                definition = Object.Instantiate(template);
                definition.name = "LPW_" + Token(spec);
                AssetDatabase.CreateAsset(definition, path);
            }
            else
            {
                EditorUtility.CopySerialized(template, definition);
                definition.name = "LPW_" + Token(spec);
            }
            SerializedObject so = new(definition);
            so.FindProperty("weaponId").stringValue = spec.definitionId;
            so.FindProperty("displayName").stringValue = spec.displayName;
            so.FindProperty("fireMode").enumValueIndex = (int)spec.fireMode;
            so.FindProperty("firstPersonViewPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(FpPath(spec));
            so.FindProperty("thirdPersonViewPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(TpPath(spec));
            so.FindProperty("firstPersonAnimationFamily").enumValueIndex = (int)spec.animationFamily;
            if (spec.category == WeaponCatalogCategory.Shotgun)
            {
                AnimationClip insert = FindClip("Assets/Low Poly FPS Pack/Components/Meshes/Arms/Shotgun_01/arms_shotgun_01.fbx", "reload_insert@shotgun_01");
                SerializedProperty animations = so.FindProperty("firstPersonAnimations");
                animations.FindPropertyRelative("ReloadAmmoLeft").objectReferenceValue = insert;
                animations.FindPropertyRelative("ReloadOutOfAmmo").objectReferenceValue = insert;
            }
            else if (spec.category == WeaponCatalogCategory.Sniper)
            {
                AnimationClip insert = FindClip("Assets/Low Poly FPS Pack/Components/Meshes/Arms/Sniper_01/arms_sniper_01.fbx", "reload_insert@sniper_01");
                SerializedProperty animations = so.FindProperty("firstPersonAnimations");
                animations.FindPropertyRelative("ReloadAmmoLeft").objectReferenceValue = insert;
                animations.FindPropertyRelative("ReloadOutOfAmmo").objectReferenceValue = insert;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssetIfDirty(definition);
        }

        private static void WriteManifest(List<LPWWeaponSpec> specs)
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<LPWWeaponManifest>();
                AssetDatabase.CreateAsset(manifest, ManifestPath);
            }
            SerializedObject so = new(manifest);
            so.FindProperty("schemaVersion").intValue = 6;
            SerializedProperty list = so.FindProperty("weapons");
            list.arraySize = specs.Count;
            for (int i = 0; i < specs.Count; i++) WriteSpec(list.GetArrayElementAtIndex(i), specs[i]);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manifest);
        }

        private static void UpdateBalance(List<LPWWeaponSpec> specs)
        {
            DemoBalanceConfig balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            SerializedObject so = new(balance);
            SerializedProperty weapons = so.FindProperty("weapons");
            List<(string id, WeaponStat stat)> keep = new();
            for (int i = 0; i < weapons.arraySize; i++)
            {
                SerializedProperty row = weapons.GetArrayElementAtIndex(i);
                string id = row.FindPropertyRelative("WeaponId").stringValue;
                if (!id.StartsWith("lpw.", StringComparison.Ordinal)) keep.Add((id, ReadWeaponStat(row.FindPropertyRelative("Stat"))));
            }
            weapons.arraySize = keep.Count + specs.Count;
            int cursor = 0;
            foreach (var row in keep) WriteBalanceRow(weapons.GetArrayElementAtIndex(cursor++), row.id, row.stat);
            foreach (LPWWeaponSpec spec in specs) WriteBalanceRow(weapons.GetArrayElementAtIndex(cursor++), spec.definitionId, spec.stat);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(balance);
        }

        private static void UpdateCatalog(List<LPWWeaponSpec> specs)
        {
            WeaponAssetCatalog catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException("Catalog missing: " + CatalogPath);
            SerializedObject so = new(catalog);
            SerializedProperty entries = so.FindProperty("entries");
            List<WeaponAssetEntry> keep = catalog.Entries.Where(x => x != null && !x.itemId.StartsWith("weapon.lpw.", StringComparison.Ordinal)).ToList();
            foreach (WeaponAssetEntry item in keep)
            {
                if (item.definition == null) item.definition = FindDefinition(item.definitionId);
                if (item.itemId.Contains("pistol") || item.itemId.Contains("handgun"))
                {
                    item.category = WeaponCatalogCategory.Pistol;
                    item.slotType = WeaponSlotType.Secondary;
                }
                else if (item.itemId.Contains("shotgun")) item.category = WeaponCatalogCategory.Shotgun;
                else if (item.itemId.Contains("smg")) item.category = WeaponCatalogCategory.Smg;
                else if (item.itemId.Contains("sniper")) item.category = WeaponCatalogCategory.Sniper;
                else item.category = WeaponCatalogCategory.Rifle;
            }
            entries.arraySize = keep.Count + specs.Count;
            int cursor = 0;
            foreach (WeaponAssetEntry item in keep) WriteCatalogRow(entries.GetArrayElementAtIndex(cursor++), item);
            foreach (LPWWeaponSpec spec in specs)
            {
                WriteCatalogRow(entries.GetArrayElementAtIndex(cursor++), new WeaponAssetEntry
                {
                    itemId = spec.itemId,
                    assetKey = spec.assetKey,
                    definitionId = spec.definitionId,
                    previewPrefabPath = TpPath(spec),
                    supportsVerifiedAttachments = false,
                    category = spec.category,
                    slotType = spec.slotType,
                    definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(DefinitionPath(spec)),
                    previewPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TpPath(spec)),
                    stats = new WeaponUiStats(spec.stat.Damage, spec.stat.Rpm, spec.stat.MagSize, spec.stat.Recoil.PitchDeg)
                });
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
        }

        private static void UpdateRuntimeRegistry()
        {
            EnsureFolder("Assets/_Project/Resources");
            LPWProductionRuntimeRegistry registry = AssetDatabase.LoadAssetAtPath<LPWProductionRuntimeRegistry>(RuntimeRegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<LPWProductionRuntimeRegistry>();
                AssetDatabase.CreateAsset(registry, RuntimeRegistryPath);
            }
            SerializedObject so = new(registry);
            so.FindProperty("weaponAssets").objectReferenceValue = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            so.FindProperty("balance").objectReferenceValue = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            so.FindProperty("manifest").objectReferenceValue = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(registry);
        }

        private static void ValidateSpecAssets(LPWWeaponSpec spec, List<string> errors)
        {
            GameObject fp = AssetDatabase.LoadAssetAtPath<GameObject>(FpPath(spec));
            GameObject tp = AssetDatabase.LoadAssetAtPath<GameObject>(TpPath(spec));
            WeaponDefinition def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(DefinitionPath(spec));
            if (fp == null) errors.Add("FP missing " + spec.itemId);
            if (tp == null) errors.Add("TP missing " + spec.itemId);
            if (def == null) errors.Add("Definition missing " + spec.itemId);
            if (def != null && def.WeaponId != spec.definitionId) errors.Add("Definition ID mismatch " + spec.itemId + " -> " + def.WeaponId);
            if (def != null && (def.FirstPersonViewPrefab == null || def.ThirdPersonViewPrefab == null)) errors.Add("Definition views missing " + spec.itemId);
            if (def != null && !def.FirstPersonAnimations.HasAimClips) errors.Add("Definition AimIn/AimOut missing " + spec.itemId);
            if (fp != null && FindDeep(fp.transform, "LPW_Gun") == null) errors.Add("FP LPW_Gun missing " + spec.itemId);
            if (fp != null)
            {
                WeaponView view = fp.GetComponent<WeaponView>();
                if (view == null || view.SightReference == null || !view.AlignAdsToSightAxis)
                    errors.Add("FP ADS sight-axis contract missing " + spec.itemId);
                Transform gun = FindDeep(fp.transform, "LPW_Gun");
                if (gun == null || Quaternion.Angle(gun.localRotation, Quaternion.Euler(spec.fpRootEuler)) > .1f)
                    errors.Add("FP LPW_Gun rotation missing " + spec.itemId);
                FPWeaponPoseProfile profile = fp.GetComponent<FPWeaponPoseProfile>();
                if (profile == null || !profile.HasRootCalibration || !profile.HasCompleteInterfaceLayout
                    || profile.WeaponRoot != gun || profile.RightHandGrip == null)
                    errors.Add("FP palm/root calibration contract missing " + spec.itemId);
                if (spec.poseCalibrationMode == LPWPoseCalibrationMode.DualLayerVerified
                    && (profile == null || !profile.HasCompleteSightLayout || !profile.HasAdsCalibration
                        || profile.RearSight != view.SightReference))
                    errors.Add("FP dual-layer ADS calibration contract missing " + spec.itemId);
            }
            if (tp != null && (tp.transform.Find("Muzzle") == null || tp.transform.Find("LeftHandTarget") == null)) errors.Add("TP root markers missing " + spec.itemId);
            if (fp != null && fp.GetComponentsInChildren<Collider>(true).Length > 0) errors.Add("FP collider " + spec.itemId);
            if (tp != null && tp.GetComponentsInChildren<Collider>(true).Length > 0) errors.Add("TP collider " + spec.itemId);
        }

        private static void WriteSpec(SerializedProperty row, LPWWeaponSpec spec)
        {
            row.FindPropertyRelative("schemaVersion").intValue = 6;
            SetString(row, "itemId", spec.itemId); SetString(row, "definitionId", spec.definitionId);
            SetString(row, "displayName", spec.displayName); SetString(row, "sourcePrefabPath", spec.sourcePrefabPath);
            SetString(row, "assetKey", spec.assetKey); SetString(row, "firstPersonTemplatePath", spec.firstPersonTemplatePath);
            SetString(row, "animationDefinitionId", spec.animationDefinitionId);
            SetString(row, "thirdPersonTemplatePath", spec.thirdPersonTemplatePath);
            row.FindPropertyRelative("category").enumValueIndex = (int)spec.category;
            row.FindPropertyRelative("slotType").enumValueIndex = (int)spec.slotType;
            row.FindPropertyRelative("fireMode").enumValueIndex = (int)spec.fireMode;
            row.FindPropertyRelative("animationFamily").enumValueIndex = (int)spec.animationFamily;
            row.FindPropertyRelative("usesBoltAction").boolValue = spec.usesBoltAction;
            row.FindPropertyRelative("tier").intValue = spec.tier;
            row.FindPropertyRelative("priceCoins").longValue = spec.priceCoins;
            row.FindPropertyRelative("unlockLevel").intValue = spec.unlockLevel;
            WriteWeaponStat(row.FindPropertyRelative("stat"), spec.stat);
            row.FindPropertyRelative("poseCalibrationMode").enumValueIndex = (int)spec.poseCalibrationMode;
            row.FindPropertyRelative("hasGripCalibration").boolValue = spec.hasGripCalibration;
            row.FindPropertyRelative("fpRootPosition").vector3Value = spec.fpRootPosition;
            row.FindPropertyRelative("fpRootEuler").vector3Value = spec.fpRootEuler;
            row.FindPropertyRelative("fpAdsCenterOffset").vector3Value = spec.fpAdsCenterOffset;
            row.FindPropertyRelative("fpRightHandGripPosition").vector3Value = spec.fpRightHandGripPosition;
            row.FindPropertyRelative("fpRightHandGripEuler").vector3Value = spec.fpRightHandGripEuler;
            row.FindPropertyRelative("fpTriggerPosition").vector3Value = spec.fpTriggerPosition;
            row.FindPropertyRelative("fpTriggerEuler").vector3Value = spec.fpTriggerEuler;
            row.FindPropertyRelative("hasSightCalibration").boolValue = spec.hasSightCalibration;
            row.FindPropertyRelative("fpRearSightPosition").vector3Value = spec.fpRearSightPosition;
            row.FindPropertyRelative("fpRearSightEuler").vector3Value = spec.fpRearSightEuler;
            row.FindPropertyRelative("fpFrontSightPosition").vector3Value = spec.fpFrontSightPosition;
            row.FindPropertyRelative("fpFrontSightEuler").vector3Value = spec.fpFrontSightEuler;
            row.FindPropertyRelative("hasAdsCalibration").boolValue = spec.hasAdsCalibration;
            row.FindPropertyRelative("fpAdsViewmodelPosition").vector3Value = spec.fpAdsViewmodelPosition;
            row.FindPropertyRelative("fpAdsViewmodelEuler").vector3Value = spec.fpAdsViewmodelEuler;
            row.FindPropertyRelative("fpSightReferencePosition").vector3Value = spec.fpSightReferencePosition;
            row.FindPropertyRelative("fpSightReferenceEuler").vector3Value = spec.fpSightReferenceEuler;
            row.FindPropertyRelative("tpRootPosition").vector3Value = spec.tpRootPosition;
            row.FindPropertyRelative("tpRootEuler").vector3Value = spec.tpRootEuler;
            row.FindPropertyRelative("supportsVerifiedAttachments").boolValue = false;
        }

        private static void WriteCatalogRow(SerializedProperty row, WeaponAssetEntry item)
        {
            SetString(row, "itemId", item.itemId); SetString(row, "assetKey", item.assetKey);
            SetString(row, "definitionId", item.definitionId); SetString(row, "previewPrefabPath", item.previewPrefabPath);
            row.FindPropertyRelative("supportsVerifiedAttachments").boolValue = item.supportsVerifiedAttachments;
            row.FindPropertyRelative("category").enumValueIndex = (int)item.category;
            row.FindPropertyRelative("slotType").enumValueIndex = (int)item.slotType;
            row.FindPropertyRelative("definition").objectReferenceValue = item.definition;
            row.FindPropertyRelative("previewPrefab").objectReferenceValue = item.previewPrefab;
            SerializedProperty stats = row.FindPropertyRelative("stats");
            if (item.stats == null) { stats.managedReferenceValue = null; return; }
            stats.FindPropertyRelative("damage").floatValue = item.stats.damage;
            stats.FindPropertyRelative("roundsPerMinute").floatValue = item.stats.roundsPerMinute;
            stats.FindPropertyRelative("magazineSize").floatValue = item.stats.magazineSize;
            stats.FindPropertyRelative("recoil").floatValue = item.stats.recoil;
        }

        private static void WriteBalanceRow(SerializedProperty row, string id, WeaponStat stat)
        {
            row.FindPropertyRelative("WeaponId").stringValue = id;
            WriteWeaponStat(row.FindPropertyRelative("Stat"), stat);
        }

        private static void WriteWeaponStat(SerializedProperty p, WeaponStat s)
        {
            p.FindPropertyRelative("Damage").intValue = s.Damage; p.FindPropertyRelative("Rpm").intValue = s.Rpm;
            p.FindPropertyRelative("MagSize").intValue = s.MagSize; p.FindPropertyRelative("ReserveAmmo").intValue = s.ReserveAmmo;
            p.FindPropertyRelative("ReloadTime").floatValue = s.ReloadTime; p.FindPropertyRelative("Spread").floatValue = s.Spread;
            p.FindPropertyRelative("MaxRange").floatValue = s.MaxRange; p.FindPropertyRelative("AdsFov").floatValue = s.AdsFov;
            WriteStruct(p.FindPropertyRelative("Recoil"), s.Recoil); WriteStruct(p.FindPropertyRelative("Accuracy"), s.Accuracy);
            p.FindPropertyRelative("Ballistic").FindPropertyRelative("PelletCount").intValue = s.Ballistic.PelletCount;
            p.FindPropertyRelative("Ballistic").FindPropertyRelative("PelletSpread").floatValue = s.Ballistic.PelletSpread;
        }

        private static WeaponStat ReadWeaponStat(SerializedProperty p)
        {
            WeaponStat s = new()
            {
                Damage = p.FindPropertyRelative("Damage").intValue, Rpm = p.FindPropertyRelative("Rpm").intValue,
                MagSize = p.FindPropertyRelative("MagSize").intValue, ReserveAmmo = p.FindPropertyRelative("ReserveAmmo").intValue,
                ReloadTime = p.FindPropertyRelative("ReloadTime").floatValue, Spread = p.FindPropertyRelative("Spread").floatValue,
                MaxRange = p.FindPropertyRelative("MaxRange").floatValue, AdsFov = p.FindPropertyRelative("AdsFov").floatValue
            };
            s.Recoil = ReadRecoil(p.FindPropertyRelative("Recoil")); s.Accuracy = ReadAccuracy(p.FindPropertyRelative("Accuracy"));
            s.Ballistic.PelletCount = p.FindPropertyRelative("Ballistic").FindPropertyRelative("PelletCount").intValue;
            s.Ballistic.PelletSpread = p.FindPropertyRelative("Ballistic").FindPropertyRelative("PelletSpread").floatValue;
            return s;
        }

        private static void WriteStruct(SerializedProperty p, object value)
        {
            foreach (var field in value.GetType().GetFields())
            {
                SerializedProperty child = p.FindPropertyRelative(field.Name);
                object v = field.GetValue(value);
                if (v is float f) child.floatValue = f; else if (v is int i) child.intValue = i;
            }
        }

        private static RecoilProfileData ReadRecoil(SerializedProperty p) => new()
        {
            PitchDeg = F(p,"PitchDeg"), YawDeg = F(p,"YawDeg"), FirstShotMultiplier = F(p,"FirstShotMultiplier"),
            Accumulation = F(p,"Accumulation"), MaxAccumulation = F(p,"MaxAccumulation"), RecoveryDelay = F(p,"RecoveryDelay"),
            RecoverySpeed = F(p,"RecoverySpeed"), SpringFrequency = F(p,"SpringFrequency"), SpringDamping = F(p,"SpringDamping"),
            ShakePositionAmplitude = F(p,"ShakePositionAmplitude"), ViewModelKickBack = F(p,"ViewModelKickBack"),
            ViewModelKickPitch = F(p,"ViewModelKickPitch"), AdsRecoilMultiplier = F(p,"AdsRecoilMultiplier")
        };

        private static AccuracyProfileData ReadAccuracy(SerializedProperty p) => new()
        {
            BaseHipSpread = F(p,"BaseHipSpread"), BaseAdsSpread = F(p,"BaseAdsSpread"), MovementSpreadMax = F(p,"MovementSpreadMax"),
            SprintSpreadExtra = F(p,"SprintSpreadExtra"), AirborneSpreadExtra = F(p,"AirborneSpreadExtra"), ShotBloomPerShot = F(p,"ShotBloomPerShot"),
            MaxBloom = F(p,"MaxBloom"), BloomRecoveryDelay = F(p,"BloomRecoveryDelay"), BloomRecoverySpeed = F(p,"BloomRecoverySpeed")
        };

        private static void DisableOriginalWeaponMeshes(GameObject root)
        {
            Transform arms = FindDeep(root.transform, "arms");
            if (arms == null) return;
            foreach (Renderer renderer in arms.GetComponentsInChildren<Renderer>(true))
                if (renderer.transform != arms) renderer.enabled = false;
        }

        private static void AlignEstimatedGrips(Transform wrapper, Vector3 rightGripLocalPosition,
            Vector3 leftGripLocalPosition, Transform rightHand, Transform leftHand)
        {
            if (rightHand == null) return;
            Vector3 rightOffset = rightHand.position - wrapper.TransformPoint(rightGripLocalPosition);
            if (leftHand == null)
            {
                wrapper.position += rightOffset;
                return;
            }
            Vector3 leftOffset = leftHand.position - wrapper.TransformPoint(leftGripLocalPosition);
            // Right hand owns the trigger/pistol grip, while a smaller support-hand share
            // prevents long and compact receivers from visibly floating off the left palm.
            wrapper.position += rightOffset * .75f + leftOffset * .25f;
        }

        private static Vector3 EstimatedGrip(Bounds bounds, LPWWeaponSpec spec)
        {
            // After the common LPW roll correction, +X points toward the stock and
            // -X toward the muzzle.  Grip coordinates are expressed as explicit,
            // category-aware fractions of the corrected gun envelope.  This keeps
            // the semantic palm anchor on the grip/trigger region instead of the
            // old min-Y bounds corner that placed every weapon above the hands.
            float backFraction;
            float heightFraction;
            switch (spec.category)
            {
                case WeaponCatalogCategory.Pistol:
                    backFraction = .20f;
                    heightFraction = .53f;
                    break;
                case WeaponCatalogCategory.Smg:
                    backFraction = .22f;
                    heightFraction = .575f;
                    break;
                case WeaponCatalogCategory.Shotgun:
                    backFraction = .25f;
                    heightFraction = .55f;
                    break;
                case WeaponCatalogCategory.Sniper:
                    backFraction = .30f;
                    heightFraction = .55f;
                    break;
                default:
                    // AssaultRifle2 is the bullpup/AUG layout: its trigger grip is
                    // farther back than the conventional rifle family.
                    backFraction = spec.tier == 2 ? .14f : .25f;
                    heightFraction = spec.tier == 2 ? .68f : .58f;
                    break;
            }
            return new Vector3(
                bounds.max.x - bounds.size.x * backFraction,
                bounds.min.y + bounds.size.y * heightFraction,
                bounds.center.z);
        }

        private static Vector3 EstimatedSupportGrip(Bounds bounds, LPWWeaponSpec spec, Vector3 rightGrip)
        {
            if (spec.category == WeaponCatalogCategory.Pistol)
                return rightGrip + new Vector3(-Mathf.Min(.025f, bounds.size.x * .08f), .005f, 0f);

            float forwardFraction;
            switch (spec.category)
            {
                case WeaponCatalogCategory.Smg: forwardFraction = .36f; break;
                case WeaponCatalogCategory.Shotgun: forwardFraction = .40f; break;
                case WeaponCatalogCategory.Sniper: forwardFraction = .42f; break;
                default: forwardFraction = .38f; break;
            }
            return new Vector3(
                bounds.min.x + bounds.size.x * forwardFraction,
                bounds.min.y + bounds.size.y * .42f,
                bounds.center.z);
        }

        private static Bounds CalculateLocalBounds(Transform reference, GameObject gun)
        {
            Renderer[] renderers = gun.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            bool initialized = false; Bounds local = default;
            foreach (Renderer renderer in renderers)
            {
                Bounds sourceBounds;
                Transform sourceTransform;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    sourceBounds = skinned.localBounds;
                    sourceTransform = skinned.transform;
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    sourceBounds = filter.sharedMesh.bounds;
                    sourceTransform = filter.transform;
                }
                Vector3 min = sourceBounds.min, max = sourceBounds.max;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    Vector3 meshPoint = new(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
                    Vector3 point = reference.InverseTransformPoint(sourceTransform.TransformPoint(meshPoint));
                    if (!initialized) { local = new Bounds(point, Vector3.zero); initialized = true; }
                    else local.Encapsulate(point);
                }
            }
            return initialized ? local : new Bounds(Vector3.zero, Vector3.one);
        }

        private static Transform NewMarker(Transform parent, string name, Vector3 position, Vector3 euler, bool world = false)
        {
            Transform old = parent.Find(name); if (old != null) Object.DestroyImmediate(old.gameObject);
            Transform marker = new GameObject(name).transform; marker.SetParent(parent, false);
            if (world) marker.position = position; else marker.localPosition = position;
            marker.localRotation = Quaternion.Euler(euler); return marker;
        }

        private static void StripColliders(GameObject root)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            root.layer = layer; foreach (Transform child in root.transform) SetLayerRecursive(child.gameObject, layer);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null; if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { Transform found = FindDeep(root.GetChild(i), name); if (found != null) return found; }
            return null;
        }

        private static WeaponDefinition FindDefinition(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponDefinition"))
            {
                WeaponDefinition d = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (d != null && d.WeaponId == id) return d;
            }
            return null;
        }

        private static LPWWeaponManifest LoadSchemaSixManifest()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null) throw new InvalidOperationException("Manifest missing: " + ManifestPath);
            if (manifest.SchemaVersion != 6)
                throw new InvalidOperationException("Manifest must be migrated to schema 6 before generation.");
            if (manifest.Weapons.Count != 29)
                throw new InvalidOperationException("Expected 29 manifest rows, got " + manifest.Weapons.Count);
            return manifest;
        }

        private static AnimationClip FindClip(string assetPath, string clipName)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (asset is AnimationClip clip && clip.name == clipName) return clip;
            throw new InvalidOperationException($"Animation clip missing: {assetPath}::{clipName}");
        }

        private static void SavePrefabToTarget(GameObject contents, string target)
        {
            const string stagingRoot = ArtifactRoot + "/Staging";
            EnsureFolder(stagingRoot);
            string staging = stagingRoot + "/" + Path.GetFileName(target);
            if (AssetDatabase.LoadAssetAtPath<Object>(staging) != null)
                AssetDatabase.DeleteAsset(staging);

            if (PrefabUtility.SaveAsPrefabAsset(contents, staging) == null)
                throw new InvalidOperationException("Staging prefab save failed: " + staging);

            if (AssetDatabase.LoadAssetAtPath<Object>(target) == null)
            {
                if (!AssetDatabase.CopyAsset(staging, target))
                    throw new InvalidOperationException($"Copy failed: {staging} -> {target}");
            }
            else
            {
                // Preserve the target .meta/GUID and overwrite only prefab YAML.  File.Copy
                // does not require delete-sharing, unlike Unity's temp-file rename.
                File.Copy(Path.GetFullPath(staging), Path.GetFullPath(target), true);
            }
            AssetDatabase.DeleteAsset(staging);
        }

        private static void EnsureFolders()
        {
            EnsureFolder(FpRoot); EnsureFolder(TpRoot); EnsureFolder(DefinitionRoot); EnsureFolder(ArtifactRoot);
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string Token(LPWWeaponSpec s) => Path.GetFileNameWithoutExtension(s.sourcePrefabPath);
        private static string FpPath(LPWWeaponSpec s) => FpRoot + "/FP_" + Token(s) + "_View.prefab";
        private static string TpPath(LPWWeaponSpec s) => TpRoot + "/TP_" + Token(s) + ".prefab";
        private static string DefinitionPath(LPWWeaponSpec s) => DefinitionRoot + "/LPW_" + Token(s) + ".asset";
        private static float Round2(float v) => Mathf.Round(v * 100f) / 100f;
        private static float F(SerializedProperty p, string name) => p.FindPropertyRelative(name).floatValue;
        private static void SetString(SerializedProperty p, string name, string value) => p.FindPropertyRelative(name).stringValue = value ?? string.Empty;
        private static void SetObject(SerializedObject so, string name, Object value) => so.FindProperty(name).objectReferenceValue = value;
    }
}
