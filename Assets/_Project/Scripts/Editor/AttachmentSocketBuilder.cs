using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 配件标准挂点批量装配 v2（Docs/21 Phase F 返工，2026-09-02；2026-09-03 原生 FP 改直测）：
    /// 旧版锚点推导系统性偏差（方案C Muzzle/SightReference 为膛线标定，低于可视枪管/导轨 ~5cm）
    /// → 全部改为网格顶点实测：
    /// —— LPW（29 把，wrapper 局部系 -X=前向）：
    ///    枪口 = 最前段截面中心；导轨顶 = 瞄具锚点 X 邻域最高顶点；护木底 = 前段最低顶点；
    ///    挂点姿态 = 实测枪管轴向 × 枪身上向（导轨顶−膛线）。
    /// —— 原生 TP（16 把，根局部系 +Z=前向 +Y=上）：
    ///    枪口 = 最前段截面中心；导轨顶 = iron_sights 网格最高顶点；护木底 = 前段最低顶点。
    /// —— 原生 FP（16 把，weapon 骨骼系）：烘焙 SMR 直接实测（弃用 TP↔FP 配准：同名网格两边
    ///    比例/相对位置不一致，配准把挂点投歪 ~16cm 致配件系统性脱位）。
    /// 挂点约定（全局统一）：-X = 枪口方向（前向），+Y = 枪械上方向。
    /// </summary>
    public static class AttachmentSocketBuilder
    {
        private static readonly string[] ModelSlots = { "Optic", "Muzzle", "Tactical", "Underbarrel" };

        [MenuItem("Tools/Attachments/Build Sockets (45 Weapons)")]
        public static void BuildAll() => BuildCore(nativeOnly: false);

        [MenuItem("Tools/Attachments/Build Sockets (Native 16 Only)")]
        public static void BuildNativeOnly() => BuildCore(nativeOnly: true);

        private static void BuildCore(bool nativeOnly)
        {
            var report = new StringBuilder();
            int fpDone = 0, tpDone = 0, skipped = 0;

            var capMap = LoadSlotCapabilities();
            var defMap = LoadDefinitions();

            foreach (var kv in capMap)
            {
                var itemId = kv.Key;
                if (nativeOnly && itemId.StartsWith("weapon.lpw.")) continue;
                var slots = kv.Value;
                if (slots.Length == 0) { report.AppendLine($"SKIP {itemId}: 无模型槽位（纯数值）"); skipped++; continue; }
                var defId = itemId.StartsWith("weapon.lpw.") ? itemId.Substring("weapon.".Length) : LegacyDefId(itemId);
                if (!defMap.TryGetValue(defId, out var definition) || definition == null)
                { report.AppendLine($"SKIP {itemId}: 定义未找到 ({defId})"); skipped++; continue; }
                if (definition.FirstPersonViewPrefab == null || definition.ThirdPersonViewPrefab == null)
                { report.AppendLine($"SKIP {itemId}: FP/TP 视图缺失"); skipped++; continue; }

                if (itemId.StartsWith("weapon.lpw."))
                {
                    var poses = DeriveLpwPoses(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab), slots, report, itemId);
                    if (poses == null) { skipped++; continue; }
                    if (WriteSockets(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab), "Armature/weapon/LPW_Gun", poses, itemId, report)) fpDone++;
                    if (WriteSockets(AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab), "LPW_Gun", poses, itemId, report)) tpDone++;
                }
                else
                {
                    var tpPoses = DeriveNativeTpPoses(AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab), slots, report, itemId);
                    if (tpPoses == null) { skipped++; continue; }
                    if (WriteSockets(AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab), null, tpPoses, itemId, report)) tpDone++;
                    // FP：直接烘焙 SMR 在 weapon 骨骼系实测（2026-09-03 弃用 TP↔FP 配准——
                    // 同名网格在 FP/TP 比例与相对位置上不一致，配准把 TP 实测值投歪 ~16cm）
                    var fpPoses = DeriveNativeFpPoses(definition, slots, report, itemId);
                    if (fpPoses != null && WriteSockets(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab), "Armature/weapon", fpPoses, itemId, report)) fpDone++;
                    else skipped++;
                }
            }
            Debug.Log($"[SocketBuilder] FP={fpDone} TP={tpDone} skipped={skipped}\n{report}");
        }

        private static Dictionary<string, WeaponDefinition> LoadDefinitions()
        {
            var defMap = new Dictionary<string, WeaponDefinition>();
            foreach (var defGuid in AssetDatabase.FindAssets("t:WeaponDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(defGuid);
                if (path.Contains("/LPWTest/")) continue;   // spike 枪与生产枪 weaponId 冲突，排除
                var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (def != null && !string.IsNullOrEmpty(def.WeaponId)) defMap[def.WeaponId] = def;
            }
            return defMap;
        }

        private static Dictionary<string, string[]> LoadSlotCapabilities()
        {
            var catGuid = AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            if (catGuid.Length == 0) { Debug.LogError("[SocketBuilder] WeaponAssetCatalog 未找到"); return new Dictionary<string, string[]>(); }
            var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.ScriptableObject>(
                AssetDatabase.GUIDToAssetPath(catGuid[0])));
            var entries = so.FindProperty("entries");
            var capMap = new Dictionary<string, string[]>();
            for (int i = 0; i < entries.arraySize; i++)
            {
                var e = entries.GetArrayElementAtIndex(i);
                var itemId = e.FindPropertyRelative("itemId").stringValue;
                var caps = e.FindPropertyRelative("slotCapabilities");
                var list = new List<string>();
                for (int s = 0; s < caps.arraySize; s++) list.Add(caps.GetArrayElementAtIndex(s).stringValue);
                capMap[itemId] = list.Where(x => ModelSlots.Contains(x)).ToArray();
            }
            return capMap;
        }

        private static string LegacyDefId(string itemId) => itemId switch
        {
            "weapon.m4" => "rifle.day3",
            "weapon.ak" => "rifle.02",
            "weapon.service_pistol" => "pistol.day2",
            "weapon.rifle03" => "rifle.03",
            "weapon.smg01" => "smg.01",
            "weapon.smg02" => "smg.02",
            "weapon.shotgun01" => "shotgun.01",
            "weapon.sniper01" => "sniper.01",
            "weapon.sniper02" => "sniper.02",
            "weapon.handgun02" => "handgun.02",
            _ => itemId
        };

        // ==================== 几何基元 ====================

        private sealed class SocketPose
        {
            public string Slot;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private sealed class MeshSample
        {
            public List<Vector3> Points = new();
        }

        /// <summary>收集 root 下（含子层）全部 MeshFilter 顶点，转换到 root 局部系.</summary>
        private static MeshSample CollectStaticVertices(Transform root)
        {
            var sample = new MeshSample();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var m = mf.transform.localToWorldMatrix;
                var inv = root.worldToLocalMatrix;
                foreach (var v in mesh.vertices)
                    sample.Points.Add(inv.MultiplyPoint3x4(m.MultiplyPoint3x4(v)));
            }
            return sample;
        }

        /// <summary>挂点旋转：-X=forward，+Y=up（forward/up 由网格实测）.</summary>
        private static Quaternion SocketRotation(Vector3 forward, Vector3 up)
        {
            var f = forward.sqrMagnitude < 1e-10f ? Vector3.forward : forward.normalized;
            var u = up - Vector3.Dot(up, f) * f;
            if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(f, Vector3.right);
            return Quaternion.LookRotation(f, u.normalized) * Quaternion.Euler(0f, 90f, 0f);
        }

        /// <summary>
        /// 导轨甲板高度检测（2026-09-02 修复"瞄具落到准星尖"bug）：
        /// 从最高点向下按 2mm 步进扫描高度轴，取第一个满足"顶点数≥10 且沿枪向跨度≥25mm 且横向跨度≥10mm"
        /// 的高度带 —— 准星刀片是孤立窄结构会被跳过，甲板是宽平面会命中。
        /// height=高度轴选择器（恒为局部 Y）；spanAlong=沿枪向轴；spanCross=横向轴。
        /// </summary>
        private static (float deckY, Vector3[] deckPts) DetectRailDeck(
            System.Collections.Generic.IEnumerable<Vector3> pts,
            System.Func<Vector3, float> height,
            System.Func<Vector3, float> spanAlong,
            System.Func<Vector3, float> spanCross,
            bool scanFromTop)
        {
            var list = pts.ToArray();
            if (list.Length == 0) return (0f, list);
            var extreme = scanFromTop ? list.Max(height) : list.Min(height);
            var step = scanFromTop ? -0.002f : 0.002f;
            var lastBin = (float?)null;
            var lastBinPts = System.Array.Empty<Vector3>();
            for (var offset = 0f; Mathf.Abs(offset) <= 0.15f; offset += step)
            {
                var binY = extreme + offset;
                var bin = list.Where(p => Mathf.Abs(height(p) - binY) <= 0.0025f).ToArray();
                if (bin.Length < 10) continue;
                lastBin = binY;
                lastBinPts = bin;
                var alongSpan = bin.Max(spanAlong) - bin.Min(spanAlong);
                var crossSpan = bin.Max(spanCross) - bin.Min(spanCross);
                if (alongSpan >= 0.025f && crossSpan >= 0.010f)
                    return (binY, bin);
            }
            // 兜底：最后一个有效层（扫描方向上离极值最近的密集层）
            if (lastBin.HasValue) return (lastBin.Value, lastBinPts);
            var top = scanFromTop ? list.OrderByDescending(height).First() : list.OrderBy(height).First();
            return (height(top), list.Where(p => Mathf.Abs(height(p) - height(top)) <= 0.003f).ToArray());
        }

        // ==================== LPW 推导（wrapper 局部系：-X 前向） ====================

        private static List<SocketPose> DeriveLpwPoses(string fpPath, string[] slots, StringBuilder report, string itemId)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fpPath);
            if (prefab == null) { report.AppendLine($"ERR {itemId}: FP prefab 无法加载"); return null; }
            var wrapper = prefab.transform.Find("Armature/weapon/LPW_Gun");
            if (wrapper == null) { report.AppendLine($"ERR {itemId}: Armature/weapon/LPW_Gun 未找到"); return null; }
            var muzzleAnchor = wrapper.Find("Muzzle");
            var sightRef = wrapper.Find("SightReference");
            if (muzzleAnchor == null || sightRef == null)
            { report.AppendLine($"ERR {itemId}: 方案C 锚点缺失"); return null; }

            var verts = CollectStaticVertices(wrapper).Points;
            if (verts.Count == 0) { report.AppendLine($"ERR {itemId}: 枪身网格无顶点"); return null; }

            // —— 枪管轴向：wrapper 构造即保证 -X = 枪管方向（方案C 校准约定）；
            //    不得用膛线锚点连线（SightReference 低于枪管 ~5cm，会使前向倾斜 30°+）——
            var forward = Vector3.left;

            // —— 枪口：最前段截面中心（最前 10cm）——
            var minX = verts.Min(v => v.x);
            var tip = verts.Where(v => v.x < minX + 0.10f).ToArray();
            var cy = tip.Average(v => v.y);
            var cz = tip.Average(v => v.z);
            var muzzlePos = new Vector3(minX + 0.012f, cy, cz);

            // —— 导轨顶：瞄具锚点 X 邻域（±12cm/+6cm）+ 宽面甲板检测
            //      （2026-09-02 v5：高度轴恒为局部 Y；准星/照门刀片窄结构被跳过）——
            var sight = sightRef.localPosition;
            var slab = verts.Where(v => v.x >= sight.x - 0.12f && v.x <= sight.x + 0.06f).ToArray();
            if (slab.Length == 0) slab = verts.ToArray();
            var (deckY, deckPts) = DetectRailDeck(slab, p => p.y, p => p.x, p => p.z, true);
            var railPos = new Vector3(deckPts.Average(p => p.x), deckY, deckPts.Average(p => p.z));

            // —— 上向：导轨顶 → 枪管轴线（膛轴 y/z 取枪口锚点；bore ∥ -X）——
            var boreYz = new Vector3(railPos.x, muzzleAnchor.localPosition.y, muzzleAnchor.localPosition.z);
            var up = (railPos - boreYz).normalized;
            var rot = SocketRotation(forward, up);

            // —— 护木底：前段（枪口 → 机匣）最低顶点群 ——
            var foreLen = sight.x - minX;
            var foreStart = minX + Mathf.Min(0.10f, 0.35f * foreLen);
            var foreEnd = sight.x - 0.02f;
            var fore = verts.Where(v => v.x >= foreStart && v.x <= foreEnd).ToArray();
            Vector3 underPos;
            if (fore.Length > 0)
            {
                var botY = fore.Min(v => v.y);
                var botVs = fore.Where(v => v.y < botY + 0.006f).ToArray();
                underPos = new Vector3(botVs.Average(v => v.x), botY + 0.002f, botVs.Average(v => v.z));
            }
            else underPos = muzzlePos + up * -0.03f;   // 枪太短（手枪）：枪口下方

            var poses = new List<SocketPose>();
            foreach (var slot in slots)
            {
                switch (slot)
                {
                    case "Optic":
                        poses.Add(new SocketPose { Slot = slot, Position = railPos, Rotation = rot });
                        break;
                    case "Muzzle":
                        poses.Add(new SocketPose { Slot = slot, Position = muzzlePos, Rotation = rot });
                        break;
                    case "Underbarrel":
                        poses.Add(new SocketPose { Slot = slot, Position = underPos, Rotation = rot });
                        break;
                    case "Tactical":
                        poses.Add(new SocketPose { Slot = slot, Position = underPos + forward * 0.06f, Rotation = rot });
                        break;
                }
            }
            return poses;
        }

        // ==================== 原生 TP 推导（根局部系：+Z 前向 +Y 上） ====================

        private static List<SocketPose> DeriveNativeTpPoses(string tpPath, string[] slots, StringBuilder report, string itemId)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tpPath);
            if (prefab == null) { report.AppendLine($"ERR {itemId}: TP prefab 无法加载"); return null; }
            var root = prefab.transform;

            var verts = CollectStaticVertices(root).Points;
            if (verts.Count == 0) { report.AppendLine($"ERR {itemId}: TP 网格无顶点"); return null; }

            var forward = Vector3.forward;
            var up = Vector3.up;
            var rot = SocketRotation(forward, up);

            // —— 枪口：最前段截面中心（挂点内收 12mm 让消音器包覆枪口）——
            var maxZ = verts.Max(v => v.z);
            var tip = verts.Where(v => v.z > maxZ - 0.08f).ToArray();
            var muzzlePos = new Vector3(tip.Average(v => v.x), tip.Average(v => v.y), maxZ - 0.012f);

            // —— 导轨参考：优先自带 scope（狙击出厂瞄具：镜环底面=导轨面，从底部向上扫），
            //      其次 iron_sights（照门座底=导轨面，从顶部向下扫）。
            //      甲板检测高度轴恒为局部 Y（2026-09-02 v5 修复 v4 轴选择错误）——
            var railRef = root.GetComponentsInChildren<MeshFilter>(true)
                .FirstOrDefault(mf => mf.name.ToLowerInvariant().Contains("scope"))
                ?? root.GetComponentsInChildren<MeshFilter>(true)
                    .FirstOrDefault(mf => mf.name.ToLowerInvariant().Contains("iron"));
            Vector3 railPos;
            if (railRef != null)
            {
                var pts = new List<Vector3>();
                var m = railRef.transform.localToWorldMatrix;
                foreach (var v in railRef.sharedMesh.vertices)
                    pts.Add(root.worldToLocalMatrix.MultiplyPoint3x4(m.MultiplyPoint3x4(v)));
                var fromTop = !railRef.name.ToLowerInvariant().Contains("scope");   // iron 顶向下找照门座底；scope 底向上找镜环底
                var (deckY, deckPts) = DetectRailDeck(pts, p => p.y, p => p.z, p => p.x, fromTop);
                railPos = new Vector3(deckPts.Average(p => p.x), deckY, deckPts.Average(p => p.z));
            }
            else
            {
                var (deckY, deckPts) = DetectRailDeck(verts, p => p.y, p => p.z, p => p.x, true);
                railPos = new Vector3(deckPts.Average(p => p.x), deckY, deckPts.Average(p => p.z));
                report.AppendLine($"WARN {itemId}: 无 iron/scope 网格，导轨用整机甲板检测");
            }

            // —— 护木底：前段（最前 40% 去掉枪口 10%）最低顶点群 ——
            var minZ = verts.Min(v => v.z);
            var fore = verts.Where(v => v.z > maxZ - 0.45f * (maxZ - minZ) && v.z < maxZ - 0.10f).ToArray();
            Vector3 underPos;
            if (fore.Length > 0)
            {
                var botY = fore.Min(v => v.y);
                var botVs = fore.Where(v => v.y < botY + 0.008f).ToArray();
                underPos = new Vector3(botVs.Average(v => v.x), botY + 0.002f, botVs.Average(v => v.z));
            }
            else underPos = muzzlePos + Vector3.down * 0.03f;

            var poses = new List<SocketPose>();
            foreach (var slot in slots)
            {
                switch (slot)
                {
                    case "Optic":
                        poses.Add(new SocketPose { Slot = slot, Position = railPos, Rotation = rot });
                        break;
                    case "Muzzle":
                        poses.Add(new SocketPose { Slot = slot, Position = muzzlePos, Rotation = rot });
                        break;
                    case "Underbarrel":
                        poses.Add(new SocketPose { Slot = slot, Position = underPos, Rotation = rot });
                        break;
                    case "Tactical":
                        poses.Add(new SocketPose { Slot = slot, Position = underPos + Vector3.forward * 0.06f, Rotation = rot });
                        break;
                }
            }
            return poses;
        }

        // ==================== 原生 FP 推导（weapon 骨骼系，直接烘焙实测） ====================

        /// <summary>
        /// 原生 FP 挂点：烘焙 FP 视图 SMR 到 weapon 骨骼系直接实测（2026-09-03 替代 TP↔FP 配准——
        /// 同名网格 FP/TP 比例与相对位置不一致，配准把 TP 实测值投歪 ~16cm，配件系统性脱位）。
        /// 规范轴：prefab 静置姿态枪身恒指世界 +Z、上 +Y（LPFP 16 把骨骼朝向约定一致，已实测验证）；
        /// 骨骼系轴 = bone.InverseTransformDirection(世界轴)，逐枪计算无魔数。
        /// 云划分：枪身+机瞄（枪口/护木底）、机瞄或激活的出厂瞄具（导轨参考，与 TP 同策）；
        /// 排除 手臂/弹壳/刀/消音器/未激活瞄具（LPFP 预置配件网格，静置=藏匿位，会污染实测）。
        /// </summary>
        private static List<SocketPose> DeriveNativeFpPoses(WeaponDefinition definition, string[] slots, StringBuilder report, string itemId)
        {
            var fpInstance = (GameObject)PrefabUtility.InstantiatePrefab(definition.FirstPersonViewPrefab);
            try
            {
                var bone = fpInstance.transform.Find("Armature/weapon");
                if (bone == null) { report.AppendLine($"ERR {itemId}: FP weapon 骨骼缺失"); return null; }

                var fwd = bone.InverseTransformDirection(Vector3.forward).normalized;
                var up = bone.InverseTransformDirection(Vector3.up).normalized;
                var cross = Vector3.Cross(fwd, up).normalized;
                float F(Vector3 p) => Vector3.Dot(p, fwd);
                float U(Vector3 p) => Vector3.Dot(p, up);

                BakeNativeFpClouds(fpInstance, bone,
                    out List<Vector3> gunPts, out List<Vector3> ironPts, out List<Vector3> scopePts);
                var bodyPts = new List<Vector3>(gunPts);
                bodyPts.AddRange(ironPts);
                if (bodyPts.Count == 0) { report.AppendLine($"ERR {itemId}: FP 烘焙无顶点"); return null; }

                var rot = SocketRotation(fwd, up);

                // —— 枪口：最前段截面中心（内收 12mm 包覆）——
                float maxF = bodyPts.Max(F), minF = bodyPts.Min(F);
                var tip = bodyPts.Where(p => F(p) > maxF - 0.08f).ToArray();
                var muzzlePos = Average(tip) - fwd * 0.012f;

                // —— 导轨：激活出厂瞄具（狙击，底部向上扫镜环底）否则机瞄（顶部向下扫照门座底）——
                Vector3 railPos;
                var railRefPts = scopePts.Count > 0 ? scopePts : ironPts;
                if (railRefPts.Count > 0)
                {
                    var (deckY, deckPts) = DetectRailDeck(railRefPts, U, F, p => Vector3.Dot(p, cross), scopePts.Count == 0);
                    var c = Average(deckPts);
                    railPos = c + up * (deckY - U(c));   // 质心投影回甲板高度
                }
                else
                {
                    var (deckY, deckPts) = DetectRailDeck(bodyPts, U, F, p => Vector3.Dot(p, cross), true);
                    var c = Average(deckPts);
                    railPos = c + up * (deckY - U(c));
                    report.AppendLine($"WARN {itemId}: FP 无 iron/scope 网格，导轨用整机甲板检测");
                }

                // —— 护木底：前段（最前 40% 去掉枪口 10%）最低顶点群 ——
                var fore = bodyPts.Where(p => F(p) > maxF - 0.45f * (maxF - minF) && F(p) < maxF - 0.10f).ToArray();
                Vector3 underPos;
                if (fore.Length > 0)
                {
                    float botU = fore.Min(U);
                    var botVs = fore.Where(p => U(p) < botU + 0.008f).ToArray();
                    underPos = Average(botVs) + up * 0.002f;
                }
                else underPos = muzzlePos - up * 0.03f;

                var poses = new List<SocketPose>();
                foreach (var slot in slots)
                {
                    switch (slot)
                    {
                        case "Optic": poses.Add(new SocketPose { Slot = slot, Position = railPos, Rotation = rot }); break;
                        case "Muzzle": poses.Add(new SocketPose { Slot = slot, Position = muzzlePos, Rotation = rot }); break;
                        case "Underbarrel": poses.Add(new SocketPose { Slot = slot, Position = underPos, Rotation = rot }); break;
                        case "Tactical": poses.Add(new SocketPose { Slot = slot, Position = underPos + fwd * 0.06f, Rotation = rot }); break;
                    }
                }
                return poses;
            }
            finally { Object.DestroyImmediate(fpInstance); }
        }

        /// <summary>烘焙 FP 视图 SMR 到 weapon 骨骼系，按名称分三云（枪身/机瞄/激活出厂瞄具）。</summary>
        private static void BakeNativeFpClouds(GameObject fpInstance, Transform bone,
            out List<Vector3> gunPts, out List<Vector3> ironPts, out List<Vector3> scopePts)
        {
            gunPts = new List<Vector3>();
            ironPts = new List<Vector3>();
            scopePts = new List<Vector3>();
            foreach (var smr in fpInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var n = smr.name.ToLowerInvariant();
                if (n.Contains("arm") || n.Contains("bullet") || n.Contains("knife") || n.Contains("silencer")) continue;
                List<Vector3> target;
                if (n.Contains("scope"))
                {
                    if (!smr.gameObject.activeSelf) continue;   // 预置配件瞄具（静置藏匿位）不作导轨参考
                    target = scopePts;
                }
                else if (n.Contains("iron")) target = ironPts;
                else target = gunPts;

                var baked = new Mesh();
                smr.BakeMesh(baked);
                var l2w = smr.transform.localToWorldMatrix;
                foreach (var v in baked.vertices)
                    target.Add(bone.worldToLocalMatrix.MultiplyPoint3x4(l2w.MultiplyPoint3x4(v)));
                Object.DestroyImmediate(baked);
            }
        }

        private static Vector3 Average(Vector3[] pts)
        {
            var c = Vector3.zero;
            foreach (var p in pts) c += p;
            return pts.Length > 0 ? c / pts.Length : c;
        }

        // ==================== 写入 prefab（幂等：先删既有 Attach_* 再建） ====================

        private static bool WriteSockets(string prefabPath, string parentPath, List<SocketPose> poses, string itemId, StringBuilder report)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var root = contents.transform;
                var parent = string.IsNullOrEmpty(parentPath) ? root : root.Find(parentPath);
                if (parent == null) { report.AppendLine($"ERR {itemId}: 挂载父节点未找到 {parentPath} @ {prefabPath}"); return false; }

                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    var child = parent.GetChild(i);
                    if (child.name.StartsWith("Attach_")) Object.DestroyImmediate(child.gameObject);
                }

                foreach (var pose in poses)
                {
                    var go = new GameObject("Attach_" + pose.Slot);
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = pose.Position;
                    go.transform.localRotation = pose.Rotation;
                    var socket = go.AddComponent<AttachmentSocket>();
                    var slotField = new SerializedObject(socket).FindProperty("slot");
                    slotField.enumValueIndex = System.Array.IndexOf(System.Enum.GetValues(typeof(AttachmentSlotType)), ParseSlot(pose.Slot));
                    slotField.serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                return true;
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static AttachmentSlotType ParseSlot(string slot) => slot switch
        {
            "Optic" => AttachmentSlotType.Optic,
            "Muzzle" => AttachmentSlotType.Muzzle,
            "Tactical" => AttachmentSlotType.Tactical,
            "Underbarrel" => AttachmentSlotType.Underbarrel,
            _ => AttachmentSlotType.Magazine
        };
    }
}
