using System.Collections.Generic;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// P4 实体镜（CF 计划 I4b/I4c）：变焦瞄具的"真实镜体"第一人称表现——
    /// 镜体 = 项目瞄具模型本体（ADS 姿态保持可见，不再全屏 overlay 藏枪）；
    /// 镜片 = 配件局部有效孔径（未标定的内置镜仍使用 OpticLensMath 回退），
    /// 镜内 = 独立 RT 相机（与世界相机同位姿的子节点）按独立倍率渲染
    /// （AdsFovMath.ScopeFov：scopeFov = 2·atan(tan(worldFov/2)/mag)，worldFov 取镜片在
    /// 眼点处取代的世界视场 2·θL——穿透放大率恒等于 mag）；分划 = 镜片前程序化贴片。
    /// 1x 瞄具（红点/全息）与非变焦路径不走本组件（OpticAdsView 既有表现不变）；
    /// 镜片构建失败（无挂点/无镜体几何）时同样让位 OpticAdsView 既有 overlay 兜底。
    /// 资源纪律（计划 I4b）：换枪/换瞄具/失活/销毁 → 镜片贴片、RT、RT 相机全量回收。
    /// 本组件只做表现；命中/散布/灵敏度数值权威仍在 Gameplay（PlayerAimState）。
    /// </summary>
    [DefaultExecutionOrder(25)] // FPWeaponMotion(20) finalizes the displayed aperture first.
    public sealed class PhysicalScopeView : MonoBehaviour
    {
        private const float ShowStartAds = 0.35f;   // 低于此开镜进度不启用 RT 相机
        private const float MidZoomRtSize = 512;    // 低倍（2-4x）RT 基准边长
        private const float HighZoomRtSize = 1024;  // 高倍（6x+）RT 基准边长
        private const float DefaultEyeRelief = 0.05f;      // 无眼点校准行的推导出瞳距离（米）
        private const float ReticleForwardOffset = 0.00005f; // 同一孔径内仅避让镜片深度
        private static readonly Color LensTint = new(0.86f, 0.93f, 1f, 1f); // 淡蓝镀膜（镜内蓝片遮挡）

        private FPWeaponRig _rig;
        private WeaponController _controller;
        private PlayerAimState _aimState;

        private UnityEngine.Camera _worldCamera;
        private UnityEngine.Camera _viewCamera;
        private UnityEngine.Camera _scopeCamera;
        private RenderTexture _rt;
        private GameObject _lensNode;
        private GameObject _reticleNode;
        private Material _lensMaterial;
        private Material _reticleMaterial;
        private string _builtLensKey;
        private string _handledOpticId;
        private float _lensRadius = 0.016f;
        private float _lensHalfAngleDeg = 20f;

        /// <summary>当前由实体镜接管的瞄具 itemId（null = 未接管）。OpticAdsView 据此让位。</summary>
        public static string HandlingOpticId { get; private set; }

        /// <summary>HUD queries its own weapon rig, never a global optic ID that another
        /// player's view can overwrite in the same frame.</summary>
        public bool HandlesOptic(string opticId) => !string.IsNullOrEmpty(opticId)
            && _lensNode != null && _reticleNode != null
            && _lensNode.activeInHierarchy && _reticleNode.activeInHierarchy
            && string.Equals(_handledOpticId, opticId, System.StringComparison.Ordinal);

        /// <summary>挂载入口（幂等）：与 FPWeaponRig 同对象（Presentation 程序集内零资产改动）。
        /// Dedicated Server 构建（无表现）不挂载。</summary>
        internal static void EnsureMounted(FPWeaponRig rig)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return; // 专用服务器无渲染：不创建 RT 相机与镜体表现
#else
            if (rig == null) return;
            if (rig.GetComponent<PhysicalScopeView>() != null) return;
            rig.gameObject.AddComponent<PhysicalScopeView>();
#endif
        }

        private void Awake()
        {
            _rig = GetComponent<FPWeaponRig>();
            _controller = GetComponentInParent<WeaponController>();
            _aimState = GetComponentInParent<PlayerAimState>();
        }

        private void OnDisable() => ReleaseScopeAssets();

        private void OnDestroy() => ReleaseScopeAssets();

        private void Update()
        {
            HandlingOpticId = null;
            if (_rig == null) _rig = GetComponent<FPWeaponRig>();
            if (_controller == null) _controller = GetComponentInParent<WeaponController>();
            if (_aimState == null) _aimState = GetComponentInParent<PlayerAimState>();
            if (_rig == null || _controller == null || _aimState == null
                || !_controller.IsInitialized || _rig.ActiveView == null)
            {
                DeactivateScopeCamera();
                ReleaseScopeAssets();
                return;
            }

            OpticAimContext optic = _controller.CurrentOpticAim;
            if (!optic.IsPhysicalScope || string.IsNullOrEmpty(optic.ItemId)
                || optic.Tier == OpticAimTier.None)
            {
                DeactivateScopeCamera();
                ReleaseScopeAssets();
                return;
            }

            string lensKey = _rig.ActiveView.GetInstanceID() + "|" + optic.ItemId + "|" + (int)optic.Tier;
            // Reapplying the same loadout replaces the optic hierarchy without changing
            // its item ID or the cached view. Its destroyed glass must be rebuilt too.
            if (_builtLensKey != lensKey || _lensNode == null || _reticleNode == null
                || !_lensNode.activeInHierarchy || !_reticleNode.activeInHierarchy)
            {
                ReleaseScopeAssets();
                if (!TryBuildLens(_rig.ActiveView, optic))
                {
                    // 诚实降级：镜片构建失败 → OpticAdsView 既有 overlay/世界 FOV 路径接管
                    DeactivateScopeCamera();
                    return;
                }
                _builtLensKey = lensKey;
            }

            HandlingOpticId = optic.ItemId;
            _handledOpticId = optic.ItemId;
            if (_aimState.Ads01 < ShowStartAds)
            {
                DeactivateScopeCamera();
                return;
            }

            UnityEngine.Camera world = ResolveWorldCamera();
            if (world == null)
            {
                DeactivateScopeCamera();
                return;
            }
            EnsureScopeCamera(world);
            EnsureRenderTarget(optic.Tier);
            if (_scopeCamera == null || _rt == null || _lensNode == null)
            {
                DeactivateScopeCamera();
                return;
            }

            SetLensImageVisible(true);
            _scopeCamera.enabled = true;
        }

        private void LateUpdate()
        {
            if (_scopeCamera != null && _scopeCamera.enabled && _worldCamera != null && _controller != null)
                UpdateLensFov(_worldCamera, _controller.CurrentOpticAim.Magnification);
        }

        /// <summary>世界相机解析：剔除 FirstPersonView 层的启用相机（URP Base），
        /// 跳过 RT 相机（含本组件旧实例）与 overlay 武器相机。同帧位姿基准。</summary>
        private UnityEngine.Camera ResolveWorldCamera()
        {
            // Camera.allCameras also contains StaticUiCamera and other auxiliary views.
            // The player's tagged world camera is authoritative, regardless of creation order.
            UnityEngine.Camera primary = UnityEngine.Camera.main;
            if (primary != null && primary.isActiveAndEnabled && primary.targetTexture == null)
            {
                _worldCamera = primary;
                return primary;
            }
            if (_worldCamera != null && _worldCamera.isActiveAndEnabled) return _worldCamera;
            _worldCamera = null;
            int fpLayer = LayerMask.NameToLayer("FirstPersonView");
            int fpBit = fpLayer >= 0 ? 1 << fpLayer : 0;
            foreach (UnityEngine.Camera candidate in UnityEngine.Camera.allCameras)
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (candidate == _scopeCamera) continue;
                if (candidate.targetTexture != null) continue;
                if ((candidate.cullingMask & fpBit) != 0) continue;
                _worldCamera = candidate;
                break;
            }
            return _worldCamera;
        }

        // ---------------------------------------------------------------- 镜片构建（项目派生）

        /// <summary>构建镜片/分划盘：几何 = 已装瞄具克隆优先，否则出厂镜体（内置瞄具）；
        /// 包围盒折算到挂点局部系（-X=前向/+Y=上）后由 OpticLensMath 推导后镜圈圆盘。</summary>
        private bool TryBuildLens(GameObject view, OpticAimContext optic)
        {
            if (view == null || _controller?.Definition == null) return false;
            var attachments = view.GetComponent<WeaponAttachmentView>();
            if (attachments == null) return false;
            Transform socket = attachments.GetSocketTransform(AttachmentSlotType.Optic);
            if (socket == null) return false;

            Transform geometry = attachments.FindSpawned(optic.ItemId);
            if (geometry == null) geometry = attachments.FindStockScope();
            if (geometry == null) return false;

            if (attachments.TryGetPhysicalScopeAim(optic.ItemId, out var aperture))
            {
                // Render in the optic's own frame: mount corrections, roll and animation
                // affect the glass and reticle together. A circular mesh clips both to the aperture.
                var entry = AttachmentAssetCatalog.LoadOrDefault().Find(optic.ItemId);
                BuildLensNodes(geometry, entry.scopeApertureCenter, entry.scopeApertureRadius,
                    optic.Tier, Quaternion.LookRotation(Vector3.back, Vector3.up));
                _lensRadius = aperture.WindowHalfHeightMeters;
                return _lensNode != null && _reticleNode != null;
            }

            Bounds local = ComputeSocketSpaceBounds(geometry, socket);
            if (!TryResolveEyeLocal(optic, local, out Vector3 eyeLocal)) return false;
            if (!OpticLensMath.TryDeriveLensFrame(local, eyeLocal, out Vector3 lensPos, out float radius)) return false;

            BuildLensNodes(socket, lensPos, radius, optic.Tier, Quaternion.LookRotation(Vector3.right, Vector3.up));
            _lensRadius = radius;
            return _lensNode != null && _reticleNode != null;
        }

        /// <summary>瞄具渲染包围盒 → 挂点局部系（8 角采样，相机空间 AABB 折算局部轴对齐盒）。</summary>
        private static Bounds ComputeSocketSpaceBounds(Transform geometry, Transform socket)
        {
            var bounds = new Bounds(socket.InverseTransformPoint(geometry.position), Vector3.zero);
            foreach (Renderer renderer in geometry.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                Bounds world = renderer.bounds;
                Vector3 c = world.center;
                Vector3 e = world.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z);
                    bounds.Encapsulate(socket.InverseTransformPoint(corner));
                }
            }
            return bounds;
        }

        /// <summary>眼点（挂点局部系）：眼点校准行优先（与 FPWeaponMotion ADS 对位同源数据），
        /// 缺行按镜体几何推导（光轴=横向中轴、眼距=默认出瞳）——诚实降级，不影响构建。</summary>
        private bool TryResolveEyeLocal(OpticAimContext optic, Bounds local, out Vector3 eyeLocal)
        {
            eyeLocal = default;
            var calibration = AttachmentAssetCatalog.LoadOrDefault() != null
                ? AttachmentAssetCatalog.LoadOrDefault().Calibration
                : null;
            string weaponItemId = _controller.Definition.CatalogItemId;
            if (calibration != null
                && (calibration.TryGetOpticEyePoint(weaponItemId, optic.ItemId, out eyeLocal)
                    || calibration.TryGetOpticEyePoint(string.Empty, optic.ItemId, out eyeLocal)))
            {
                return float.IsFinite(eyeLocal.x) && float.IsFinite(eyeLocal.y) && float.IsFinite(eyeLocal.z);
            }
            eyeLocal = new Vector3(
                local.max.x + DefaultEyeRelief,
                local.min.y + local.size.z * 0.5f,
                local.center.z);
            return float.IsFinite(eyeLocal.x) && float.IsFinite(eyeLocal.y) && float.IsFinite(eyeLocal.z);
        }

        private void BuildLensNodes(Transform socket, Vector3 lensPosLocal, float radius,
            OpticAimTier tier, Quaternion orientation)
        {
            int layer = socket.gameObject.layer;
            Mesh disc = GetDiscMesh();
            if (disc == null) return;
            _lensMaterial = CreateLensMaterial();
            if (_lensMaterial == null) return;
            _reticleMaterial = CreateReticleMaterial(tier);
            // R4 审计修复：单位圆盘按推导半径整体缩放——镜片/分划几何尺寸与 OpticLensMath 的
            // 厘米级半径一致（旧实现未缩放，实际盘面恒为单位圆）
            _lensNode = CreateDiscNode("PhysicalScopeLens", socket, lensPosLocal, layer, disc,
                _lensMaterial, radius, orientation);
            _reticleNode = CreateDiscNode("PhysicalScopeReticle", socket,
                lensPosLocal + orientation * Vector3.forward
                    * (tier == OpticAimTier.LowZoom ? ReticleForwardOffset : .0008f), layer, disc,
                _reticleMaterial, radius, orientation);
            SetLensImageVisible(false); // RT 首帧渲染前镜片不显示（避免黑盘闪现）
        }

        private static GameObject CreateDiscNode(string nodeName, Transform parent, Vector3 localPos,
            int layer, Mesh mesh, Material material, float radius, Quaternion orientation)
        {
            if (material == null) return null;
            var go = new GameObject(nodeName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one * Mathf.Max(0.001f, radius); // 单位圆盘 → 实际半径
            // 节点 +Z（盘面法线）指向眼侧（挂点 +X）；+Y 对齐挂点上向，分划/纹理方向随世界竖直。
            go.transform.localRotation = orientation;
            go.layer = layer;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go;
        }

        private static Material CreateLensMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            Material mat = new Material(shader) { name = "PhysicalScopeLens" };
            mat.SetColor("_BaseColor", LensTint);
            return mat;
        }

        private static Material CreateReticleMaterial(OpticAimTier tier)
        {
            // The reticle is the aperture's final transparent layer. Mesh depth from
            // the animated scope housing can otherwise hide it during recoil/ADS.
            Shader shader = Shader.Find("Game/UI/NativeSniperScope");
            if (shader == null) return null;
            if (tier == OpticAimTier.HighZoom)
            {
                var native = Game.Gameplay.Settings.NativeScopeReticleCatalog.Load();
                if (native == null || native.SniperTexture == null) return null;
                var original = new Material(shader) { name = "LPFP Native Sniper Reticle" };
                original.mainTexture = native.SniperTexture;
                original.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                return original;
            }
            Texture2D reticle = ScopeReticleTexture.Get(tier);
            if (reticle == null) return null;
            Material mat = new Material(shader) { name = "PhysicalScopeReticle", color = Color.white };
            mat.mainTexture = reticle;
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return mat;
        }

        /// <summary>RT 首帧渲染前隐藏镜内影像盘（模型自带玻璃可见）；启用时随 RT 相机同开。
        /// R4 审计修复：分划与镜片同步显隐（旧实现失活只藏镜片、分划残留）。</summary>
        private void SetLensImageVisible(bool visible)
        {
            if (_lensNode != null && _lensNode.TryGetComponent(out MeshRenderer lensRenderer))
                lensRenderer.enabled = visible;
            if (_reticleNode != null && _reticleNode.TryGetComponent(out MeshRenderer reticleRenderer))
                reticleRenderer.enabled = visible;
        }

        // ---------------------------------------------------------------- RT 相机与资源释放

        /// <summary>RT 相机：世界相机的同位姿子节点（零脚本延迟继承 look/recoil/后坐回声），
        /// 追加为最后子节点——FPWeaponMotion.ResolveViewCamera 仍先命中 FP View Camera（顺序不破坏）。
        /// 剔除 = 世界相机剔除（已不含 8/9）再防御性剥离 FP/本地身体/UI 层。</summary>
        private void EnsureScopeCamera(UnityEngine.Camera world)
        {
            if (_scopeCamera != null)
            {
                if (_scopeCamera.transform.parent == world.transform) return;
                DestroyScopeCamera();
            }
            var go = new GameObject("PhysicalScopeCamera");
            go.transform.SetParent(world.transform, false);
            _scopeCamera = go.AddComponent<UnityEngine.Camera>();
            _scopeCamera.enabled = false;
            // R4 审计修复：RT 相机必须绑定 targetTexture——旧实现创建了 RT 与材质却从未赋给相机，
            // 相机输出根本不会写入镜内纹理（画面恒空/黑盘）
            if (_rt != null) _scopeCamera.targetTexture = _rt;
            _scopeCamera.clearFlags = world.clearFlags;
            _scopeCamera.backgroundColor = world.backgroundColor;
            _scopeCamera.nearClipPlane = Mathf.Clamp(world.nearClipPlane, 0.02f, 0.3f);
            _scopeCamera.farClipPlane = Mathf.Max(20f, world.farClipPlane);
            _scopeCamera.useOcclusionCulling = false;
            _scopeCamera.allowMSAA = false;
            _scopeCamera.eventMask = 0;
            _scopeCamera.cullingMask = BuildScopeMask(world);
        }

        private static int BuildScopeMask(UnityEngine.Camera world)
        {
            int mask = world.cullingMask;
            mask &= ~LayerBit("FirstPersonView");
            mask &= ~LayerBit("LocalPlayerBody");
            mask &= ~LayerBit("UI");
            return mask;
        }

        private static int LayerBit(string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? 1 << layer : 0;
        }

        /// <summary>RT 维护（R4 审计修复）：方形 RT（512/1024）——镜内相机 aspect 恒 1，圆盘 UV 的
        /// 0..1 映射两轴等比，穿透放大率与屏幕宽高比（16:9/21:9）无关（旧矩形 RT + 全 UV 映射导致
        /// 横纵成像比例不一致、倍率失真）。</summary>
        private void EnsureRenderTarget(OpticAimTier tier)
        {
            int size = Mathf.RoundToInt(tier == OpticAimTier.HighZoom ? HighZoomRtSize : MidZoomRtSize);
            if (_rt != null && _rt.width != size) ReleaseRenderTarget();
            if (_rt != null) return;
            _rt = new RenderTexture(size, size, 24, RenderTextureFormat.Default)
            {
                name = "PhysicalScope_RT",
                useMipMap = false,
            };
            _rt.Create();
            if (_scopeCamera != null) _scopeCamera.targetTexture = _rt;
            if (_lensMaterial != null) _lensMaterial.SetTexture("_BaseMap", _rt);
        }

        /// <summary>用 FP 相机下的镜片屏占比求 RT 视场。世界相机与 FP 相机的位置/FOV
        /// 可以不同，不能用世界相机到枪模的距离代替显示尺寸。方形 RT 保持两轴等比。</summary>
        private void UpdateLensFov(UnityEngine.Camera world, float magnification)
        {
            if (_lensNode == null || _scopeCamera == null || _rt == null) return;
            Transform lens = _lensNode.transform;
            if (_viewCamera == null && _controller != null)
            {
                int fpBit = LayerBit("FirstPersonView");
                foreach (var candidate in _controller.GetComponentsInChildren<UnityEngine.Camera>(true))
                    if (candidate.targetTexture == null && (candidate.cullingMask & fpBit) != 0)
                    { _viewCamera = candidate; break; }
            }
            if (_viewCamera != null)
            {
                Vector3 top = _viewCamera.WorldToViewportPoint(lens.TransformPoint(Vector3.up));
                Vector3 bottom = _viewCamera.WorldToViewportPoint(lens.TransformPoint(Vector3.down));
                float diameter = Mathf.Abs(top.y - bottom.y);
                float tanHalf = diameter * Mathf.Tan(world.fieldOfView * .5f * Mathf.Deg2Rad)
                    / Mathf.Max(1f, magnification);
                if (top.z > 0f && bottom.z > 0f && float.IsFinite(tanHalf) && tanHalf > 0f)
                {
                    _scopeCamera.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(tanHalf) * Mathf.Rad2Deg, .1f, 55f);
                    _scopeCamera.aspect = 1f;
                    return;
                }
            }
            Vector3 toCamera = world.transform.position - lens.position;
            float eyeDistance = Vector3.Dot(toCamera, lens.forward); // 沿盘面法线（眼侧）的垂直距眼
            _lensHalfAngleDeg = OpticLensMath.LensHalfAngleDegrees(eyeDistance, _lensRadius);
            _scopeCamera.fieldOfView = Mathf.Clamp(
                AdsFovMath.ScopeFov(_lensHalfAngleDeg * 2f, magnification), 1.5f, 55f);
            _scopeCamera.aspect = 1f;
        }

        private void DeactivateScopeCamera()
        {
            if (_scopeCamera != null) _scopeCamera.enabled = false;
            SetLensImageVisible(false);
        }

        /// <summary>活动镜资源全量回收（换枪/换瞄具/失活/销毁）：镜片/分划贴片、RT、RT 相机、
        /// 分划材质（R4 审计修复：旧实现未保存未销毁——泄漏）与静态接管标记。</summary>
        private void ReleaseScopeAssets()
        {
            if (_lensNode != null) ReleaseObject(_lensNode);
            if (_reticleNode != null) ReleaseObject(_reticleNode);
            _lensNode = null;
            _reticleNode = null;
            if (_lensMaterial != null) ReleaseObject(_lensMaterial);
            _lensMaterial = null;
            if (_reticleMaterial != null) ReleaseObject(_reticleMaterial); // 分划贴图为静态缓存，不销毁
            _reticleMaterial = null;
            ReleaseRenderTarget();
            DestroyScopeCamera();
            _builtLensKey = null;
            // R4 审计修复：失活/销毁/换装必须清除静态接管标记——OpticAdsView 的让位判定依赖它，
            // 残留会让 1x/降级路径被错误接管
            if (!string.IsNullOrEmpty(_handledOpticId) && HandlingOpticId == _handledOpticId)
                HandlingOpticId = null;
            _handledOpticId = null;
        }

        private void ReleaseRenderTarget()
        {
            if (_rt == null) return;
            _rt.Release();
            ReleaseObject(_rt);
            _rt = null;
        }

        private void DestroyScopeCamera()
        {
            if (_scopeCamera == null) return;
            ReleaseObject(_scopeCamera.gameObject);
            _scopeCamera = null;
        }

        // ---------------------------------------------------------------- 几何与分划生成
        private static void ReleaseObject(Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        /// <summary>镜片圆盘网格（共享静态）：法线 +Z；从眼侧看 +Z 正面时屏幕右是局部 -X，
        /// 因此 U 反向，避免镜内世界与非对称分划左右镜像。
        /// 三角法线与顶点法线一致指向 +Z，正面朝眼侧。</summary>
        private static Mesh _discMesh;
        private static Mesh GetDiscMesh()
        {
            if (_discMesh != null) return _discMesh;
            const int segments = 96;
            var vertices = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var normals = new Vector3[segments + 1];
            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            normals[0] = Vector3.forward;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
                vertices[i + 1] = new Vector3(dir.x, dir.y, 0f);
                uvs[i + 1] = new Vector2(0.5f - 0.5f * dir.x, 0.5f + 0.5f * dir.y);
                normals[i + 1] = Vector3.forward;
            }
            var triangles = new int[segments * 3];
            for (int i = 0; i < segments; i++)
            {
                int current = 1 + i;
                int next = 1 + (i + 1) % segments;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = current;
                triangles[i * 3 + 2] = next;
            }
            _discMesh = new Mesh { name = "PhysicalScopeDisc" };
            _discMesh.vertices = vertices;
            _discMesh.uv = uvs;
            _discMesh.normals = normals;
            _discMesh.triangles = triangles;
            _discMesh.RecalculateBounds();
            return _discMesh;
        }
    }

    /// <summary>分划贴图（程序化，按档位缓存）：外粗柱 + 细十字 + 中心照明点；
    /// 高倍追加密位点列。512² 单张，CPU 一次性生成后置为不可读。</summary>
    internal static class ScopeReticleTexture
    {
        private static readonly Dictionary<OpticAimTier, Texture2D> Cache = new();

        internal static Texture2D Get(OpticAimTier tier)
        {
            if (Cache.TryGetValue(tier, out Texture2D cached) && cached != null) return cached;
            Texture2D texture = tier == OpticAimTier.HighZoom ? BuildHighPower() : BuildPrism();
            Cache[tier] = texture;
            return texture;
        }

        private static Texture2D BuildPrism()
        {
            const int size = 512;
            var pixels = new Color32[size * size];
            Color32 post = new(10, 10, 10, 235);
            Color32 thin = new(18, 18, 18, 215);
            Color32 lit = new(255, 96, 18, 255);
            float c = size * 0.5f;
            float gap = size * 0.012f;
            float postLen = size * 0.30f;
            // Preserve readable coverage even on the smallest formal aperture (P90).
            float thick = size * 0.022f;
            float thinWidth = size * 0.012f;
            // 三粗柱（左右下）+ 细十字 + V 字 chevron + 中心亮点
            DrawLine(pixels, size, new Vector2(0f, c), new Vector2(c - gap - postLen, c), thick, post);
            DrawLine(pixels, size, new Vector2(size, c), new Vector2(c + gap + postLen, c), thick, post);
            DrawLine(pixels, size, new Vector2(c, 0f), new Vector2(c, c - gap - postLen), thick, post);
            DrawLine(pixels, size, new Vector2(c - gap - postLen, c), new Vector2(c - gap, c), thinWidth, thin);
            DrawLine(pixels, size, new Vector2(c + gap + postLen, c), new Vector2(c + gap, c), thinWidth, thin);
            DrawLine(pixels, size, new Vector2(c, c - gap - postLen), new Vector2(c, c - gap), thinWidth, thin);
            DrawChevron(pixels, size, new Vector2(c, c), size * 0.030f, size * 0.010f, lit);
            for (int i = 1; i <= 4; i++)
            {
                float x = size * 0.040f * i;
                float half = size * (i % 2 == 0 ? 0.014f : 0.009f);
                DrawLine(pixels, size, new Vector2(c + x, c - half), new Vector2(c + x, c + half), thinWidth, thin);
                DrawLine(pixels, size, new Vector2(c - x, c - half), new Vector2(c - x, c + half), thinWidth, thin);
            }
            return Bake(pixels, size);
        }

        private static Texture2D BuildHighPower()
        {
            const int size = 512;
            var pixels = new Color32[size * size];
            Color32 post = new(8, 8, 8, 240);
            Color32 thin = new(14, 14, 14, 225);
            Color32 lit = new(235, 60, 16, 255);
            float c = size * 0.5f;
            float gap = size * 0.010f;
            float postLen = size * 0.34f;
            float thick = size * 0.006f;
            float thinWidth = size * 0.0020f;
            // 密位十字：四粗柱 + 细线接续 + 轴向密位点 + 中心亮点
            DrawLine(pixels, size, new Vector2(0f, c), new Vector2(c - gap - postLen, c), thick, post);
            DrawLine(pixels, size, new Vector2(size, c), new Vector2(c + gap + postLen, c), thick, post);
            DrawLine(pixels, size, new Vector2(c, size), new Vector2(c, c + gap + postLen), thick, post);
            DrawLine(pixels, size, new Vector2(c, 0f), new Vector2(c, c - gap - postLen), thick, post);
            DrawLine(pixels, size, new Vector2(c - gap - postLen, c), new Vector2(c - gap, c), thinWidth, thin);
            DrawLine(pixels, size, new Vector2(c + gap + postLen, c), new Vector2(c + gap, c), thinWidth, thin);
            DrawLine(pixels, size, new Vector2(c, c + gap + postLen), new Vector2(c, c + gap), thinWidth, thin);
            DrawLine(pixels, size, new Vector2(c, c - gap - postLen), new Vector2(c, c - gap), thinWidth, thin);
            for (int i = 1; i <= 4; i++)
            {
                float d = size * 0.034f * i;
                float dot = Mathf.Max(1.5f, size * 0.0028f);
                FillDisc(pixels, size, new Vector2(c + d, c), dot, thin);
                FillDisc(pixels, size, new Vector2(c - d, c), dot, thin);
                FillDisc(pixels, size, new Vector2(c, c + d), dot, thin);
                FillDisc(pixels, size, new Vector2(c, c - d), dot, thin);
            }
            FillDisc(pixels, size, new Vector2(c, c), Mathf.Max(1.6f, size * 0.0026f), lit);
            return Bake(pixels, size);
        }

        private static void DrawChevron(Color32[] pixels, int size, Vector2 tip, float span, float width, Color32 color)
        {
            DrawLine(pixels, size, tip, tip + new Vector2(-span, span * 0.7f), width, color);
            DrawLine(pixels, size, tip, tip + new Vector2(span, span * 0.7f), width, color);
        }

        private static Texture2D Bake(Color32[] pixels, int size)
        {
            // The physical aperture can be only tens of pixels wide. Mips preserve
            // coverage of fine markings instead of dropping lines between bilinear samples.
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = "PhysicalScopeReticle",
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>线段绘制（包围盒扫描 + 点到线段距离），背景透明；后画覆盖先画。</summary>
        private static void DrawLine(Color32[] pixels, int size, Vector2 a, Vector2 b, float width, Color32 color)
        {
            float minX = Mathf.Max(0f, Mathf.Min(a.x, b.x) - width);
            float maxX = Mathf.Min(size - 1, Mathf.Max(a.x, b.x) + width);
            float minY = Mathf.Max(0f, Mathf.Min(a.y, b.y) - width);
            float maxY = Mathf.Min(size - 1, Mathf.Max(a.y, b.y) + width);
            Vector2 delta = b - a;
            float lengthSq = Mathf.Max(1e-6f, delta.sqrMagnitude);
            for (int y = Mathf.FloorToInt(minY); y <= Mathf.CeilToInt(maxY); y++)
            {
                for (int x = Mathf.FloorToInt(minX); x <= Mathf.CeilToInt(maxX); x++)
                {
                    Vector2 p = new(x + 0.5f, y + 0.5f);
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, delta) / lengthSq);
                    float distance = Vector2.Distance(p, a + delta * t);
                    if (distance > width * 0.5f) continue;
                    pixels[y * size + x] = color;
                }
            }
        }

        private static void FillDisc(Color32[] pixels, int size, Vector2 center, float radius, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(center.x + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(center.y + radius));
            float radiusSq = radius * radius;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x + 0.5f - center.x;
                    float dy = y + 0.5f - center.y;
                    if (dx * dx + dy * dy <= radiusSq) pixels[y * size + x] = color;
                }
            }
        }
    }
}
