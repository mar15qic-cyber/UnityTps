using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Debugging;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 瞄具光轴人工校准窗口（真瞄准镜 L1/L2，Docs/19 F4 落地工具）：
    /// Play Mode 下装备"武器×瞄具"并强制 ADS（FOV 覆盖同步生效，所见即所得），
    /// 屏幕中心品红十字=弹着点权威，人微调眼点直到网格自带准星与十字重合
    /// （窗口与 overlay 同步显示像素误差，调到 ≈0 即"指哪打哪"）。
    /// 数据写入 AttachmentCalibration.OpticAimRows——保存与运行时读取同一字段
    /// （验收动作：保存→退出 Play→重进装备→准星仍贴合）。
    /// 数据分层：组合覆盖（weaponItemId 非空，稀疏）优先；瞄具默认（weaponItemId 空）兜底；
    /// 无记录时 FPWeaponMotion 维持机瞄对位（诚实降级，不影响未校准组合）。
    /// </summary>
    public sealed class OpticAimCalibrationWindow : EditorWindow
    {
        private const string AttachCatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private static readonly float[] Steps = { 0.0001f, 0.001f, 0.01f };
        private static readonly string[] StepNames = { "0.1mm", "1mm", "10mm" };

        private AttachmentAssetCatalog _attachCatalog;
        private WeaponAssetCatalog _weaponCatalog;
        private List<WeaponAssetEntry> _weapons = new();
        private sealed class OpticChoice
        {
            public string itemId;
            public string displayName;
            public OpticAimTier aimTier;
            public bool builtIn;
            public AttachmentAssetEntry attachment;
        }
        private List<OpticChoice> _optics = new();
        private int _weaponIndex;
        private int _opticIndex;
        private int _stepIndex = 1;
        private Vector3 _eyePoint;
        private Vector3 _axisFrontPoint;   // 光轴前点（挂点局部，零=取挂点前向 -X）
        private Vector3 _windowCenter;     // 镜窗中心（挂点局部，零=未标定）
        private float _windowHalfWidth;    // 镜窗半宽（米）
        private float _windowHalfHeight;   // 镜窗半高（米）
        private float _autoRelief = 0.05f;
        private bool _editDefault;      // false=组合覆盖（稀疏）；true=瞄具默认
        private Vector2 _scroll;
        private string _status = "就绪。";
        private bool _forced;
        private double _pendingForceAt = -1;
        private GameObject _overlayGo;
        private GameObject _player;
        private UnityEngine.Camera _fpCamera;

        [MenuItem("Tools/Attachments/Optic Aim Calibration")]
        public static void Open()
            => GetWindow<OpticAimCalibrationWindow>(false, "瞄具光轴校准", true).Show();

        /// <summary>
        /// Phase 5 数据修复：按目录重建 opticAimRows。
        /// 背景：AttachmentCalibration.asset 曾被批量写入 15 行 opticItemId 为空的失效行
        /// （索引器跳过空键 → 全部组合"缺少眼点校准"降级机瞄，眼点光轴对位链路整体失效）。
        /// 本命令：① 清理空键失效行；② 按目录逐瞄具写 tier 缺省眼点（weaponItemId 空 =
        /// tier-1 默认行，全部组合可达）；③ 内置狙击（builtin.weapon.sniper0X）逐枪组合行。
        /// 眼点档位沿用原失效行数值簇（挂点局部系 -X=前向：眼距 0.10~0.16 / 光轴高 0.04~0.055）。
        /// </summary>
        [MenuItem("Tools/Attachments/Rebuild Optic Aim Rows (Catalog)")]
        public static void RebuildOpticAimRowsFromCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(AttachCatalogPath);
            var calibration = catalog != null ? catalog.Calibration : null;
            if (calibration == null)
            {
                Debug.LogError("[OpticAimRebuild] 未找到 AttachmentAssetCatalog 或其 Calibration 引用，中止");
                return;
            }

            int removed = calibration.RemoveInvalidOpticAimRows();
            int written = 0;
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || entry.slot != AttachmentSlotType.Optic || entry.aimTier == OpticAimTier.None)
                    continue;
                calibration.SetOpticEyePoint(string.Empty, entry.itemId, DefaultEyeForTier(entry.aimTier));
                written++;
            }

            // 内置狙击出厂镜组合行（原失效行 weapon.sniper0X 的重建目标）
            string[] sniperWeaponIds = { "weapon.sniper01", "weapon.sniper02", "weapon.sniper03" };
            foreach (var weaponId in sniperWeaponIds)
            {
                calibration.SetOpticEyePoint(weaponId, $"builtin.{weaponId}", DefaultEyeForTier(OpticAimTier.HighZoom));
                written++;
            }

            EditorUtility.SetDirty(calibration);
            AssetDatabase.SaveAssets();
            Debug.Log($"[OpticAimRebuild] 完成：清理失效行 {removed} 条，写入眼点 {written} 条" +
                      $"（目录瞄具默认 + 内置狙击 3 组合）。验收=OpticAimCoverageTests 全绿 + 实机开镜复核。");
        }

        /// <summary>tier 缺省眼点（挂点局部系，-X=前向：x=眼距 / y=光轴高）。</summary>
        private static Vector3 DefaultEyeForTier(OpticAimTier tier)
        {
            switch (tier)
            {
                case OpticAimTier.HighZoom: return new Vector3(0.16f, 0.055f, 0f);
                case OpticAimTier.LowZoom: return new Vector3(0.14f, 0.050f, 0f);
                case OpticAimTier.Holo: return new Vector3(0.12f, 0.045f, 0f);
                case OpticAimTier.RedDot: return new Vector3(0.10f, 0.040f, 0f);
                default: return new Vector3(0.12f, 0.045f, 0f);
            }
        }

        private void OnEnable()
        {
            _attachCatalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(AttachCatalogPath);
            var guids = AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            if (guids.Length > 0)
                _weaponCatalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
            RebuildLists();
            EditorApplication.playModeStateChanged += HandlePlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeChanged;
            ReleaseForce();
        }

        private void RebuildLists()
        {
            _weapons = _weaponCatalog != null
                ? _weaponCatalog.Entries.Where(e => e != null && e.definition != null && e.HasSlot("Optic")).ToList()
                : new List<WeaponAssetEntry>();
            _optics = _attachCatalog != null
                ? _attachCatalog.Entries.Where(e => e != null && e.slot == AttachmentSlotType.Optic
                    && e.HasModel && e.aimTier != OpticAimTier.None)
                    .Select(e => new OpticChoice { itemId = e.itemId, displayName = e.displayName, aimTier = e.aimTier, attachment = e })
                    .ToList()
                : new List<OpticChoice>();
            foreach (var weapon in _weapons)
            {
                var builtIn = weapon.definition.BuiltInOptic;
                if (!builtIn.IsValid || _optics.Any(o => o.itemId == builtIn.opticId)) continue;
                _optics.Add(new OpticChoice
                {
                    itemId = builtIn.opticId,
                    displayName = $"内置瞄具 ({weapon.definition.DisplayName})",
                    aimTier = builtIn.aimTier,
                    builtIn = true
                });
            }
            _weaponIndex = Mathf.Clamp(_weaponIndex, 0, Mathf.Max(0, _weapons.Count - 1));
            _opticIndex = Mathf.Clamp(_opticIndex, 0, Mathf.Max(0, _optics.Count - 1));
        }

        private string CurrentWeaponItemId => _editDefault || _weapons.Count == 0 ? string.Empty : _weapons[_weaponIndex].itemId;
        private OpticChoice CurrentOptic => _optics.Count == 0 ? null : _optics[_opticIndex];
        private string CurrentOpticItemId => CurrentOptic?.itemId;
        private AttachmentCalibration Calibration => _attachCatalog != null ? _attachCatalog.Calibration : null;

        private void OnGUI()
        {
            if (_attachCatalog == null || _weaponCatalog == null || Calibration == null)
            {
                EditorGUILayout.HelpBox("目录缺失：AttachmentAssetCatalog / WeaponAssetCatalog / Calibration。", MessageType.Error);
                if (GUILayout.Button("重新加载")) OnEnable();
                return;
            }

            EditorGUILayout.LabelField("① 选择组合", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _weaponIndex = EditorGUILayout.Popup("武器（Optic 能力）", _weaponIndex,
                _weapons.Select(w => $"{w.definition.DisplayName}  [{w.itemId}]").ToArray());
            _opticIndex = EditorGUILayout.Popup("瞄具（四档/内置）", _opticIndex,
                _optics.Select(o => $"{o.displayName}  [{o.itemId}]  {o.aimTier}").ToArray());
            _editDefault = GUILayout.Toggle(_editDefault,
                _editDefault ? "编辑：该瞄具默认眼点（tier-1，影响全部武器）" : "编辑：当前组合覆盖（tier-2，稀疏）", "Button");
            if (EditorGUI.EndChangeCheck()) LoadEyePointFromRows();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("② 装备并强制 ADS（需 Play Mode）", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("装备并强制 ADS")) EquipAndForceAds();
                if (GUILayout.Button("解除强制")) ReleaseForce();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("③ 微调眼点（挂点局部系：+X=镜后 / +Y=上）", EditorStyles.boldLabel);
            _stepIndex = GUILayout.Toolbar(_stepIndex, StepNames);
            DrawAxisRow("X", ref _eyePoint.x);
            DrawAxisRow("Y", ref _eyePoint.y);
            DrawAxisRow("Z", ref _eyePoint.z);
            using (new EditorGUILayout.HorizontalScope())
            {
                _autoRelief = EditorGUILayout.FloatField("自动眼距", _autoRelief);
                if (GUILayout.Button("按包围盒推导")) AutoGuessEyePoint();
                if (GUILayout.Button("读取记录")) LoadEyePointFromRows();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("③b 光轴前点（可选，零=挂点前向 -X）", EditorStyles.boldLabel);
            DrawAxisRow("X", ref _axisFrontPoint.x);
            DrawAxisRow("Y", ref _axisFrontPoint.y);
            DrawAxisRow("Z", ref _axisFrontPoint.z);
            EditorGUILayout.LabelField("③c 镜窗几何（零=运行时包围盒近似）", EditorStyles.boldLabel);
            DrawAxisRow("X", ref _windowCenter.x);
            DrawAxisRow("Y", ref _windowCenter.y);
            DrawAxisRow("Z", ref _windowCenter.z);
            using (new EditorGUILayout.HorizontalScope())
            {
                float halfW = EditorGUILayout.FloatField("半宽 m", _windowHalfWidth);
                if (!Mathf.Approximately(halfW, _windowHalfWidth)) { _windowHalfWidth = Mathf.Max(0f, halfW); WriteRow(); }
                float halfH = EditorGUILayout.FloatField("半高 m", _windowHalfHeight);
                if (!Mathf.Approximately(halfH, _windowHalfHeight)) { _windowHalfHeight = Mathf.Max(0f, halfH); WriteRow(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("推导镜窗（包围盒近似）")) AutoGuessWindow();
                if (GUILayout.Button("清零光轴/镜窗"))
                {
                    _axisFrontPoint = Vector3.zero;
                    _windowCenter = Vector3.zero;
                    _windowHalfWidth = _windowHalfHeight = 0f;
                    WriteRow();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("④ 保存 / 清除", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存到磁盘")) { AssetDatabase.SaveAssets(); _status = "已保存。验收：退出 Play 重进装备比对。"; }
                if (GUILayout.Button("清除当前键记录"))
                {
                    bool removed = Calibration.RemoveOpticEyePoint(CurrentWeaponItemId, CurrentOpticItemId);
                    _status = removed ? "已清除（回退上级数据/机瞄对位）。" : "该键无记录。";
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(_status, MessageType.None);

            EditorGUILayout.LabelField("组合进度（✓=组合覆盖 ·=仅默认 —=无）", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
            foreach (var w in _weapons)
            {
                EditorGUILayout.LabelField(w.definition.DisplayName);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16);
                    foreach (var o in _optics)
                    {
                        bool combo = Calibration.TryGetOpticEyePoint(w.itemId, o.itemId, out _);
                        bool dflt = Calibration.TryGetOpticEyePoint(string.Empty, o.itemId, out _);
                        string mark = combo ? "✓" : dflt ? "·" : "—";
                        if (GUILayout.Button($"{mark}{ShortName(o.itemId)}", GUILayout.Width(86)))
                        {
                            _weaponIndex = _weapons.IndexOf(w);
                            _opticIndex = _optics.IndexOf(o);
                            _editDefault = false;
                            LoadEyePointFromRows();
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string ShortName(string itemId)
        {
            var parts = (itemId ?? "").Split('.');
            return parts.Length > 0 ? parts[parts.Length - 1] : itemId;
        }

        private void DrawAxisRow(string label, ref float value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(14));
                if (GUILayout.Button("-", GUILayout.Width(24))) { value -= Steps[_stepIndex]; WriteRow(); }
                float next = EditorGUILayout.FloatField(value, GUILayout.MinWidth(90));
                if (!Mathf.Approximately(next, value)) { value = next; WriteRow(); }
                if (GUILayout.Button("+", GUILayout.Width(24))) { value += Steps[_stepIndex]; WriteRow(); }
                GUILayout.Label($"{value * 1000f:F1} mm");
            }
        }

        // ---------------- 装备/强制 ----------------

        private void EquipAndForceAds()
        {
            if (!Application.isPlaying) { _status = "需先进入 Play Mode（建议 Arena_LPWTest）。"; return; }
            if (_weapons.Count == 0 || _optics.Count == 0) { _status = "武器/瞄具列表为空。"; return; }
            _player = FindPlayer();
            if (_player == null) { _status = "场景内未找到 Player。"; return; }
            _player.SetActive(true); // 场景 Player 可能被禁用（LPWAdsRuntimeVerifier 同款兜底）

            var def = _weapons[_weaponIndex].definition;
            var controller = _player.GetComponent<WeaponController>();
            var rig = _player.GetComponentInChildren<FPWeaponRig>(true);
            if (controller == null || rig == null) { _status = "Player 缺 WeaponController/FPWeaponRig。"; return; }

            controller.EquipDefinition(def);
            InvokePrivate(rig, "ShowView", def, false);
            if (!CurrentOptic.builtIn)
                WeaponAttachmentStore.Save(_weapons[_weaponIndex].itemId,
                    new Dictionary<string, string> { { "Optic", CurrentOptic.itemId } });
            var activeView = typeof(FPWeaponRig).GetField("_activeView", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(rig) as GameObject;
            if (!CurrentOptic.builtIn)
                InvokePrivate(rig, "ApplyPersistedAttachments", activeView, def);

            _pendingForceAt = EditorApplication.timeSinceStartup + 0.4; // 等视图/动画器就绪再冻结 aim 姿势
            _status = $"已装备 {def.DisplayName} + {CurrentOptic.displayName}，强制 ADS 排队中…";
            _fpCamera = _player.GetComponentsInChildren<UnityEngine.Camera>(true)
                .FirstOrDefault(c => c.name == "FP View Camera");
        }

        private void FinishForceAds()
        {
            if (_player == null) return;
            var aim = _player.GetComponent<PlayerAimState>();
            if (aim != null) aim.DebugAdsOverride = 1f;
            // 动画器冻结在 aim 保持姿势（LPWAdsRuntimeVerifier 模式）；LPW V2 枪由 LPWGunPoseDriver 读 Ads01
            foreach (var animator in _player.GetComponentsInChildren<FPWeaponAnimator>(true))
            {
                if (!animator.gameObject.activeInHierarchy) continue;
                InvokePrivate(animator, "ExecuteAimIdle");
                animator.enabled = false;
            }
            EnsureOverlay();
            _forced = true;
            _status = "强制 ADS 生效。微调眼点直到像素误差 ≈0（网格准星×绿框×红十字重合）。";
            LoadEyePointFromRows();
        }

        private void ReleaseForce()
        {
            _forced = false;
            _pendingForceAt = -1;
            if (_player != null)
            {
                var aim = _player.GetComponent<PlayerAimState>();
                if (aim != null) aim.DebugAdsOverride = null;
                foreach (var animator in _player.GetComponentsInChildren<FPWeaponAnimator>(true))
                    if (animator != null) animator.enabled = true;
            }
            if (_overlayGo != null) { DestroyImmediate(_overlayGo); _overlayGo = null; }
            OpticAimCalibrationOverlay.EyeScreenPosition = null;
            OpticAimCalibrationOverlay.EyePixelError = -1f;
            OpticAimCalibrationOverlay.StatusText = string.Empty;
        }

        private void HandlePlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode) ReleaseForce();
        }

        // ---------------- 眼点读写 ----------------

        private void LoadEyePointFromRows()
        {
            if (Calibration != null && CurrentOpticItemId != null
                && Calibration.TryGetOpticAim(CurrentWeaponItemId, CurrentOpticItemId, out var data))
            {
                _eyePoint = data.EyePointLocal;
                _axisFrontPoint = data.HasAxisFront ? data.AxisFrontPointLocal : Vector3.zero;
                _windowCenter = data.HasWindow ? data.WindowCenterLocal : Vector3.zero;
                _windowHalfWidth = data.HasWindow ? data.WindowHalfWidthMeters : 0f;
                _windowHalfHeight = data.HasWindow ? data.WindowHalfHeightMeters : 0f;
                _status = $"已读取[{(_editDefault ? "默认" : "组合")}]记录：eye={_eyePoint * 1000f}mm"
                    + (data.HasAxisFront ? " + 轴向" : "") + (data.HasWindow ? " + 镜窗" : "");
            }
            else
            {
                _eyePoint = Vector3.zero;
                _axisFrontPoint = Vector3.zero;
                _windowCenter = Vector3.zero;
                _windowHalfWidth = _windowHalfHeight = 0f;
                _status = "该键无记录：先“按包围盒推导”得初值，再微调。";
            }
        }

        /// <summary>把窗口内全部光轴字段写回当前键（眼点+光轴前点+镜窗；零值=语义缺省）。</summary>
        private void WriteRow()
        {
            if (Calibration == null || CurrentOpticItemId == null) return;
            Calibration.SetOpticAim(CurrentWeaponItemId, CurrentOpticItemId, new OpticAimData
            {
                EyePointLocal = _eyePoint,
                AxisFrontPointLocal = _axisFrontPoint,
                WindowCenterLocal = _windowCenter,
                WindowHalfWidthMeters = _windowHalfWidth,
                WindowHalfHeightMeters = _windowHalfHeight,
                TargetViewportHeight = Calibration.TryGetOpticAim(CurrentWeaponItemId, CurrentOpticItemId, out var existing)
                    ? existing.TargetViewportHeight : 0f,
                HasAxisFront = _axisFrontPoint.sqrMagnitude > 1e-10f,
                HasWindow = _windowCenter.sqrMagnitude > 1e-10f && _windowHalfWidth > 0f && _windowHalfHeight > 0f
            });
        }

        /// <summary>镜窗初值推导：配件网格 OBB 折回挂点局部系（OpticAimGeometry 同源实现，
        /// 与运行时未标定兜底一致），写入后可继续微调为正式值。</summary>
        private void AutoGuessWindow()
        {
            var socket = FindActiveOpticSocket();
            if (socket == null) { _status = "未找到激活视图的 Optic 挂点（先装备并强制 ADS）。"; return; }
            if (!Game.Presentation.Camera.OpticAimGeometry.TryApproximateWindowFromRenderers(
                    socket, out var center, out var halfW, out var halfH))
            {
                _status = "挂点下未找到配件网格（先装备瞄具）。";
                return;
            }
            _windowCenter = center;
            _windowHalfWidth = halfW;
            _windowHalfHeight = halfH;
            WriteRow();
            _status = $"镜窗近似：center={_windowCenter * 1000f:F0}mm half={halfW * 1000f:F0}×{halfH * 1000f:F0}mm（近似值，请实机微调）。";
        }

        private void AutoGuessEyePoint()
        {
            var socket = FindActiveOpticSocket();
            if (socket == null) { _status = "未找到激活视图的 Optic 挂点（先装备并强制 ADS）。"; return; }
            Transform spawned = null;
            foreach (Transform child in socket)
                if (child.name.StartsWith("Att_")) { spawned = child; break; }
            if (spawned == null) { _status = "挂点下无配件实例。"; return; }
            var renderers = spawned.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) { _status = "配件无 Renderer。"; return; }

            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var r in renderers)
            {
                var b = r.bounds;
                for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 2; cy++) for (int cz = 0; cz < 2; cz++)
                {
                    var corner = new Vector3(cx == 0 ? b.min.x : b.max.x, cy == 0 ? b.min.y : b.max.y, cz == 0 ? b.min.z : b.max.z);
                    var local = socket.InverseTransformPoint(corner);
                    lo = Vector3.Min(lo, local);
                    hi = Vector3.Max(hi, local);
                }
            }
            // 挂点局部系 -X=前向：镜后=+X 端面中心，再加眼距
            _eyePoint = new Vector3(hi.x + _autoRelief, (lo.y + hi.y) * 0.5f, (lo.z + hi.z) * 0.5f);
            WriteRow();
            _status = $"包围盒推导：{_eyePoint * 1000f} mm（微调至像素误差 ≈0）。";
        }

        // ---------------- 实时误差反馈 ----------------

        private void Update()
        {
            if (_pendingForceAt > 0 && EditorApplication.timeSinceStartup >= _pendingForceAt)
            {
                _pendingForceAt = -1;
                FinishForceAds();
            }
            if (!_forced || !Application.isPlaying) return;
            var socket = FindActiveOpticSocket();
            if (socket == null || _fpCamera == null) return;
            var eyeWorld = socket.TransformPoint(_eyePoint);
            var sp = _fpCamera.WorldToScreenPoint(eyeWorld);
            var gui = new Vector2(sp.x, Screen.height - sp.y);
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            bool eyeInFront = sp.z > 0f;
            if (eyeInFront)
                OpticAimCalibrationOverlay.EyeScreenPosition = gui;
            else
                OpticAimCalibrationOverlay.EyeScreenPosition = null;
            OpticAimCalibrationOverlay.EyePixelError = eyeInFront ? Vector2.Distance(gui, center) : -1f;

            // S1 构图/轴向指标（ADS 审计 A1 验收所需的实机读数）
            bool hasAxis = _axisFrontPoint.sqrMagnitude > 1e-10f;
            Vector3 axisLocal = hasAxis ? (_axisFrontPoint - _eyePoint).normalized : new Vector3(-1f, 0f, 0f);
            Vector3 axisWorld = socket.TransformDirection(axisLocal).normalized;
            float eyeDistance = _eyePoint.magnitude;
            float axisErrorDeg = -1f;
            float windowFraction = -1f;
            bool hasWindow = _windowCenter.sqrMagnitude > 1e-10f && _windowHalfWidth > 0f && _windowHalfHeight > 0f;
            if (hasWindow)
            {
                var windowWorld = socket.TransformPoint(_windowCenter);
                eyeDistance = Vector3.Distance(eyeWorld, windowWorld);
                Vector3 toWindow = windowWorld - eyeWorld;
                if (toWindow.sqrMagnitude > 1e-8f)
                    axisErrorDeg = Vector3.Angle(axisWorld, toWindow.normalized);
                float tanHalfFov = Mathf.Tan(_fpCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                if (eyeDistance > 1e-4f && tanHalfFov > 1e-4f)
                    windowFraction = (_windowHalfHeight / eyeDistance) / tanHalfFov;
            }
            OpticAimCalibrationOverlay.StatusText =
                $"[{(_editDefault ? "默认" : "组合")}] {CurrentWeaponItemId}×{CurrentOpticItemId}  eye={_eyePoint * 1000f}mm  "
                + $"眼距={eyeDistance * 1000f:F0}mm  轴向{(hasAxis ? "前点" : "默认-X")}"
                + (hasWindow ? $"  轴向误差={axisErrorDeg:F2}°  镜窗高占比={windowFraction * 100f:F1}%"
                    : "  镜窗未标定（运行时包围盒近似）");
            Repaint();
        }

        // ---------------- 查找 ----------------

        private static GameObject FindPlayer()
            => Resources.FindObjectsOfTypeAll<GameObject>()
                .FirstOrDefault(x => x != null && x.name == "Player" && x.scene.IsValid());

        private Transform FindActiveOpticSocket()
        {
            if (_player == null) return null;
            foreach (var view in _player.GetComponentsInChildren<WeaponView>(false))
            {
                if (view == null || !view.isActiveAndEnabled) continue;
                var attachments = view.GetComponent<WeaponAttachmentView>();
                var socket = attachments != null ? attachments.GetSocketTransform(AttachmentSlotType.Optic) : null;
                if (socket != null) return socket;
            }
            return null;
        }

        private void EnsureOverlay()
        {
            if (_overlayGo != null) return;
            _overlayGo = new GameObject("OpticAimCalibrationOverlay");
            _overlayGo.AddComponent<OpticAimCalibrationOverlay>();
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Debug.LogWarning($"[OpticAimCalibrationWindow] {target.GetType().Name}.{method} 未找到");
            else m.Invoke(target, args);
        }
    }
}
