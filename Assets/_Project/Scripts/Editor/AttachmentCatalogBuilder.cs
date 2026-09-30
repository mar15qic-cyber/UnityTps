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
    /// 2026-09-21：正式链只保留 LPFP 四款基础瞄具，LPW Optic 与旧手枪别名已下线。
    /// </summary>
    public static class AttachmentCatalogBuilder
    {
        private const string AttachmentCatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";

        private static readonly Vector3 LpfpMountEuler = new Vector3(0f, -90f, 0f);   // LPFP optical models +Z axis faces downrange.
        private static readonly Vector3 SuppressorMountEuler = new Vector3(0f, 90f, 0f); // Silencer asset outlet is -Z; its threaded +Z end faces the barrel.

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
            public Vector3 apertureCenter;
            public float apertureRadius;
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
            Suppressor(A("attach.lpfp.muffler.01", "经典消音器", "经典制式消音器；适配原生武器", AttachmentSlotType.Muzzle,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Silencer.prefab", SuppressorMountEuler, MuzzleMods())),
            // —— LPFP 正式四款基础瞄具（身份与 Scope_01..04 一一对应）——
            Aperture(ZoomFov(A("attach.lpfp.optic.01", "LPFP 低倍瞄具", "Scope_01；3x 低倍棱镜瞄具", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_01.prefab", OpticAimTier.LowZoom, LpfpMountEuler,
                new[] { M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.03f) }), 18f, 3f),
                new Vector3(0f, 0f, -.043809f), .0136f),
            A("attach.rifle.optic", "LPFP 1x 步枪瞄具", "Scope_02；通行证保留的 1x 光学瞄具", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_02.prefab", OpticAimTier.Holo, LpfpMountEuler,
                new ModSpec[0]),
            Aperture(ZoomFov(A("attach.lpfp.optic.03", "LPFP 低倍瞄具 II", "Scope_03；3x 低倍棱镜瞄具", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_03.prefab", OpticAimTier.LowZoom, LpfpMountEuler,
                new[] { M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 1.03f) }), 18f, 3f),
                new Vector3(0f, .0262827f, .0342f), .0122f),
            A("attach.lpfp.optic.02", "LPFP 1x 全息瞄具", "Scope_04；1x 全息瞄具", AttachmentSlotType.Optic,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_04.prefab", OpticAimTier.Holo, LpfpMountEuler,
                new ModSpec[0]),
            // —— 战术 ——
            A("attach.lpw.tactical.laser", "激光指示器", "下挂激光指示模块；开镜对中提示", AttachmentSlotType.Tactical,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Laser_01.prefab", new Vector3(180f, 0f, 0f),
                new[] { M(WeaponStatId.Spread, ModifierOperation.Multiply, 0.85f) }),
            A("attach.lpw.tactical.light", "战术手电", "下挂照明模块", AttachmentSlotType.Tactical,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Light_01.prefab", new Vector3(180f, 0f, 0f), new ModSpec[0]),
            // —— 下挂 ——
            A("attach.lpw.grip.01", "垂直前握把", "下挂垂直握把；提升操控稳定性", AttachmentSlotType.Underbarrel,
                "Assets/LowPolyWeapons/Prefabs/Attachments/Mod_04.prefab",
                new[]
                {
                    M(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.88f),
                    M(WeaponStatId.HorizontalRecoil, ModifierOperation.Multiply, 0.85f),
                    M(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 0.90f)
                }),
            // —— 通行证通用件（瞄具已并入上方四款正式 Scope 映射）——
            A("attach.rifle.magazine", "步枪加长弹匣", "通行证奖励；弹容量 +8", AttachmentSlotType.Magazine, null,
                new[] { M(WeaponStatId.MagazineSize, ModifierOperation.Add, 8f) }),
            Suppressor(A("attach.pistol.muzzle", "手枪消音器", "通行证奖励；LPFP 原生消音器", AttachmentSlotType.Muzzle,
                "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Silencer.prefab", SuppressorMountEuler, MuzzleMods())),
            A("attach.pistol.magazine", "手枪加长弹匣", "通行证奖励；弹容量 +3", AttachmentSlotType.Magazine, null,
                new[] { M(WeaponStatId.MagazineSize, ModifierOperation.Add, 3f) }),
            A("attach.sniper.magazine", "狙击枪加长弹匣", "狙击枪专用；弹容量 +3", AttachmentSlotType.Magazine, null,
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
        private static AttSpec Aperture(AttSpec spec, Vector3 center, float radius)
        {
            spec.apertureCenter = center;
            spec.apertureRadius = radius;
            return spec;
        }

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
            ("weapon.m4",             new[] { "Optic", "Muzzle", "Magazine" }),                           // AKM：恢复 LPFP 基础瞄具能力
            ("weapon.ak",             new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                // M4A1：原厂垂直握把，不开放下挂握把
            ("weapon.service_pistol", new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                   // 格洛克17
            ("weapon.rifle03",        new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),                // SCAR-L：原厂垂直握把，不开放下挂握把
            ("weapon.smg01",          new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),   // Vector: built-in grip
            ("weapon.smg02",          new[] { "Optic", "Muzzle", "Magazine" }),                              // P90
            ("weapon.shotgun01",      new[] { "Optic", "Muzzle", "Tactical", "Magazine" }),               // M870：恢复 LPFP 基础瞄具能力
            ("weapon.sniper01",       new[] { "Magazine" }),                                                 // AWM：仅内置高倍镜，基础瞄具不提供（2026-09-21 用户拍板）
            ("weapon.sniper02",       new[] { "Magazine" }),                                                 // M82A1：仅内置高倍镜，无消音器
            ("weapon.handgun02",      new[] { "Optic", "Muzzle", "Magazine" }),                           // M1911：恢复 LPFP 基础瞄具能力
            ("weapon.handgun03",      new[] { "Optic", "Muzzle", "Magazine" }),                           // Handgun 03：恢复 LPFP 基础瞄具能力
            ("weapon.handgun04",      new[] { "Optic", "Muzzle", "Magazine" }),                           // Handgun 04：恢复 LPFP 基础瞄具能力
            ("weapon.smg03",          new[] { "Optic", "Muzzle", "Tactical", "Underbarrel", "Magazine" }), // SMG 03
            ("weapon.smg04",          new[] { "Optic", "Muzzle", "Tactical", "Magazine" }), // P90: integral foregrip
            ("weapon.smg05",          new[] { "Optic", "Muzzle", "Tactical", "Magazine" }), // SMG 05
            ("weapon.sniper03",       new[] { "Magazine" }),                                                 // Sniper 03：仅内置高倍镜（2026-09-21 用户拍板）
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
                    scopeApertureCenter = spec.apertureCenter,
                    scopeApertureRadius = spec.apertureRadius,
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
                NativeAttachmentMountRepair.NormalizeOpticSeat(entry);
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
                    return new Vector3(-hi.x + 0.006f, -center.y, -center.z);
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
