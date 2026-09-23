using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 无 PlayMode 的 SCAR × 553 构图取证入口。它只创建 HideFlags.DontSave 的临时
    /// prefab 实例，并在 finally 销毁；不会保存或改动当前场景/资产。
    /// 它以生产校准数据求一次完整光轴姿态，并输出 45° FP 相机下实际镜体投影高度，
    /// 用来确认 ADS 解是否确实把模型带到眼前，而不是只让数学单测通过。
    /// </summary>
    internal static class Scar553AdsProbe
    {
        private const string ScarPrefabPath = "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab";
        private const string ScopePrefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_04.prefab";
        private const string Scope03PrefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_03.prefab";
        private const string Scope04PrefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_04.prefab";
        private const string WeaponId = "weapon.rifle03";
        private const string OpticId = "attach.lpfp.optic.02";

        [MenuItem("Tools/Game/Diagnostics/ADS/Probe SCAR + 553 (isolated prefab)")]
        private static void Probe553()
        {
            Probe(ScopePrefabPath, "Scar553AdsProbe.png");
        }

        [MenuItem("Tools/Game/Diagnostics/Mount/Probe SCAR + 553 HipFire mount")]
        private static void Probe553HipFireMount()
        {
            // Mount audit intentionally leaves FP_Weapon_Root at its authored hip-fire
            // pose.  It applies only the catalog mount and AttachmentCalibration chain;
            // no ADS solver, eye point, window framing, or reticle code is involved.
            Probe(Scope04PrefabPath, "Scar553HipFireMount.png", applyAdsPose: false);
        }

        // Asset audit only.  These retain the actual 553 mount calibration but render a
        // candidate source mesh, making a catalog/model mismatch visible before any
        // production catalog remap is considered.
        [MenuItem("Tools/Game/Diagnostics/ADS/Audit SCAR mount + Scope_03 candidate")]
        private static void ProbeScope03Candidate()
        {
            Probe(Scope03PrefabPath, "ScarScope03Candidate.png");
        }

        [MenuItem("Tools/Game/Diagnostics/ADS/Audit SCAR mount + Scope_04 candidate")]
        private static void ProbeScope04Candidate()
        {
            Probe(Scope04PrefabPath, "ScarScope04Candidate.png");
        }

        private static void Probe(string scopePrefabPath, string outputFileName, bool applyAdsPose = true)
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            if (catalog == null || catalog.Calibration == null || !catalog.TryGet(OpticId, out var optic))
            {
                Debug.LogError("[ADS Probe] 缺少生产 AttachmentAssetCatalog/553 条目。");
                return;
            }
            if (!catalog.Calibration.TryGetOpticAim(WeaponId, OpticId, out var aim))
            {
                Debug.LogError("[ADS Probe] 缺少 SCAR×553 光轴校准行。");
                return;
            }

            var scarAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ScarPrefabPath);
            if (scarAsset == null)
            {
                Debug.LogError("[ADS Probe] FP_Rifle03_View prefab 丢失。 ");
                return;
            }
            // Camera.Render does not reliably include PrefabUtility preview-stage objects in
            // this editor version.  Use a disposable normal-scene instance far below Arena;
            // only the probe camera's dedicated layer can see it.
            var root = (GameObject)PrefabUtility.InstantiatePrefab(scarAsset);
            SetHideFlagsRecursively(root, HideFlags.DontSave);
            root.name = "ADS_Probe_SCAR_Temporary";
            GameObject cameraHost = null;
            try
            {
                var socket = FindOpticSocket(root);
                if (socket == null)
                {
                    Debug.LogError("[ADS Probe] FP_Rifle03_View 不含 Optic socket。");
                    return;
                }
                var scopeAsset = AssetDatabase.LoadAssetAtPath<GameObject>(scopePrefabPath);
                if (scopeAsset == null)
                {
                    Debug.LogError("[ADS Probe] Scope_02 prefab 丢失。");
                    return;
                }
                var scope = (GameObject)PrefabUtility.InstantiatePrefab(scopeAsset, socket);
                scope.name = "Att_" + OpticId;
                SetHideFlagsRecursively(scope, HideFlags.DontSave);
                scope.transform.localRotation = optic.MountRotation;
                scope.transform.localPosition = optic.mountOffset;
                // 与 WeaponAttachmentView.ApplyAttachments 相同的生产组合校准换算。SCAR×553
                // 有非零 offset；不应用它的 probe 不能代表视频里的实际镜体。
                if (catalog.Calibration.TryGet(WeaponId, OpticId, out var positionOffset,
                        out var rotationOffset, out var authorFrame))
                {
                    var currentFrame = Quaternion.Inverse(root.transform.rotation) * socket.rotation;
                    if (!socket.GetComponent<AttachmentSocket>().GeometryVerified
                        && authorFrame != default && Quaternion.Angle(currentFrame, authorFrame) > 0.05f)
                    {
                        positionOffset = Quaternion.Inverse(currentFrame) * authorFrame * positionOffset;
                        rotationOffset = (Quaternion.Inverse(currentFrame) * authorFrame
                            * Quaternion.Euler(rotationOffset) * Quaternion.Inverse(authorFrame) * currentFrame).eulerAngles;
                    }
                    scope.transform.localPosition += positionOffset;
                    scope.transform.localRotation *= Quaternion.Euler(rotationOffset);
                }

                cameraHost = new GameObject("ADS_Probe_FP_Camera");
                cameraHost.hideFlags = HideFlags.DontSave;
                var camera = cameraHost.AddComponent<UnityEngine.Camera>();
                camera.fieldOfView = 45f;
                camera.aspect = 16f / 9f;
                camera.nearClipPlane = 0.01f;
                camera.transform.position = new Vector3(-0.047f, -1999.918f, -0.299f);
                camera.transform.rotation = Quaternion.identity;
                root.transform.position = new Vector3(0f, -2000f, 0f);

                Bounds opticBoundsInSocket = default;
                bool hasOpticBoundsInSocket = TryGetRendererBoundsInReference(scope, socket,
                    out opticBoundsInSocket);

                Vector3 eyeRoot = root.transform.InverseTransformPoint(socket.TransformPoint(aim.EyePointLocal));
                Vector3 axisRoot = root.transform.InverseTransformDirection(socket.TransformDirection(aim.AxisDirectionLocal)).normalized;
                Vector3 upRoot = root.transform.InverseTransformDirection(socket.TransformDirection(Vector3.up)).normalized;
                bool useFullAxis = OpticAimGeometry.CanUseFullAxisSolve(aim);
                Quaternion rotation = useFullAxis
                    ? OpticAimGeometry.SolveAimLocalRotation(axisRoot, upRoot)
                    : Quaternion.identity;
                if (applyAdsPose)
                    root.transform.localRotation = rotation;
                // Match production: an authored combination window is authoritative;
                // body-bounds approximation remains only for a genuinely uncalibrated optic.
                bool hasWindow = aim.HasWindow;
                Vector3 centerLocal = aim.WindowCenterLocal;
                float halfWidth = aim.WindowHalfWidthMeters;
                float halfHeight = aim.WindowHalfHeightMeters;
                if (!hasWindow)
                    hasWindow = OpticAimGeometry.TryApproximateWindowFromRenderers(socket,
                        out centerLocal, out halfWidth, out halfHeight);
                Vector3 windowRoot = hasWindow
                    ? root.transform.InverseTransformPoint(socket.TransformPoint(centerLocal))
                    : default;
                if (applyAdsPose)
                {
                    // Keep the diagnostic on the same production path as FPWeaponMotion:
                    // a measured axis owns rotation, while the authored window and target
                    // height own eye relief.  The previous probe still snapped the eye point
                    // to the camera for full-axis rows, making the probe show the old giant
                    // near-clipped optic even though runtime already used window framing.
                    root.transform.localPosition = hasWindow
                        ? OpticAimGeometry.SolveWindowFramingLocalPosition(rotation, windowRoot,
                            camera.transform.localPosition, halfHeight, camera.fieldOfView,
                            aim.TargetViewportHeight > 0.01f ? aim.TargetViewportHeight : 0.33f)
                        : OpticAimGeometry.SolveAimLocalPosition(rotation, eyeRoot,
                            camera.transform.localPosition);
                }
                else
                {
                    // HipFire capture: keep the authored root pose and aim a disposable
                    // camera at the optic socket so the physical rail relationship is
                    // visible. This is a mount-only screenshot, not an ADS validation.
                    root.transform.localPosition = Vector3.zero;
                    root.transform.localRotation = Quaternion.identity;
                    // Socket local convention is -X along the rifle, +Y upward, +Z
                    // across the receiver. View from +Z with a small forward offset:
                    // the optic foot, socket/rail datum, and receiver top are all visible.
                    Vector3 mountTarget = socket.TransformPoint(new Vector3(
                        hasOpticBoundsInSocket ? opticBoundsInSocket.center.x : 0.05f,
                        hasOpticBoundsInSocket ? Mathf.Max(0.01f, opticBoundsInSocket.min.y + 0.02f) : 0.02f,
                        0f));
                    Vector3 cameraLocal = new Vector3(
                        hasOpticBoundsInSocket ? opticBoundsInSocket.center.x + 0.08f : 0.05f,
                        hasOpticBoundsInSocket ? opticBoundsInSocket.center.y + 0.02f : 0.05f,
                        0.34f);
                    camera.transform.position = socket.TransformPoint(cameraLocal);
                    camera.transform.rotation = Quaternion.LookRotation(
                        mountTarget - camera.transform.position, socket.up);
                    camera.fieldOfView = 20f;
                }

                const int probeLayer = 31;
                SetLayerRecursively(root, probeLayer);
                root.SetActive(true);
                scope.SetActive(true);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = true;
                    renderer.forceRenderingOff = false;
                }
                camera.enabled = true;
                // The probe owns layer 31 exclusively, so the camera only renders the
                // temporary FP prefab and mounted optic, never the Arena.
                camera.cullingMask = 1 << probeLayer;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogError("[ADS Probe] 隔离 SCAR prefab 没有 Renderer，无法取证。");
                    return;
                }

                if (!TryViewportBounds(camera, scope, out var bounds))
                {
                    Debug.LogError("[ADS Probe] 553 在求解后没有任何位于 FP 相机前方的渲染器。");
                    return;
                }
                float heightPercent = (bounds.yMax - bounds.yMin) * 100f;
                Vector3 firstViewport = camera.WorldToViewportPoint(renderers[0].bounds.center);
                string windowText = "unavailable";
                if (hasWindow
                    && TryWindowViewportBounds(camera, socket, centerLocal, halfWidth, halfHeight, out var windowBounds))
                    windowText = $"x={windowBounds.xMin:F3}..{windowBounds.xMax:F3}, y={windowBounds.yMin:F3}..{windowBounds.yMax:F3}, height={(windowBounds.yMax - windowBounds.yMin) * 100f:F1}%";
                string imagePath = Path.GetFullPath(Path.Combine("Temp", outputFileName));
                string imageEvidence = RenderProbe(camera, imagePath);
                string mode = applyAdsPose ? "ADS" : "HipFireMount";
                string mountContact = hasOpticBoundsInSocket
                    ? $"railSocketY=0.000000; opticBottomY={opticBoundsInSocket.min.y:F6}; " +
                      $"opticTopY={opticBoundsInSocket.max.y:F6}; bottomToRail={opticBoundsInSocket.min.y:F6} " +
                      "(positive=gap, negative=penetration)"
                    : "mountContact=unavailable";
                Debug.Log($"[ADS Probe] SCAR×553 {mode}: viewport x={bounds.xMin:F3}..{bounds.xMax:F3}, " +
                          $"y={bounds.yMin:F3}..{bounds.yMax:F3}, height={heightPercent:F1}% (full mesh); " +
                          $"effective window={windowText}; fullAxis={useFullAxis}. " +
                          $"目标样板有效镜窗为 28–38%；静态姿态图：{imageEvidence}。" +
                          $"scene(camera/root)={camera.gameObject.scene.name}/{root.scene.name}; cullingMask=0x{camera.cullingMask:X8}; renderers={renderers.Length}; " +
                          $"firstRenderer={renderers[0].name} layer={renderers[0].gameObject.layer} viewport={firstViewport:F3}. " +
                          $"socketLocal={socket.localPosition:F6}; opticLocal={scope.transform.localPosition:F6}; " +
                          $"opticSocketDeltaY={(scope.transform.localPosition.y):F6}; {mountContact}. " +
                          $"mesh={scopePrefabPath}; windowLocal=center=({centerLocal.x:F4},{centerLocal.y:F4},{centerLocal.z:F4}), half={halfWidth:F4}×{halfHeight:F4}, authored={aim.HasWindow}, targetViewportHeight={(aim.TargetViewportHeight > 0.01f ? aim.TargetViewportHeight : 0.33f):F3}. " +
                          "SCAR 的 AimIn 资产为空，因此本 probe 对应生产程序化 ADS，而非漏采动画 clip。", root);
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        private static Transform FindOpticSocket(GameObject root)
        {
            foreach (var socket in root.GetComponentsInChildren<AttachmentSocket>(true))
                if (socket.Slot == AttachmentSlotType.Optic) return socket.transform;
            return null;
        }

        private static bool TryViewportBounds(UnityEngine.Camera camera, GameObject scope, out Rect bounds)
        {
            bounds = new Rect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (var renderer in scope.GetComponentsInChildren<Renderer>(true))
            {
                var b = renderer.bounds;
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    Vector3 p = camera.WorldToViewportPoint(new Vector3(x == 0 ? b.min.x : b.max.x,
                        y == 0 ? b.min.y : b.max.y, z == 0 ? b.min.z : b.max.z));
                    if (p.z <= 0f) continue;
                    bounds.xMin = Mathf.Min(bounds.xMin, p.x);
                    bounds.xMax = Mathf.Max(bounds.xMax, p.x);
                    bounds.yMin = Mathf.Min(bounds.yMin, p.y);
                    bounds.yMax = Mathf.Max(bounds.yMax, p.y);
                    any = true;
                }
            }
            return any;
        }

        private static string RenderProbe(UnityEngine.Camera camera, string outputPath)
        {
            const int width = 1280;
            const int height = 720;
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            try
            {
                camera.targetTexture = target;
                camera.backgroundColor = new Color(0.12f, 0.18f, 0.26f, 1f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.Render();
                RenderTexture.active = target;
                var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, image.EncodeToPNG());
                Object.DestroyImmediate(image);
                var info = new FileInfo(outputPath);
                byte[] digest;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(outputPath))
                    digest = sha.ComputeHash(stream);
                string hashPrefix = System.BitConverter.ToString(digest).Replace("-", string.Empty).Substring(0, 16);
                return $"{outputPath} (mtime={info.LastWriteTime:O}, bytes={info.Length}, sha256={hashPrefix})";
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static bool TryWindowViewportBounds(UnityEngine.Camera camera, Transform socket, Vector3 center,
            float halfWidth, float halfHeight, out Rect bounds)
        {
            bounds = new Rect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            bool any = false;
            for (int up = 0; up < 2; up++)
            for (int side = 0; side < 2; side++)
            {
                Vector3 local = center + Vector3.up * (up == 0 ? -halfHeight : halfHeight)
                    + Vector3.forward * (side == 0 ? -halfWidth : halfWidth);
                Vector3 point = camera.WorldToViewportPoint(socket.TransformPoint(local));
                    if (point.z <= 0f) continue;
                bounds.xMin = Mathf.Min(bounds.xMin, point.x);
                bounds.xMax = Mathf.Max(bounds.xMax, point.x);
                bounds.yMin = Mathf.Min(bounds.yMin, point.y);
                bounds.yMax = Mathf.Max(bounds.yMax, point.y);
                any = true;
            }
            return any;
        }

        private static bool TryGetRendererBoundsInReference(GameObject root, Transform reference,
            out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;
            if (root == null || reference == null) return false;

            // Renderer.bounds is a world-space AABB. Converting its eight corners back
            // to socket space can invent a false penetration when the optic is rotated
            // by the LPFP mount Euler. Prefer exact MeshFilter vertices for the contact
            // measurement; use localBounds only for skinned/unsupported renderers.
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                foreach (var vertex in meshFilter.sharedMesh.vertices)
                {
                    Encapsulate(ref bounds, ref initialized,
                        reference.InverseTransformPoint(meshFilter.transform.TransformPoint(vertex)));
                }
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
                    Vector3 local = new Vector3(
                        x == 0 ? localBounds.min.x : localBounds.max.x,
                        y == 0 ? localBounds.min.y : localBounds.max.y,
                        z == 0 ? localBounds.min.z : localBounds.max.z);
                    Encapsulate(ref bounds, ref initialized,
                        reference.InverseTransformPoint(renderer.transform.TransformPoint(local)));
                }
            }
            return initialized;
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

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
        }

        private static void SetHideFlagsRecursively(GameObject go, HideFlags flags)
        {
            go.hideFlags = flags;
            foreach (Transform child in go.transform) SetHideFlagsRecursively(child.gameObject, flags);
        }
    }
}
