using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 配件资产目录装配（Docs/21 Phase F v2 返工）：
    /// 1) 精简目录：12 个消音器副本 → 3 个分族消音器（紧凑=手枪/SMG、重型=步枪/霰弹、经典=原生武器）；
    /// 2) mountOffset 按配件包围盒自动计算（消音器尾端贴枪口/瞄具底面贴导轨/握把顶端贴护木底）；
    /// 3) slotCapabilities 与后端 AttachmentSystemSeeder 家族矩阵同源。
    /// </summary>
    public static class AttachmentCatalogBuilder
    {
        private const string AttachmentCatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";

        private static readonly Vector3 LpfpMountEuler = new Vector3(0f, -90f, 0f);   // LPFP 配件 +Z 长轴 → 挂点 -X 前向

        private sealed class AttSpec
        {
            public string id;
            public string name;
            public string desc;
            public AttachmentSlotType slot;
            public OpticAimTier tier;
            public string prefabPath;   // null = 无固定模型
            public Vector3 mountEuler = default;
            public bool suppressor;     // 消音器：写入 AttachmentAssetEntry.isSuppressor
            public float adsFov;        // 开镜 FOV 覆盖（>1 生效）：写入 AttachmentAssetEntry.adsFovOverride
            public float magnification; // 固定放大率（P4 实体镜 I4b，>1 生效）：写入 AttachmentAssetEntry.magnification
            public ModSpec[] mods;
        }

        private sealed class ModSpec
        {
            public WeaponStatId stat;
            public ModifierOperation op;
            public float value;
        }

        private static ModSpec M(WeaponStatId stat, ModifierOperation op, float value)
            => new ModSpec { stat = stat, op = op, value = value };

        private static ModSpec[] MuzzleMods() => new[]
        {
            M(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.92f),
            M(WeaponStatId.HorizontalRecoil, ModifierOperation.Multiply, 0.92f),
            M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.05f)
        };

        private static readonly AttSpec[] AttachmentData =
        {
            // —— 枪口（3 个分族；2026-09-02 返工：12 个口径副本实为 2 形状×6 拷贝，砍掉重复）——
            Suppressor(A("attach.lpw.muffler.01", "紧凑消音器", "紧凑型消音器；适配手枪与冲锋枪", AttachmentSlotType.Muzzle,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Muffler_01_1.prefab", MuzzleMods())),
            Suppressor(A("attach.lpw.muffler.02", "重型消音器", "全长重型消音器；适配步枪与霰弹枪", AttachmentSlotType.Muzzle,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Muffler_02_1.prefab", MuzzleMods())),
            Suppressor(A("attach.lpfp.muffler.01", "经典消音器", "经典制式消音器；适配原生武器", AttachmentSlotType.Muzzle,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Silencer.prefab", LpfpMountEuler, MuzzleMods())),
            // —— 瞄具（LPW Optic_01..08 + LPFP Scope_01/02；分档=Docs/21 多模态审计）——
            A("attach.lpw.optic.01", "紧凑红点镜", "开放式反射红点；开镜快、无放大", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_01.prefab", OpticAimTier.RedDot,
                new[] { M(WeaponStatId.AdsRecoil, ModifierOperation.Multiply, 0.97f), M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.95f) }),
            A("attach.lpw.optic.02", "全息瞄具 551", "方形窗口全息瞄具；视野开阔", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_02.prefab", OpticAimTier.Holo,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.94f), M(WeaponStatId.AdsRecoil, ModifierOperation.Multiply, 0.97f) }),
            A("attach.lpw.optic.03", "紧凑全息瞄具", "短镜体全息瞄具", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_03.prefab", OpticAimTier.Holo,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.94f), M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 0.98f) }),
            ZoomFov(A("attach.lpw.optic.04", "4 倍战术瞄准镜", "4x 固定倍率棱镜镜；中距离压制", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_04.prefab", OpticAimTier.LowZoom,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.88f), M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.05f) }), 28f, 4f),
            A("attach.lpw.optic.05", "封闭红点镜", "封闭镜体红点；兼顾机瞄高度", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_05.prefab", OpticAimTier.RedDot,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.95f), M(WeaponStatId.AdsRecoil, ModifierOperation.Multiply, 0.97f) }),
            A("attach.lpw.optic.06", "微型红点镜", "微型红点；最轻量化", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_06.prefab", OpticAimTier.RedDot,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.96f) }),
            ZoomFov(A("attach.lpw.optic.07", "高倍狙击镜", "高倍率远距离狙击镜", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_07.prefab", OpticAimTier.HighZoom,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.85f), M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.08f) }), 12f, 8f),
            ZoomFov(A("attach.lpw.optic.08", "远射狙击镜", "高倍率远射瞄准镜", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_08.prefab", OpticAimTier.HighZoom,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.85f), M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.08f) }), 12f, 8f),
            ZoomFov(A("attach.lpfp.optic.01", "3 倍战术瞄镜", "3x 战术棱镜瞄镜", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_01.prefab", OpticAimTier.LowZoom, LpfpMountEuler,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.90f), M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.03f) }), 28f, 3f),
            A("attach.lpfp.optic.02", "全息瞄具 553", "方形窗口全息瞄具（增强版）", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_02.prefab", OpticAimTier.Holo, LpfpMountEuler,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.93f) }),
            // —— 战术 ——
            A("attach.lpw.tactical.laser", "激光指示器", "下挂激光指示模块；开镜对中提示", AttachmentSlotType.Tactical,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Laser_01.prefab",
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.85f) }),
            A("attach.lpw.tactical.light", "战术手电", "下挂照明模块", AttachmentSlotType.Tactical,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Light_01.prefab", new ModSpec[0]),
            // —— 下挂 ——
            A("attach.lpw.grip.01", "垂直前握把", "下挂垂直握把；提升操控稳定性", AttachmentSlotType.Underbarrel,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Mod_04.prefab",
                new[]
                {
                    M(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.88f),
                    M(WeaponStatId.HorizontalRecoil, ModifierOperation.Multiply, 0.85f),
                    M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 0.90f)
                }),
            // —— 通行证通用件（保持原 itemId/数值；使用正式 LPFP/LPW 外观）——
            A("attach.rifle.optic", "步枪光学瞄具", "通行证奖励；通用瞄具", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_02.prefab", OpticAimTier.Holo, LpfpMountEuler,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.95f) }),
            Suppressor(A("attach.rifle.muzzle", "步枪消音器", "通行证奖励；通用消音器", AttachmentSlotType.Muzzle, null, MuzzleMods())),
            A("attach.rifle.magazine", "步枪加长弹匣", "通行证奖励；弹容量 +8", AttachmentSlotType.Magazine, null,
                new[] { M(WeaponStatId.MagazineSize, ModifierOperation.Add, 8f) }),
            A("attach.pistol.optic", "手枪光学瞄具", "通行证奖励；通用瞄具", AttachmentSlotType.Optic,
                "Assets/LowPolyWeapons/Prefabs/Optic/Optic_06.prefab", OpticAimTier.RedDot,
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.95f) }),
            Suppressor(A("attach.pistol.muzzle", "手枪消音器", "通行证奖励；通用消音器", AttachmentSlotType.Muzzle, null, MuzzleMods())),
            A("attach.pistol.magazine", "手枪加长弹匣", "通行证奖励；弹容量 +3", AttachmentSlotType.Magazine, null,
                new[] { M(WeaponStatId.MagazineSize, ModifierOperation.Add, 3f) }),
        };

        private static AttSpec A(string id, string name, string desc, AttachmentSlotType slot, string prefabPath, ModSpec[] mods)
            => new AttSpec { id = id, name = name, desc = desc, slot = slot, tier = OpticAimTier.None, prefabPath = prefabPath, mods = mods };

        private static AttSpec A(string id, string name, string desc, AttachmentSlotType slot, string prefabPath, OpticAimTier tier, ModSpec[] mods)
            => new AttSpec { id = id, name = name, desc = desc, slot = slot, tier = tier, prefabPath = prefabPath, mods = mods };

        private static AttSpec A(string id, string name, string desc, AttachmentSlotType slot, string prefabPath, Vector3 mountEuler, ModSpec[] mods)
            => new AttSpec { id = id, name = name, desc = desc, slot = slot, tier = OpticAimTier.None, prefabPath = prefabPath, mountEuler = mountEuler, mods = mods };

        private static AttSpec A(string id, string name, string desc, AttachmentSlotType slot, string prefabPath, OpticAimTier tier, Vector3 mountEuler, ModSpec[] mods)
            => new AttSpec { id = id, name = name, desc = desc, slot = slot, tier = tier, prefabPath = prefabPath, mountEuler = mountEuler, mods = mods };

        /// <summary>标记消音器条目（写入 AttachmentAssetEntry.isSuppressor，开火音切消音池）。</summary>
        private static AttSpec Suppressor(AttSpec spec)
        {
            spec.suppressor = true;
            return spec;
        }

        /// <summary>设置瞄具开镜 FOV 覆盖与固定放大率（低倍/高倍分档；写入 AttachmentAssetEntry）。
        /// magnification >1 = 实体镜（镜内 RT 独立倍率，PhysicalScopeView 消费）；0 = 既有 overlay 路径。</summary>
        private static AttSpec ZoomFov(AttSpec spec, float adsFov, float magnification = 0f)
        {
            spec.adsFov = adsFov;
            spec.magnification = magnification;
            return spec;
        }

        /// <summary>
        /// 槽位能力表（与后端 AttachmentSystemSeeder 家族矩阵同源；2026-09-02 返工版：
        /// 导轨按模型实测重判——狙击全开放高倍镜、M82A1/狙击族无消音器、MP5 更正）。
        /// </summary>
        private static readonly (string WeaponId, string[] Slots)[] SlotCapabilities =
        {
            // —— 原生 16 把 ——
            ("weapon.m4",             new[] { "Muzzle", "Magazine" }),                                       // AKM：无顶部导轨
            ("weapon.ak",             new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // M4A1
            ("weapon.service_pistol", new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                   // 格洛克17
            ("weapon.rifle03",        new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // SCAR-L
            ("weapon.smg01",          new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // Vector
            ("weapon.smg02",          new[] { "Optic", "Muzzle", "Magazine" }),                              // P90
            ("weapon.shotgun01",      new[] { "Muzzle", "Tactical", "Magazine" }),                           // M870
            ("weapon.sniper01",       new[] { "Optic", "Magazine" }),                                        // AWM：仅高倍镜，无消音器
            ("weapon.sniper02",       new[] { "Optic", "Magazine" }),                                        // M82A1：仅高倍镜，无消音器
            ("weapon.handgun02",      new[] { "Muzzle", "Magazine" }),                                       // M1911
            ("weapon.handgun03",      new[] { "Muzzle", "Magazine" }),                                       // Handgun 03
            ("weapon.handgun04",      new[] { "Muzzle", "Magazine" }),                                       // Handgun 04
            ("weapon.smg03",          new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }), // SMG 03
            ("weapon.smg04",          new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }), // SMG 04
            ("weapon.smg05",          new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }), // SMG 05
            ("weapon.sniper03",       new[] { "Optic", "Magazine" }),                                        // Sniper 03
            // —— LPW 29 把 ——
            ("weapon.lpw.rifle.01",   new[] { "Muzzle", "Magazine" }),                                       // AKM II
            ("weapon.lpw.rifle.02",   new[] { "Optic", "Muzzle", "Magazine" }),                              // AUG
            ("weapon.lpw.rifle.03",   new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // G36
            ("weapon.lpw.rifle.04",   new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // M16A4
            ("weapon.lpw.rifle.05",   new[] { "Muzzle", "Magazine" }),                                       // M16A1
            ("weapon.lpw.rifle.06",   new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // M4A1 II
            ("weapon.lpw.pistol.01",  new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                   // 格洛克 II
            ("weapon.lpw.pistol.02",  new[] { "Muzzle", "Magazine" }),                                       // M1911 II
            ("weapon.lpw.pistol.03",  new[] { "Optic", "Muzzle", "Magazine" }),                              // 沙漠之鹰
            ("weapon.lpw.pistol.04",  new[] { "Muzzle", "Magazine" }),                                       // 格洛克 III
            ("weapon.lpw.pistol.05",  new[] { "Magazine" }),                                                 // 转轮：无枪口螺纹
            ("weapon.lpw.pistol.06",  new[] { "Muzzle", "Magazine" }),                                       // 微型 Uzi
            ("weapon.lpw.shotgun.01", new[] { "Magazine" }),                                                 // 汤姆森
            ("weapon.lpw.shotgun.02", new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                   // M870 战术版
            ("weapon.lpw.shotgun.03", new[] { "Muzzle", "Magazine" }),                                        // M870 猎鹿版
            ("weapon.lpw.shotgun.04", new[] { "Magazine" }),                                                 // 双管
            ("weapon.lpw.shotgun.05", new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                   // AA-12
            ("weapon.lpw.smg.01",     new[] { "Muzzle", "Magazine" }),                                       // MAC-10
            ("weapon.lpw.smg.02",     new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // MP5（2026-09-02 更正，原误判 G36C）
            ("weapon.lpw.smg.03",     new[] { "Optic", "Muzzle", "Magazine" }),                              // P90 双弹匣
            ("weapon.lpw.smg.04",     new[] { "Optic", "Muzzle", "Magazine" }),                              // P90 II
            ("weapon.lpw.smg.05",     new[] { "Optic", "Muzzle", "Magazine" }),                              // KSG
            ("weapon.lpw.smg.06",     new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }),   // UMP45
            ("weapon.lpw.sniper.01",  new[] { "Optic", "Magazine" }),                                        // AWM II
            ("weapon.lpw.sniper.02",  new[] { "Optic", "Magazine" }),                                        // SVD
            ("weapon.lpw.sniper.03",  new[] { "Optic", "Magazine" }),                                        // M700
            ("weapon.lpw.sniper.04",  new[] { "Optic", "Magazine" }),                                        // M95
            ("weapon.lpw.sniper.05",  new[] { "Magazine" }),                                                 // M1887 杠杆
            ("weapon.lpw.sniper.06",  new[] { "Optic", "Magazine" }),                                        // VSS：一体消音，无枪口槽
        };

        [MenuItem("Tools/Attachments/Build Attachment Catalog")]
        public static void BuildAll()
        {
            int attachments = BuildAttachmentCatalog();
            int weapons = BackfillWeaponSlotCapabilities();
            int itemIds = BackfillDefinitionItemIds();
            AssetDatabase.SaveAssets();
            Debug.Log($"[AttachmentCatalogBuilder] 完成：{attachments} 配件条目，{weapons} 把枪槽位能力回填，{itemIds} 定义 itemId 回填。");
        }

        private static int BuildAttachmentCatalog()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources"))
                AssetDatabase.CreateFolder("Assets/_Project", "Resources");
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(AttachmentCatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<AttachmentAssetCatalog>();
                AssetDatabase.CreateAsset(catalog, AttachmentCatalogPath);
            }
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            if (calibration == null)
            {
                calibration = ScriptableObject.CreateInstance<AttachmentCalibration>();
                AssetDatabase.CreateAsset(calibration, CalibrationPath);
            }
            var catalogSo = new SerializedObject(catalog);
            catalogSo.FindProperty("calibration").objectReferenceValue = calibration;
            catalogSo.ApplyModifiedPropertiesWithoutUndo();

            var entries = catalog.EditorEntries;
            entries.Clear();
            foreach (var spec in AttachmentData)
            {
                var prefab = string.IsNullOrEmpty(spec.prefabPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(spec.prefabPath);
                var entry = new AttachmentAssetEntry
                {
                    itemId = spec.id,
                    displayName = spec.name,
                    description = spec.desc,
                    slot = spec.slot,
                    aimTier = spec.tier,
                    isSuppressor = spec.suppressor,
                    adsFovOverride = spec.adsFov,
                    magnification = spec.magnification,
                    prefabPath = spec.prefabPath,
                    prefab = prefab,
                    mountEuler = spec.mountEuler,
                    modifiers = new List<AttachmentModifierEntry>()
                };
                if (spec.mods != null)
                    foreach (var m in spec.mods)
                        entry.modifiers.Add(new AttachmentModifierEntry { stat = m.stat, op = m.op, value = m.value });
                if (prefab != null)
                    entry.mountOffset = ComputeMountOffset(prefab, spec.mountEuler, spec.slot);
                entries.Add(entry);
            }
            EditorUtility.SetDirty(catalog);
            return entries.Count;
        }

        /// <summary>
        /// 按挂点局部系（-X=前向，+Y=上；LPFP 配件先经 mountEuler 旋转）计算贴合偏移：
        /// Muzzle：尾端(+X 端)对齐挂点并前压 12mm 包覆枪口，膛轴(中心 y/z)对齐；
        /// Optic/Tactical：底面(minY)贴挂点，x 居中；
        /// Underbarrel：顶端(maxY)贴挂点，x 居中。
        /// </summary>
        private static Vector3 ComputeMountOffset(GameObject prefab, Vector3 mountEuler, AttachmentSlotType slot)
        {
            var renderer = prefab.GetComponentInChildren<MeshRenderer>();
            if (renderer == null) return Vector3.zero;
            var lb = renderer.localBounds;
            var rot = Quaternion.Euler(mountEuler);
            // 角点旋转到挂点局部系求 AABB
            var lo = Vector3.positiveInfinity; var hi = Vector3.negativeInfinity;
            for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 2; cy++) for (int cz = 0; cz < 2; cz++)
            {
                var c = rot * new Vector3(cx == 0 ? lb.min.x : lb.max.x, cy == 0 ? lb.min.y : lb.max.y, cz == 0 ? lb.min.z : lb.max.z);
                lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c);
            }
            var center = (lo + hi) * 0.5f;
            switch (slot)
            {
                case AttachmentSlotType.Muzzle:
                    return new Vector3(-hi.x - 0.012f, -center.y, -center.z);
                case AttachmentSlotType.Underbarrel:
                    return new Vector3(-center.x, -hi.y, -center.z);
                default:   // Optic / Tactical：底面贴挂点
                    return new Vector3(-center.x, -lo.y, -center.z);
            }
        }

        /// <summary>definitionId → itemId 写入 WeaponDefinition.catalogItemId（战斗侧配件反查用；排除 LPWTest spike）.</summary>
        private static int BackfillDefinitionItemIds()
        {
            var catGuid = AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            if (catGuid.Length == 0) return 0;
            var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.ScriptableObject>(
                AssetDatabase.GUIDToAssetPath(catGuid[0])));
            var entries = so.FindProperty("entries");
            var defMap = new Dictionary<string, string>();
            for (int i = 0; i < entries.arraySize; i++)
            {
                var e = entries.GetArrayElementAtIndex(i);
                defMap[e.FindPropertyRelative("definitionId").stringValue] = e.FindPropertyRelative("itemId").stringValue;
            }
            int updated = 0;
            foreach (var defGuid in AssetDatabase.FindAssets("t:WeaponDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(defGuid);
                if (path.Contains("/LPWTest/")) continue;
                var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (def == null || string.IsNullOrEmpty(def.WeaponId)) continue;
                if (!defMap.TryGetValue(def.WeaponId, out var itemId)) continue;
                var defSo = new SerializedObject(def);
                var prop = defSo.FindProperty("catalogItemId");
                if (prop.stringValue == itemId) continue;
                prop.stringValue = itemId;
                defSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                updated++;
            }
            return updated;
        }

        private static int BackfillWeaponSlotCapabilities()
        {
            var catalogGuid = AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            if (catalogGuid.Length == 0) { Debug.LogWarning("[AttachmentCatalogBuilder] WeaponAssetCatalog 未找到"); return 0; }
            var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.ScriptableObject>(
                AssetDatabase.GUIDToAssetPath(catalogGuid[0])));
            var entries = so.FindProperty("entries");
            var capMap = new Dictionary<string, string[]>();
            foreach (var (weaponId, slots) in SlotCapabilities) capMap[weaponId] = slots;

            int updated = 0;
            for (int i = 0; i < entries.arraySize; i++)
            {
                var e = entries.GetArrayElementAtIndex(i);
                var itemId = e.FindPropertyRelative("itemId").stringValue;
                if (!capMap.TryGetValue(itemId, out var slots)) continue;
                var capsProp = e.FindPropertyRelative("slotCapabilities");
                capsProp.arraySize = slots.Length;
                for (int s = 0; s < slots.Length; s++) capsProp.GetArrayElementAtIndex(s).stringValue = slots[s];
                e.FindPropertyRelative("supportsVerifiedAttachments").boolValue = slots.Length > 0;
                updated++;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(so.targetObject);
            return updated;
        }
    }
}
