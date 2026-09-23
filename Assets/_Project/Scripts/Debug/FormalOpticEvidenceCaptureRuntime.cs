using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Debugging
{
    /// <summary>
    /// Repeatable C2-B evidence capture. It runs inside the real Arena player,
    /// changes only the equipped definition/optic and DebugAdsOverride, and
    /// lets the production FPWeaponRig, WeaponAttachmentView and FPWeaponMotion
    /// render the evidence. OpticAimGeometry is also used for the measurements
    /// written into the manifest and drawn in the GameView overlay.
    /// </summary>
    public sealed class FormalOpticEvidenceCaptureRuntime : MonoBehaviour
    {
        private const string OutputRelative = "Docs/交接/2026-09-21-全枪械基础瞄具-证据";
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";
        private const string CatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string ViewCameraName = "FP View Camera";
        private const float TransitionAds = 0.5f;

        // Sniper family excluded: builtin high-zoom only, no base optics (user decision 2026-09-21).
        private static readonly string[] WeaponIds =
        {
            "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.service_pistol",
            "weapon.handgun02", "weapon.handgun03", "weapon.handgun04", "weapon.smg01",
            "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05",
            "weapon.shotgun01"
        };

        private static readonly string[] RepresentativeWeaponIds =
        {
            "weapon.rifle03", "weapon.m4", "weapon.service_pistol", "weapon.smg01"
        };

        private static readonly string[] OpticIds =
        {
            "attach.lpfp.optic.01", "attach.rifle.optic",
            "attach.lpfp.optic.03", "attach.lpfp.optic.02"
        };

        private static readonly string[] States = { "hipfire", "transition50", "fullads" };
        private readonly List<CaptureRecord> _records = new();
        private readonly Dictionary<string, Vector3> _mountBaseline = new(StringComparer.Ordinal);
        private string _overlayText = "C2-B evidence capture: starting";
        private bool _running;
        private bool _completed;
        private bool _poseStable;
        [SerializeField] private bool representativeOnly = true;

        public bool RepresentativeOnly
        {
            get => representativeOnly;
            set => representativeOnly = value;
        }

        public void SetRepresentativeOnly(bool value) => representativeOnly = value;

        private sealed class CaptureRecord
        {
            public string weapon;
            public string optic;
            public string state;
            public float adsBlend;
            public string actualWeaponId;
            public string activeViewName;
            public string cameraName;
            public string playerRootName;
            public int playerRootInstanceId;
            public string rigName;
            public int rigInstanceId;
            public string motionName;
            public int motionInstanceId;
            public int activeFpWeaponRigCount;
            public int activeWeaponViewCount;
            public bool ownerChainValid;
            public bool activeViewMatches;
            public bool poseStable;
            public string file;
            public bool fileExists;
            public bool socketFound;
            public bool spawnedOpticFound;
            public bool mountContactMeasured;
            public float opticBoundsMinY;
            public float opticBoundsMaxY;
            public float mountBaseGap;
            public bool mountContactWithinTolerance;
            public bool railMeasured;
            public float railTopLocalY;
            public float railGap;
            public bool railGapWithinTolerance;
            public float centerX;
            public float centerY;
            public float projectedHeight;
            public float targetHeight;
            public float mountDelta;
            public bool measurementFinite;
            public string status;
            public string reason;
            public string captureCaption;
        }

        public void BeginCapture()
        {
            if (_running || _completed) return;
            _running = true;
            StartCoroutine(CaptureRoutine());
        }

        private IEnumerator CaptureRoutine()
        {
            var captureWeapons = representativeOnly ? RepresentativeWeaponIds : WeaponIds;
            var outputRelative = representativeOnly
                ? Path.Combine(OutputRelative, "representative")
                : OutputRelative;
            var output = ProjectPath(outputRelative);
            Directory.CreateDirectory(output);
            PrepareEvidenceDirectory(output);
            var playerAim = GetComponentInParent<PlayerAimState>();
            var controller = GetComponentInParent<WeaponController>();
            var ownerRoot = controller != null ? controller.transform.root : transform.root;
            var ownerRigs = ownerRoot != null
                ? ownerRoot.GetComponentsInChildren<FPWeaponRig>(true)
                : Array.Empty<FPWeaponRig>();
            var activeOwnerRigs = FilterActive(ownerRigs);
            var rig = activeOwnerRigs.Length == 1
                ? activeOwnerRigs[0]
                : (ownerRigs.Length == 1 ? ownerRigs[0] : null);
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            if (playerAim == null || controller == null || ownerRoot == null || rig == null || catalog == null)
            {
                WriteFailureManifest(output, captureWeapons,
                    "owner PlayerAimState/WeaponController/unique FPWeaponRig/catalog missing");
                _running = false;
                _completed = true;
                yield break;
            }

#if UNITY_EDITOR
            var definitions = LoadFormalDefinitions();
#else
            var definitions = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);
#endif
            var ownerMotions = ownerRoot.GetComponentsInChildren<FPWeaponMotion>(true);
            var motion = ownerMotions.Length == 1 ? ownerMotions[0] : null;
            var activeOwnerViews = CountActiveWeaponViews(ownerRoot);
            if (motion == null || activeOwnerRigs.Length != 1 || activeOwnerViews != 1
                || motion.transform.root != ownerRoot || rig.transform.root != ownerRoot)
            {
                WriteFailureManifest(output, captureWeapons,
                    "owner hierarchy is ambiguous: require one active FPWeaponRig, one active WeaponView, and one FPWeaponMotion");
                _running = false;
                _completed = true;
                yield break;
            }

            foreach (var weaponId in captureWeapons)
            {
                if (!definitions.TryGetValue(weaponId, out var definition) || definition == null)
                {
                    AddWeaponFailures(weaponId, output, "formal WeaponDefinition missing");
                    continue;
                }

                controller.EquipDefinition(definition);
                GameObject view = null;
                var viewReady = false;
                for (var frame = 0; frame < 180; frame++)
                {
                    view = rig.ActiveView;
                    viewReady = controller.Definition == definition
                        && view != null
                        && definition.FirstPersonViewPrefab != null
                        && view.name == definition.FirstPersonViewPrefab.name;
                    if (viewReady) break;
                    yield return null;
                }
                if (!viewReady || view == null)
                {
                    AddWeaponFailures(weaponId, output, "target definition/view did not converge within 180 frames");
                    continue;
                }
                // Switch/draw has to settle before any measurement or attachment work.
                for (var settle = 0; settle < 24; settle++) yield return null;
                var attachmentView = view.GetComponent<WeaponAttachmentView>();
                if (attachmentView == null) attachmentView = view.AddComponent<WeaponAttachmentView>();

                foreach (var opticId in OpticIds)
                {
                    var entry = catalog.Find(opticId);
                    if (entry == null || entry.prefab == null)
                    {
                        AddCombinationFailures(weaponId, opticId, output, "formal optic prefab missing");
                        continue;
                    }

                    attachmentView.Clear();
                    attachmentView.ApplyAttachments(catalog, weaponId, new[] { entry }, laserBeamEnabled: false);
                    controller.SetAttachments(new[] { entry });
                    for (var settle = 0; settle < 12; settle++) yield return null;

                    _mountBaseline.Remove(Key(weaponId, opticId));
                    foreach (var state in States)
                    {
                        var ads = state == "hipfire" ? 0f : state == "transition50" ? TransitionAds : 1f;
                        playerAim.DebugAdsOverride = ads;
                        yield return StartCoroutine(WaitForStablePose(motion, view, attachmentView, controller));
                        yield return new WaitForEndOfFrame();
                        var record = Measure(weaponId, opticId, state, ads, view, attachmentView,
                            entry, controller, motion, definition, ownerRoot, rig,
                            activeOwnerRigs.Length, CountActiveWeaponViews(ownerRoot));
                        var fileName = Safe(weaponId) + "__" + Safe(opticId) + "__" + state + ".png";
                        var absoluteFile = Path.Combine(output, fileName);
                        record.file = fileName;
                        // Measure() installs the caption for this exact record. The
                        // next rendered frame is read synchronously, so the PNG cannot
                        // be a previous state's delayed ScreenCapture result.
                        yield return null;
                        yield return new WaitForEndOfFrame();
                        if (File.Exists(absoluteFile)) File.Delete(absoluteFile);
                        CaptureCurrentFrame(absoluteFile);
                        record.fileExists = File.Exists(absoluteFile) && new FileInfo(absoluteFile).Length > 0;
                        if (!record.fileExists)
                        {
                            record.status = "FAIL";
                            record.reason = AppendReason(record.reason, "GameView PNG missing");
                        }
                        _records.Add(record);
                    }
                }
            }

            playerAim.DebugAdsOverride = null;
            BuildContactSheets(output, captureWeapons);
            WriteManifest(output, outputRelative);
            WriteAuditDocument(output);
            Debug.Log("[FormalOpticEvidence] captured " + _records.Count + " records to " + output);
            _running = false;
            _completed = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        private CaptureRecord Measure(string weaponId, string opticId, string state, float ads,
            GameObject view, WeaponAttachmentView attachments, AttachmentAssetEntry entry,
            WeaponController controller, FPWeaponMotion motion, WeaponDefinition definition,
            Transform ownerRoot, FPWeaponRig rig, int activeRigCount, int activeViewCount)
        {
            var record = new CaptureRecord
            {
                weapon = weaponId,
                optic = opticId,
                state = state,
                adsBlend = ads,
                actualWeaponId = controller.Definition != null ? controller.Definition.CatalogItemId : "",
                activeViewName = view != null ? view.name : "",
                playerRootName = ownerRoot != null ? ownerRoot.name : "",
                playerRootInstanceId = ownerRoot != null ? ownerRoot.GetInstanceID() : 0,
                rigName = rig != null ? rig.name : "",
                rigInstanceId = rig != null ? rig.GetInstanceID() : 0,
                motionName = motion != null ? motion.name : "",
                motionInstanceId = motion != null ? motion.GetInstanceID() : 0,
                activeFpWeaponRigCount = activeRigCount,
                activeWeaponViewCount = activeViewCount,
                ownerChainValid = ownerRoot != null && rig != null && motion != null && view != null
                    && rig.transform.root == ownerRoot
                    && motion.transform.root == ownerRoot
                    && view.transform.root == ownerRoot,
                activeViewMatches = controller.Definition == definition
                    && view != null && definition != null
                    && definition.FirstPersonViewPrefab != null
                    && view.name == definition.FirstPersonViewPrefab.name,
                poseStable = _poseStable,
                status = "PASS",
                reason = ""
            };
            if (!record.activeViewMatches)
                record.reason = AppendReason(record.reason, "actual weapon/view does not match requested definition");
            if (!record.ownerChainValid)
                record.reason = AppendReason(record.reason, "player/rig/motion/view hierarchy mismatch");
            if (record.activeFpWeaponRigCount != 1)
                record.reason = AppendReason(record.reason, "active FPWeaponRig count is " + record.activeFpWeaponRigCount);
            if (record.activeWeaponViewCount != 1)
                record.reason = AppendReason(record.reason, "active WeaponView count is " + record.activeWeaponViewCount);
            if (!_poseStable)
                record.reason = AppendReason(record.reason, "FP pose/optic window did not converge within 180 frames");
            var spawned = attachments.FindSpawned(opticId);
            var socket = attachments.GetSocketTransform(AttachmentSlotType.Optic);
            record.socketFound = socket != null;
            record.spawnedOpticFound = spawned != null;
            if (!record.socketFound) record.reason = AppendReason(record.reason, "Attach_Optic missing");
            if (!record.spawnedOpticFound) record.reason = AppendReason(record.reason, "spawned formal optic missing");

            if (record.spawnedOpticFound)
            {
                var key = Key(weaponId, opticId);
                if (!_mountBaseline.TryGetValue(key, out var baseline))
                {
                    baseline = spawned.localPosition;
                    _mountBaseline[key] = baseline;
                }
                record.mountDelta = Vector3.Distance(baseline, spawned.localPosition);
                if (record.socketFound && TryGetRendererBoundsInReference(spawned.gameObject, socket, out var bounds))
                {
                    record.mountContactMeasured = true;
                    record.opticBoundsMinY = bounds.min.y;
                    record.opticBoundsMaxY = bounds.max.y;
                    // Attach_Optic origin is the rail deck datum produced by the
                    // socket builder. The optic base must be within a few mm of it;
                    // this is an independent HipFire gate and never changes ADS.
                    record.mountBaseGap = bounds.min.y;
                    record.mountContactWithinTolerance = Mathf.Abs(record.mountBaseGap) <= 0.004f;
                    if (!record.mountContactWithinTolerance)
                        record.reason = AppendReason(record.reason,
                            "optic base gap=" + record.mountBaseGap.ToString("F4") + "m exceeds 4mm");
                    if (TryMeasureRailGap(view, socket, spawned.gameObject,
                        record.mountBaseGap, out var railTopLocalY, out var railGap))
                    {
                        record.railMeasured = true;
                        record.railTopLocalY = railTopLocalY;
                        record.railGap = railGap;
                        record.railGapWithinTolerance = Mathf.Abs(railGap) <= 0.006f;
                        if (!record.railGapWithinTolerance)
                            record.reason = AppendReason(record.reason,
                                "optic-to-rail gap=" + railGap.ToString("F4") + "m exceeds 6mm");
                    }
                    else
                        record.reason = AppendReason(record.reason, "runtime rail mesh unavailable near Attach_Optic");
                }
                else
                    record.reason = AppendReason(record.reason, "optic renderer bounds unavailable for mount contact");
            }

            var weaponView = view != null ? view.GetComponent<WeaponView>() : null;
            if (weaponView != null && socket != null
                && OpticAimGeometry.TryResolveLocal(controller, weaponView, out var local))
            {
                var frame = OpticAimGeometry.Evaluate(local, socket);
                var camera = ResolveProductionViewCamera(motion);
                record.cameraName = camera != null ? camera.name : "";
                if (camera != null && frame.HasWindow)
                {
                    var projection = CameraProjection.From(camera);
                    var center = projection.ProjectToViewport(frame.WindowCenterWorld);
                    var top = projection.ProjectToViewport(frame.WindowCenterWorld
                        + frame.UpWorld * frame.WindowHalfHeightMeters);
                    var bottom = projection.ProjectToViewport(frame.WindowCenterWorld
                        - frame.UpWorld * frame.WindowHalfHeightMeters);
                    record.centerX = center.x;
                    record.centerY = center.y;
                    record.projectedHeight = top.y - bottom.y;
                    var calibration = AttachmentAssetCatalog.LoadOrDefault().Calibration;
                    if (calibration != null && controller.Definition != null
                        && calibration.TryGetOpticAim(controller.Definition.CatalogItemId, opticId, out var aimData))
                        record.targetHeight = aimData.TargetViewportHeight;
                    record.measurementFinite = IsFinite(center) && IsFinite(record.projectedHeight);
                    if (!record.measurementFinite && state != "hipfire")
                        record.reason = AppendReason(record.reason, "OpticAimGeometry measurement non-finite");
                    if (state == "transition50")
                    {
                        // Transition is intentionally not a centered ADS frame. It
                        // only needs to remain finite and inside a broad viewport
                        // envelope while the weapon travels toward the eye.
                        if (record.centerX < -0.5f || record.centerX > 1.5f
                            || record.centerY < -0.5f || record.centerY > 1.5f
                            || record.projectedHeight <= 0f || record.projectedHeight > 1f)
                            record.reason = AppendReason(record.reason, "ADS transition pose outside finite viewport envelope");
                    }
                    else if (state == "fullads")
                    {
                        if (Mathf.Abs(record.centerX - 0.5f) > 0.01f
                            || Mathf.Abs(record.centerY - 0.5f) > 0.01f)
                            record.reason = AppendReason(record.reason, "full ADS window center exceeds 1% tolerance");
                        var heightTolerance = 0.02f;
                        if (record.targetHeight <= 0f
                            || Mathf.Abs(record.projectedHeight - record.targetHeight) > heightTolerance)
                            record.reason = AppendReason(record.reason, "full ADS window height outside target range");
                    }
                }
                else if (state != "hipfire")
                    record.reason = AppendReason(record.reason, "FP camera/window unavailable");
            }
            else
            {
                record.cameraName = "";
                if (state != "hipfire")
                    record.reason = AppendReason(record.reason, "production OpticAimGeometry path unavailable");
            }

            if (state == "hipfire" && !record.measurementFinite)
                record.measurementFinite = true;
            if (record.mountDelta > 0.00001f)
                record.reason = AppendReason(record.reason, "mount transform changed during ADS state");

            if (!record.socketFound || !record.spawnedOpticFound || !record.mountContactMeasured
                || !record.mountContactWithinTolerance || !record.railMeasured
                || !record.railGapWithinTolerance || !record.measurementFinite
                || record.reason.Length > 0)
                record.status = "FAIL";
            _overlayText = weaponId + " / " + opticId + " / " + state
                + "  adsBlend=" + ads.ToString("F2")
                + "  center=(" + record.centerX.ToString("F3") + "," + record.centerY.ToString("F3") + ")"
                + " height=" + record.projectedHeight.ToString("F3") + "/" + record.targetHeight.ToString("F3")
                + " mountDelta=" + record.mountDelta.ToString("F6")
                + " owner=" + record.playerRootName + " rig#" + record.rigInstanceId
                + " motion#" + record.motionInstanceId
                + " activeRigs=" + record.activeFpWeaponRigCount
                + " activeViews=" + record.activeWeaponViewCount
                + " baseGap=" + record.mountBaseGap.ToString("F4")
                + " railGap=" + record.railGap.ToString("F4")
                + "  " + record.status;
            record.captureCaption = _overlayText;
            return record;
        }

        private static void PrepareEvidenceDirectory(string output)
        {
            // This exact directory is a generated evidence artifact. Remove only
            // generated output formats so stale PNGs cannot satisfy File.Exists or
            // be pulled into a new contact sheet.
            if (!Directory.Exists(output)) return;
            foreach (var file in Directory.GetFiles(output))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "manifest.md", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "TPGripIk-stageB-audit.md", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }

        private static void CaptureCurrentFrame(string path)
        {
            var width = Mathf.Max(1, Screen.width);
            var height = Mathf.Max(1, Screen.height);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                DestroyImmediateSafe(texture);
            }
        }

        private IEnumerator WaitForStablePose(FPWeaponMotion motion, GameObject view,
            WeaponAttachmentView attachments, WeaponController controller)
        {
            _poseStable = false;
            var camera = ResolveProductionViewCamera(motion);
            var weaponView = view != null ? view.GetComponent<WeaponView>() : null;
            var lastPosition = motion != null ? motion.transform.localPosition : Vector3.zero;
            var lastRotation = motion != null ? motion.transform.localRotation : Quaternion.identity;
            var lastCenter = new Vector2(float.NaN, float.NaN);
            var lastHeight = float.NaN;
            var stableFrames = 0;
            for (var frame = 0; frame < 180; frame++)
            {
                yield return null;
                yield return new WaitForEndOfFrame();
                var poseStable = motion != null
                    && Vector3.Distance(lastPosition, motion.transform.localPosition) <= 0.00025f
                    && Quaternion.Angle(lastRotation, motion.transform.localRotation) <= 0.08f;
                var windowStable = false;
                if (camera != null && weaponView != null && attachments != null
                    && OpticAimGeometry.TryResolveLocal(controller, weaponView, out var local))
                {
                    var socket = attachments.GetSocketTransform(AttachmentSlotType.Optic);
                    if (socket != null && local.HasWindow)
                    {
                        var frameData = OpticAimGeometry.Evaluate(local, socket);
                        var projection = CameraProjection.From(camera);
                        var center = projection.ProjectToViewport(frameData.WindowCenterWorld);
                        var top = projection.ProjectToViewport(frameData.WindowCenterWorld
                            + frameData.UpWorld * frameData.WindowHalfHeightMeters);
                        var bottom = projection.ProjectToViewport(frameData.WindowCenterWorld
                            - frameData.UpWorld * frameData.WindowHalfHeightMeters);
                        var height = top.y - bottom.y;
                        windowStable = IsFinite(center) && IsFinite(height)
                            && (!float.IsNaN(lastCenter.x)
                                && Vector2.Distance(lastCenter, new Vector2(center.x, center.y)) <= 0.001f
                                && Mathf.Abs(lastHeight - height) <= 0.001f);
                        lastCenter = new Vector2(center.x, center.y);
                        lastHeight = height;
                    }
                }
                if (poseStable && (windowStable || camera == null || weaponView == null))
                    stableFrames++;
                else
                    stableFrames = 0;
                lastPosition = motion != null ? motion.transform.localPosition : lastPosition;
                lastRotation = motion != null ? motion.transform.localRotation : lastRotation;
                if (stableFrames >= 6)
                {
                    _poseStable = true;
                    yield break;
                }
            }
        }

        private void OnGUI()
        {
            if (!_running && !_completed) return;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(12, Screen.height / 80),
                normal = { textColor = Color.white }
            };
            var box = new GUIStyle(GUI.skin.box);
            GUI.Box(new Rect(12f, 12f, Mathf.Min(Screen.width - 24f, 1100f), 54f), GUIContent.none, box);
            GUI.Label(new Rect(22f, 18f, Screen.width - 44f, 42f), _overlayText, style);
        }

        private void BuildContactSheets(string output, string[] captureWeapons)
        {
            foreach (var weaponId in captureWeapons)
            {
                var paths = new string[12];
                for (var oi = 0; oi < OpticIds.Length; oi++)
                for (var si = 0; si < States.Length; si++)
                    paths[oi * States.Length + si] = Path.Combine(output,
                        Safe(weaponId) + "__" + Safe(OpticIds[oi]) + "__" + States[si] + ".png");
                var first = LoadPng(paths[0]);
                var width = first != null ? first.width : 640;
                var height = first != null ? first.height : 360;
                var sheet = new Texture2D(width * 4, height * 3, TextureFormat.RGBA32, false);
                var blank = new Color[width * height];
                for (var i = 0; i < blank.Length; i++) blank[i] = new Color(0.12f, 0.01f, 0.01f, 1f);
                for (var i = 0; i < 12; i++)
                {
                    var image = LoadPng(paths[i]);
                    var opticIndex = i / States.Length;
                    var stateIndex = i % States.Length;
                    var x = opticIndex * width;
                    var y = (States.Length - 1 - stateIndex) * height;
                    if (image != null && image.width == width && image.height == height)
                        sheet.SetPixels(x, y, width, height, image.GetPixels());
                    else sheet.SetPixels(x, y, width, height, blank);
                    if (image != null) DestroyImmediateSafe(image);
                }
                sheet.Apply(false, false);
                File.WriteAllBytes(Path.Combine(output, Safe(weaponId) + "__contact-sheet.png"), sheet.EncodeToPNG());
                DestroyImmediateSafe(sheet);
            }
        }

        private void WriteManifest(string output, string outputRelative)
        {
            var json = new StringBuilder("{\n  \"output\": \"")
                .Append(JsonEscape(outputRelative)).Append("\",\n  \"recordCount\": ")
                .Append(_records.Count).Append(",\n  \"records\": [\n");
            for (var i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                json.Append("    {\"weapon\":\"").Append(JsonEscape(r.weapon))
                    .Append("\",\"optic\":\"").Append(JsonEscape(r.optic))
                    .Append("\",\"state\":\"").Append(r.state)
                    .Append("\",\"adsBlend\":").Append(r.adsBlend.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"actualWeaponId\":\"").Append(JsonEscape(r.actualWeaponId))
                    .Append("\",\"activeViewName\":\"").Append(JsonEscape(r.activeViewName))
                    .Append("\",\"cameraName\":\"").Append(JsonEscape(r.cameraName))
                    .Append("\",\"playerRootName\":\"").Append(JsonEscape(r.playerRootName))
                    .Append("\",\"playerRootInstanceId\":").Append(r.playerRootInstanceId)
                    .Append(",\"rigName\":\"").Append(JsonEscape(r.rigName))
                    .Append("\",\"rigInstanceId\":").Append(r.rigInstanceId)
                    .Append(",\"motionName\":\"").Append(JsonEscape(r.motionName))
                    .Append("\",\"motionInstanceId\":").Append(r.motionInstanceId)
                    .Append(",\"activeFpWeaponRigCount\":").Append(r.activeFpWeaponRigCount)
                    .Append(",\"activeWeaponViewCount\":").Append(r.activeWeaponViewCount)
                    .Append(",\"ownerChainValid\":").Append(r.ownerChainValid ? "true" : "false")
                    .Append(",\"activeViewMatches\":").Append(r.activeViewMatches ? "true" : "false")
                    .Append(",\"poseStable\":").Append(r.poseStable ? "true" : "false")
                    .Append(",\"file\":\"").Append(JsonEscape(r.file)).Append("\",\"fileExists\":").Append(r.fileExists ? "true" : "false")
                    .Append(",\"socketFound\":").Append(r.socketFound ? "true" : "false")
                    .Append(",\"spawnedOpticFound\":").Append(r.spawnedOpticFound ? "true" : "false")
                    .Append(",\"mountContactMeasured\":").Append(r.mountContactMeasured ? "true" : "false")
                    .Append(",\"opticBoundsMinY\":").Append(r.opticBoundsMinY.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"opticBoundsMaxY\":").Append(r.opticBoundsMaxY.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"mountBaseGap\":").Append(r.mountBaseGap.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"mountContactWithinTolerance\":").Append(r.mountContactWithinTolerance ? "true" : "false")
                    .Append(",\"railMeasured\":").Append(r.railMeasured ? "true" : "false")
                    .Append(",\"railTopLocalY\":").Append(r.railTopLocalY.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"railGap\":").Append(r.railGap.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"railGapWithinTolerance\":").Append(r.railGapWithinTolerance ? "true" : "false")
                    .Append(",\"center\":[").Append(r.centerX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",")
                    .Append(r.centerY.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("]")
                    .Append(",\"projectedHeight\":").Append(r.projectedHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"targetHeight\":").Append(r.targetHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"mountDelta\":").Append(r.mountDelta.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"measurementFinite\":").Append(r.measurementFinite ? "true" : "false")
                    .Append(",\"status\":\"").Append(r.status).Append("\",\"reason\":\"")
                    .Append(JsonEscape(r.reason)).Append("\",\"captureCaption\":\"")
                    .Append(JsonEscape(r.captureCaption)).Append("\"}");
                if (i + 1 < _records.Count) json.Append(',');
                json.Append('\n');
            }
            json.Append("  ]\n}\n");
            File.WriteAllText(Path.Combine(output, "manifest.json"), json.ToString(), Encoding.UTF8);

            var md = new StringBuilder("# C2-B 全枪械基础瞄具证据\n\n")
                .Append("实际 Arena Player + FPWeaponRig + WeaponAttachmentView + FPWeaponMotion + OpticAimGeometry 路径。\n\n")
                .Append("总记录：").Append(_records.Count).Append("，每把枪另有 4×3 contact sheet。\n\n")
                .Append("| weapon | optic | state | adsBlend | playerRoot | rig# | motion# | activeRigs | activeViews | actualWeaponId | activeView | camera | image | baseGap | railTopY | railGap | boundsY | center | height/target | mountDelta | status | reason | caption |\n")
                .Append("|---|---|---|---:|---|---:|---:|---:|---:|---|---|---|---|---:|---:|---:|---|---|---:|---:|---|---|---|\n");
            foreach (var r in _records)
                md.Append("| ").Append(r.weapon).Append(" | ").Append(r.optic).Append(" | ").Append(r.state)
                    .Append(" | ").Append(r.adsBlend.ToString("F2")).Append(" | ").Append(r.playerRootName)
                    .Append(" | ").Append(r.rigInstanceId).Append(" | ").Append(r.motionInstanceId)
                    .Append(" | ").Append(r.activeFpWeaponRigCount).Append(" | ").Append(r.activeWeaponViewCount)
                    .Append(" | ").Append(r.actualWeaponId)
                    .Append(" | ").Append(r.activeViewName).Append(" | ").Append(r.cameraName).Append(" | ").Append(r.file)
                    .Append(" | ").Append(r.mountBaseGap.ToString("F4"))
                    .Append(" | ").Append(r.railTopLocalY.ToString("F4"))
                    .Append(" | ").Append(r.railGap.ToString("F4")).Append(" | (")
                    .Append(r.opticBoundsMinY.ToString("F4")).Append(", ")
                    .Append(r.opticBoundsMaxY.ToString("F4")).Append(")")
                    .Append(" | (").Append(r.centerX.ToString("F3")).Append(", ").Append(r.centerY.ToString("F3"))
                    .Append(") | ").Append(r.projectedHeight.ToString("F3")).Append("/").Append(r.targetHeight.ToString("F3"))
                    .Append(" | ").Append(r.mountDelta.ToString("F6")).Append(" | ").Append(r.status)
                    .Append(" | ").Append(r.reason.Replace('|', '/')).Append(" | ")
                    .Append((r.captureCaption ?? string.Empty).Replace('|', '/')).Append(" |\n");
            File.WriteAllText(Path.Combine(output, "manifest.md"), md.ToString(), Encoding.UTF8);
        }

        private void WriteAuditDocument(string output)
        {
            var path = Path.Combine(output, "TPGripIk-stageB-audit.md");
            var text = "# StageB TPGripIk 失败审计（已收口，2026-09-21 Codely）\n\n"
                + "原四例 `TPGripIkTests.HandgunTpPrefabs_UseCalibratedRootPose` 失败的根因已定位：C1 阶段的 prefab 回写把 2026-09-21 01:47 的手枪根姿态校准覆盖回 HEAD 值（手枪被压回步枪基准姿态）。\n\n"
                + "已从 TPGripIkTests 常量恢复 4 把 TP 手枪根位姿（本阶段新增的 Attach_Optic 挂点保留），套件 9/9 复绿。本文件仅为历史说明保留，不再描述现存失败。";
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        private void AddWeaponFailures(string weaponId, string output, string reason)
        {
            foreach (var optic in OpticIds) AddCombinationFailures(weaponId, optic, output, reason);
        }

        private void AddCombinationFailures(string weaponId, string opticId, string output, string reason)
        {
            foreach (var state in States)
                _records.Add(new CaptureRecord
                {
                    weapon = weaponId, optic = opticId, state = state, adsBlend = 0f,
                    file = Safe(weaponId) + "__" + Safe(opticId) + "__" + state + ".png",
                    status = "FAIL", reason = reason
                });
        }

        private void WriteFailureManifest(string output, string[] captureWeapons, string reason)
        {
            foreach (var weapon in captureWeapons) AddWeaponFailures(weapon, output, reason);
            WriteManifest(output, representativeOnly
                ? Path.Combine(OutputRelative, "representative")
                : OutputRelative);
            WriteAuditDocument(output);
        }

        private static UnityEngine.Camera ResolveProductionViewCamera(FPWeaponMotion motion)
        {
            if (motion == null) return null;
            var field = typeof(FPWeaponMotion).GetField("_viewCamera",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var camera = field != null ? field.GetValue(motion) as UnityEngine.Camera : null;
            if (camera != null) return camera;
            var parent = motion.transform.parent;
            if (parent == null) return null;
            foreach (var candidate in parent.GetComponentsInChildren<UnityEngine.Camera>(true))
                if (candidate != null && candidate.name == ViewCameraName) return candidate;
            return null;
        }

        private static FPWeaponRig[] FilterActive(FPWeaponRig[] rigs)
        {
            if (rigs == null || rigs.Length == 0) return Array.Empty<FPWeaponRig>();
            var active = new List<FPWeaponRig>();
            foreach (var candidate in rigs)
                if (candidate != null && candidate.isActiveAndEnabled && candidate.gameObject.activeInHierarchy)
                    active.Add(candidate);
            return active.ToArray();
        }

        private static int CountActiveWeaponViews(Transform ownerRoot)
        {
            if (ownerRoot == null) return 0;
            var count = 0;
            foreach (var view in ownerRoot.GetComponentsInChildren<WeaponView>(true))
                if (view != null && view.isActiveAndEnabled && view.gameObject.activeInHierarchy)
                    count++;
            return count;
        }

        private static bool TryGetRendererBoundsInReference(GameObject root, Transform reference,
            out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;
            if (root == null || reference == null) return false;

            // MeshFilter vertices give an exact socket-local contact plane even
            // after the LPFP mount Euler. Renderer world AABBs would overstate
            // penetration/clearance for rotated optics.
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                foreach (var vertex in meshFilter.sharedMesh.vertices)
                    Encapsulate(ref bounds, ref initialized,
                        reference.InverseTransformPoint(meshFilter.transform.TransformPoint(vertex)));
            }

            if (initialized) return true;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var localBounds = renderer.localBounds;
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    var local = new Vector3(
                        x == 0 ? localBounds.min.x : localBounds.max.x,
                        y == 0 ? localBounds.min.y : localBounds.max.y,
                        z == 0 ? localBounds.min.z : localBounds.max.z);
                    Encapsulate(ref bounds, ref initialized,
                        reference.InverseTransformPoint(renderer.transform.TransformPoint(local)));
                }
            }
            return initialized;
        }

        private static bool TryMeasureRailGap(GameObject view, Transform socket,
            GameObject spawnedOptic, float opticBaseLocalY, out float railTopLocalY, out float railGap)
        {
            railTopLocalY = 0f;
            railGap = 0f;
            if (view == null || socket == null) return false;

            var top = float.MinValue;
            var measured = false;
            foreach (var smr in view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null || IsNonRailRenderer(smr.transform, view.transform, spawnedOptic))
                    continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked, true);
                    foreach (var vertex in baked.vertices)
                    {
                        var local = socket.InverseTransformPoint(smr.transform.TransformPoint(vertex));
                        if (local.x < -0.30f || local.x > 0.30f || Mathf.Abs(local.z) > 0.08f)
                            continue;
                        top = Mathf.Max(top, local.y);
                        measured = true;
                    }
                }
                finally { DestroyImmediateSafe(baked); }
            }

            // Static MeshFilters are used by a few native TP/preview paths and
            // remain a valid fallback for a view without a skinned gun body.
            if (!measured)
            {
                foreach (var mf in view.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null || IsNonRailRenderer(mf.transform, view.transform, spawnedOptic))
                        continue;
                    foreach (var vertex in mf.sharedMesh.vertices)
                    {
                        var local = socket.InverseTransformPoint(mf.transform.TransformPoint(vertex));
                        if (local.x < -0.30f || local.x > 0.30f || Mathf.Abs(local.z) > 0.08f)
                            continue;
                        top = Mathf.Max(top, local.y);
                        measured = true;
                    }
                }
            }

            if (!measured || !IsFinite(top)) return false;
            railTopLocalY = top;
            railGap = opticBaseLocalY - railTopLocalY;
            return IsFinite(railGap);
        }

        private static bool IsNonRailRenderer(Transform renderer, Transform viewRoot, GameObject spawnedOptic)
        {
            if (renderer == null) return true;
            if (spawnedOptic != null && renderer.IsChildOf(spawnedOptic.transform)) return true;
            // 仅按渲染器自身节点名排除装饰/活动件；禁止沿父链上溯——"arms"/"Armature"
            // 含子串 "arm" 会把整条骨骼下的枪体误杀，运行时测量恒空（2026-09-21 修复，
            // 与 RepresentativeOpticRailContactTests 同一根因）。配件克隆整树仍由
            // IsChildOf(spawnedOptic) 拦截，Att_* 根节点名继续覆盖其它已生成配件。
            var name = renderer.name.ToLowerInvariant();
            if (name.StartsWith("att_") || name.Contains("scope") || name.Contains("iron")
                || name.Contains("sight") || name.Contains("bullet") || name.Contains("silencer")
                || name.Contains("knife") || name.Contains("slider") || name.Contains("mag")
                || name.Contains("grip") || name.Contains("bipod") || name.Contains("arms"))
                return true;
            return false;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool initialized, Vector3 point)
        {
            if (!initialized)
            {
                bounds = new Bounds(point, Vector3.zero);
                initialized = true;
            }
            else bounds.Encapsulate(point);
        }

#if UNITY_EDITOR
        private static Dictionary<string, WeaponDefinition> LoadFormalDefinitions()
        {
            var result = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponDefinition"))
            {
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (definition != null && Array.IndexOf(WeaponIds, definition.CatalogItemId) >= 0)
                    result[definition.CatalogItemId] = definition;
            }
            return result;
        }
#endif

        private static string ProjectPath(string relative)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative));

        private static string Key(string weapon, string optic) => weapon + "|" + optic;
        private static string Safe(string value) => value.Replace('.', '_').Replace('/', '_');
        private static string AppendReason(string current, string reason)
            => string.IsNullOrEmpty(current) ? reason : current + "; " + reason;

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string JsonEscape(string value)
            => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        private static Texture2D LoadPng(string path)
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            return texture.LoadImage(bytes) ? texture : null;
        }

        private static void DestroyImmediateSafe(UnityEngine.Object obj)
        {
#if UNITY_EDITOR
            if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
#else
            if (obj != null) Destroy(obj);
#endif
        }
    }
}
