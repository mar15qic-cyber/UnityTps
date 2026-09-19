using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 配件贴合校准窗口（Docs/21 v2 收尾）：自动甲板检测对个别枪型有残余偏差，
    /// 以人工微调为准 —— 选枪 → 选槽/配件 → Scene 内目视 → 微调按钮 → 保存。
    /// 校准写入 AttachmentCalibration（Resources），枪匠预览 / FP / TP 运行时自动生效。
    /// 偏移语义 = 相对挂点初始位姿的增量（挂点重建不影响已保存校准的语义，但需复核）。
    /// </summary>
    public sealed class AttachmentCalibrationWindow : EditorWindow
    {
        private const string CatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";

        private AttachmentAssetCatalog catalog;
        private AttachmentCalibration calibration;
        private List<WeaponAssetEntry> weapons;
        private int weaponIndex;
        private AttachmentSlotType slot = AttachmentSlotType.Optic;
        private int attachmentIndex;
        private GameObject previewInstance;
        private WeaponAttachmentView previewView;
        private GameObject activeAttachment;
        private float stepMm = 2f;
        private Vector2 scroll;

        [MenuItem("Tools/Attachments/Calibration Window")]
        private static void Open() => GetWindow<AttachmentCalibrationWindow>("配件校准");

        private void OnEnable()
        {
            catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(CatalogPath);
            calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            var catGuid = AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            if (catGuid.Length > 0)
            {
                var wc = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(AssetDatabase.GUIDToAssetPath(catGuid[0]));
                weapons = wc.Entries.Where(e => e != null && !string.IsNullOrEmpty(e.itemId)).ToList();
            }
            if (weapons == null) weapons = new List<WeaponAssetEntry>();
        }

        private void OnDisable() => DestroyPreview();

        private WeaponAssetEntry Weapon => weaponIndex >= 0 && weaponIndex < weapons.Count ? weapons[weaponIndex] : null;

        private void OnGUI()
        {
            if (catalog == null || calibration == null || weapons.Count == 0)
            {
                EditorGUILayout.HelpBox("缺少 AttachmentAssetCatalog / AttachmentCalibration / WeaponAssetCatalog 资产。先运行 Tools/Attachments/Build Attachment Catalog。", MessageType.Error);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);

            // —— 选枪 ——
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("① 选择武器", EditorStyles.boldLabel);
            var names = weapons.Select(w => $"{w.definition?.DisplayName ?? w.itemId}  ({w.itemId})").ToArray();
            var next = EditorGUILayout.Popup(Mathf.Clamp(weaponIndex, 0, names.Length - 1), names);
            if (next != weaponIndex) { weaponIndex = next; DestroyPreview(); }
            var weapon = Weapon;
            if (weapon == null) { EditorGUILayout.EndScrollView(); return; }

            // —— 选槽 ——
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("② 选择槽位（仅显示该枪支持的槽）", EditorStyles.boldLabel);
            var caps = weapon.slotCapabilities ?? System.Array.Empty<string>();
            foreach (var slotType in new[] { AttachmentSlotType.Optic, AttachmentSlotType.Muzzle, AttachmentSlotType.Underbarrel, AttachmentSlotType.Tactical })
            {
                var key = slotType.ToString();
                if (!caps.Contains(key)) continue;
                var active = slot == slotType;
                var bg = active ? new Color(1f, 0.79f, 0.24f, 0.35f) : Color.clear;
                var rect = EditorGUILayout.BeginHorizontal();
                EditorGUI.DrawRect(rect, bg);
                if (GUILayout.Button(key, GUILayout.Height(22)))
                {
                    if (slot != slotType) { slot = slotType; attachmentIndex = 0; DestroyPreview(); }
                }
                EditorGUILayout.EndHorizontal();
            }

            // —— 选配件 ——
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("③ 选择配件（按该槽实际素材）", EditorStyles.boldLabel);
            var options = catalog.FindBySlot(slot).Where(a => a.HasModel).ToList();
            if (options.Count == 0)
            {
                EditorGUILayout.HelpBox("该槽无带模型配件。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            attachmentIndex = Mathf.Clamp(attachmentIndex, 0, options.Count - 1);
            var nextAtt = EditorGUILayout.Popup(attachmentIndex, options.Select(a => a.displayName).ToArray());
            if (nextAtt != attachmentIndex) { attachmentIndex = nextAtt; DestroyPreview(); }
            var attachment = options[attachmentIndex];

            // —— 预览管理 ——
            if (previewInstance == null) SpawnPreview(weapon, attachment);

            // —— 微调 ——
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("④ 微调（mm；Scene 视图同步目视）", EditorStyles.boldLabel);
            stepMm = EditorGUILayout.Slider("步长", stepMm, 0.5f, 20f);
            if (activeAttachment == null) { EditorGUILayout.HelpBox("预览未就绪。", MessageType.Warning); EditorGUILayout.EndScrollView(); return; }

            var p = activeAttachment.transform.localPosition * 1000f;
            EditorGUILayout.LabelField($"当前局部位置  X={p.x:0.0}  Y={p.y:0.0}  Z={p.z:0.0} mm");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("X", GUILayout.Width(20));
            if (GUILayout.Button("−")) Nudge(-stepMm, 0, 0);
            if (GUILayout.Button("+")) Nudge(stepMm, 0, 0);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Y", GUILayout.Width(20));
            if (GUILayout.Button("−")) Nudge(0, -stepMm, 0);
            if (GUILayout.Button("+")) Nudge(0, stepMm, 0);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Z", GUILayout.Width(20));
            if (GUILayout.Button("−")) Nudge(0, 0, -stepMm);
            if (GUILayout.Button("+")) Nudge(0, 0, stepMm);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重置为自动位")) ResetToAuto(weapon, attachment);
            if (GUILayout.Button("保存校准")) SaveCalibration(weapon, attachment);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("应用到该槽全部配件")) SaveToAllInSlot(weapon, slot);
            if (GUILayout.Button("清除该枪该槽校准")) ClearCalibration(weapon, attachment);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "用法：Scene 视图对准预览枪（已自动选中配件）→ 目视判断贴合 → 点微调按钮 → 满意后「保存校准」。\n" +
                "保存后：枪匠页 3D 预览、局内 FP/TP 视图全部自动生效。\n" +
                "「应用到该槽全部配件」把当前增量复制给同枪同槽所有配件（同族配件误差一致时省时间）。", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void SpawnPreview(WeaponAssetEntry weapon, AttachmentAssetEntry attachment)
        {
            DestroyPreview();
            var prefab = weapon.previewPrefab;
            if (prefab == null) return;
            previewInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            previewInstance.transform.position = Vector3.zero;
            previewInstance.transform.rotation = Quaternion.Euler(0f, 270f, 0f);   // 枪口朝 +X（Scene 右侧）
            previewInstance.name = "[CalibPreview] " + prefab.name;
            previewView = previewInstance.AddComponent<WeaponAttachmentView>();
            previewView.ApplyAttachments(catalog, weapon.itemId, new[] { attachment }, laserBeamEnabled: false); // 校准预览：不挂激光束
            activeAttachment = previewView.Spawned.FirstOrDefault();
            if (activeAttachment != null)
            {
                Selection.activeGameObject = activeAttachment;
                var r = activeAttachment.GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    var sv = EditorWindow.GetWindow<SceneView>();
                    sv.Frame(r.bounds, false);
                }
            }
        }

        private void DestroyPreview()
        {
            if (previewInstance != null) DestroyImmediate(previewInstance);
            previewInstance = null;
            previewView = null;
            activeAttachment = null;
        }

        private void Nudge(float dx, float dy, float dz)
        {
            if (activeAttachment == null) return;
            Undo.RecordObject(activeAttachment.transform, "Attachment Calibrate");
            activeAttachment.transform.localPosition += new Vector3(dx, dy, dz) / 1000f;
        }

        private void ResetToAuto(WeaponAssetEntry weapon, AttachmentAssetEntry attachment)
        {
            calibration.Set(weapon.itemId, attachment.itemId, Vector3.zero, Vector3.zero);
            AssetDatabase.SaveAssets();
            SpawnPreview(weapon, attachment);
        }

        private void SaveCalibration(WeaponAssetEntry weapon, AttachmentAssetEntry attachment)
        {
            if (activeAttachment == null) return;
            // 偏移 = 当前局部位置 − 自动位（mountOffset），运行时 ApplyAttachments 会叠加回来
            var offset = activeAttachment.transform.localPosition - attachment.mountOffset;
            calibration.Set(weapon.itemId, attachment.itemId, offset, Vector3.zero);
            AssetDatabase.SaveAssets();
            ShowNotification(new GUIContent($"已保存 {weapon.itemId} × {attachment.itemId}"));
        }

        private void SaveToAllInSlot(WeaponAssetEntry weapon, AttachmentSlotType slotType)
        {
            if (activeAttachment == null) return;
            var current = activeAttachment.transform.localPosition;
            foreach (var a in catalog.FindBySlot(slotType).Where(a => a.HasModel))
            {
                var offset = current - a.mountOffset;
                calibration.Set(weapon.itemId, a.itemId, offset, Vector3.zero);
            }
            AssetDatabase.SaveAssets();
            ShowNotification(new GUIContent("已应用到该槽全部配件"));
        }

        private void ClearCalibration(WeaponAssetEntry weapon, AttachmentAssetEntry attachment)
        {
            calibration.Set(weapon.itemId, attachment.itemId, Vector3.zero, Vector3.zero);
            AssetDatabase.SaveAssets();
            SpawnPreview(weapon, attachment);
        }
    }
}
