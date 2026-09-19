using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>相机投影参数快照（值类型）。纯数学投影不依赖 UnityEngine.Camera 实例，
    /// EditMode 可直接构造测试；运行时用 From(Camera) 采集。</summary>
    public struct CameraProjection
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float FovDegrees;   // 垂直 FOV
        public float Aspect;       // width / height
        public float NearClip;
        public float FarClip;

        public static CameraProjection From(UnityEngine.Camera camera)
        {
            return new CameraProjection
            {
                Position = camera.transform.position,
                Rotation = camera.transform.rotation,
                FovDegrees = camera.fieldOfView,
                Aspect = camera.aspect,
                NearClip = camera.nearClipPlane,
                FarClip = camera.farClipPlane
            };
        }

        public float TanHalfFov => Mathf.Tan(Mathf.Max(0.1f, FovDegrees) * 0.5f * Mathf.Deg2Rad);

        public Vector3 Forward => Rotation * Vector3.forward;
        public Vector3 Up => Rotation * Vector3.up;
        public Vector3 Right => Rotation * Vector3.right;

        /// <summary>世界点 → 视口坐标（x/y∈[0,1]，z=沿前向深度；z&lt;=0 表示在相机背后，x/y 无意义）。</summary>
        public Vector3 ProjectToViewport(Vector3 worldPoint)
        {
            Vector3 local = Quaternion.Inverse(Rotation) * (worldPoint - Position);
            float tanHalf = TanHalfFov;
            float depth = local.z;
            if (Mathf.Abs(depth) < 1e-6f) depth = 1e-6f;
            float x = 0.5f + 0.5f * (local.x / (depth * tanHalf * Mathf.Max(1e-4f, Aspect)));
            float y = 0.5f + 0.5f * (local.y / (depth * tanHalf));
            return new Vector3(x, y, local.z);
        }

        /// <summary>视口坐标 → 世界方向（单位向量）。</summary>
        public Vector3 ViewportDirection(Vector2 viewport)
        {
            float tanHalf = TanHalfFov;
            Vector3 local = new Vector3(
                (viewport.x - 0.5f) * 2f * tanHalf * Mathf.Max(1e-4f, Aspect),
                (viewport.y - 0.5f) * 2f * tanHalf,
                1f);
            return (Rotation * local).normalized;
        }

        /// <summary>视口坐标 + 沿前向深度 → 世界点。深度必须 &gt; 0；方向近乎垂直于前向时退化为最近可用深度。</summary>
        public Vector3 ViewportPointAtDepth(Vector2 viewport, float depthAlongForward)
        {
            Vector3 direction = ViewportDirection(viewport);
            Vector3 forward = Forward;
            float along = Vector3.Dot(direction, forward);
            if (along < 1e-4f) along = 1e-4f;
            return Position + direction * (depthAlongForward / along);
        }

        /// <summary>跨相机屏幕匹配：求 overlay 系中的一个世界点，它经 overlay 相机投影后
        /// 落在与 sourceWorldPoint 经 source 相机投影相同的视口位置上（深度沿 overlay 前向，
        /// 夹在 [minDepth, maxDepth]）。曳光/分划跨 FOV 收敛的基础原语。</summary>
        public static bool TryMatchAcrossCameras(in CameraProjection source, in CameraProjection overlay,
            Vector3 sourceWorldPoint, float minDepth, float maxDepth, out Vector3 overlayWorldPoint)
        {
            overlayWorldPoint = default;
            Vector3 viewport = source.ProjectToViewport(sourceWorldPoint);
            if (viewport.z <= 0f) return false;
            float depth = Vector3.Dot(sourceWorldPoint - overlay.Position, overlay.Forward);
            if (depth < minDepth) depth = minDepth;
            if (depth > maxDepth) depth = maxDepth;
            overlayWorldPoint = overlay.ViewportPointAtDepth(new Vector2(viewport.x, viewport.y), depth);
            return IsFinite(overlayWorldPoint);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }

    /// <summary>挂点局部系（-X=前向/+Y=上）的光轴瞄准数据。由校准行解析，缺镜窗时用配件包围盒近似。</summary>
    public struct OpticAimLocal
    {
        public Vector3 EyePointLocal;
        public Vector3 AxisDirectionLocal;   // 单位向量，指向目标
        public Vector3 WindowCenterLocal;
        public float WindowHalfWidthMeters;
        public float WindowHalfHeightMeters;
        public bool HasWindow;
        public bool WindowIsApproximate;     // true=包围盒近似值（未标定），校准 UI 应提示
    }

    /// <summary>世界系光轴帧（每帧随挂点位姿求值；分划绑定与曳光匹配共用）。</summary>
    public struct OpticAimFrame
    {
        public Vector3 EyeWorld;
        public Vector3 AxisWorld;            // 单位向量，eye→target
        public Vector3 UpWorld;              // ⊥轴的滚转参考
        public Vector3 WindowCenterWorld;
        public bool HasWindow;
        public float WindowHalfWidthMeters;
        public float WindowHalfHeightMeters;
    }

    /// <summary>
    /// 光轴几何解析（ADS 审计 A1/A2 共用语义源）：把 AttachmentCalibration.OpticAimRows 的
    /// 挂点局部数据折算成世界系眼点/光轴/镜窗。FPWeaponMotion（姿态求解）、OpticAdsView（分划
    /// 绑定镜窗投影）与校准窗口（误差显示）都从本类取数，保证"分划-镜窗-弹着"同一套几何。
    /// </summary>
    public static class OpticAimGeometry
    {
        /// <summary>
        /// 光轴姿态解（纯数学，EditMode 可测）：把 (光轴, up) 帧对齐到父系 (前向 +Z, 上 +Y)。
        /// 返回枪根应有的父系局部旋转。固定点性质：应用后 axisRoot→+Z、upRoot→+Y，
        /// 与相机旋转/后坐无关（帧取逆，不含世界旋转项）。
        /// </summary>
        public static Quaternion SolveAimLocalRotation(Vector3 axisRoot, Vector3 upRoot)
        {
            if (axisRoot.sqrMagnitude < 0.5f || upRoot.sqrMagnitude < 0.5f)
                return Quaternion.identity;
            return Quaternion.Inverse(Quaternion.LookRotation(axisRoot, upRoot));
        }

        /// <summary>光轴平移解（纯数学）：使眼点（root 局部系）经 localRotation 旋转后落在 camParent
        /// （FP 相机在父系的位置）——眼距/构图由校准数据直接决定（ADS 审计 A1 的 z 修复）。</summary>
        public static Vector3 SolveAimLocalPosition(Quaternion localRotation, Vector3 eyeRoot, Vector3 camParent)
            => camParent - localRotation * eyeRoot;

        /// <summary>解析挂点局部系光轴数据。无校准行返回 false（调用方诚实降级）；
        /// 有校准行但未标定镜窗时用配件包围盒近似（WindowIsApproximate=true）。</summary>
        public static bool TryResolveLocal(WeaponController weapon, WeaponView view, out OpticAimLocal local)
        {
            local = default;
            if (weapon == null || view == null || weapon.Definition == null) return false;
            var ctx = weapon.CurrentOpticAim;
            if (ctx.ItemId == null || ctx.Tier == OpticAimTier.None) return false;
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var calibration = catalog != null ? catalog.Calibration : null;
            if (calibration == null) return false;
            if (!calibration.TryGetOpticAim(weapon.Definition.CatalogItemId, ctx.ItemId, out var data)) return false;
            var attachments = view.GetComponent<WeaponAttachmentView>();
            var socket = attachments != null ? attachments.GetSocketTransform(AttachmentSlotType.Optic) : null;
            if (socket == null) return false;

            local.EyePointLocal = data.EyePointLocal;
            local.AxisDirectionLocal = data.AxisDirectionLocal;
            if (data.HasWindow)
            {
                local.WindowCenterLocal = data.WindowCenterLocal;
                local.WindowHalfWidthMeters = data.WindowHalfWidthMeters;
                local.WindowHalfHeightMeters = data.WindowHalfHeightMeters;
                local.HasWindow = IsFiniteWindow(data.WindowHalfWidthMeters, data.WindowHalfHeightMeters);
                local.WindowIsApproximate = false;
            }
            if (!local.HasWindow && TryApproximateWindowFromRenderers(socket,
                    out var approxCenter, out var approxHalfW, out var approxHalfH))
            {
                local.WindowCenterLocal = approxCenter;
                local.WindowHalfWidthMeters = approxHalfW;
                local.WindowHalfHeightMeters = approxHalfH;
                local.HasWindow = true;
                local.WindowIsApproximate = true;
            }
            return IsFinite(local.EyePointLocal) && IsFinite(local.AxisDirectionLocal)
                && local.AxisDirectionLocal.sqrMagnitude > 0.5f;
        }

        /// <summary>每帧把局部数据折算成世界帧（挂点位姿驱动，无缓存、无反馈回路）。</summary>
        public static OpticAimFrame Evaluate(in OpticAimLocal local, Transform socket)
        {
            var frame = new OpticAimFrame
            {
                EyeWorld = socket.TransformPoint(local.EyePointLocal),
                AxisWorld = socket.TransformDirection(local.AxisDirectionLocal).normalized
            };
            Vector3 rawUp = socket.TransformDirection(Vector3.up);
            Vector3 up = Vector3.ProjectOnPlane(rawUp, frame.AxisWorld);
            frame.UpWorld = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            frame.HasWindow = local.HasWindow;
            frame.WindowHalfWidthMeters = local.WindowHalfWidthMeters;
            frame.WindowHalfHeightMeters = local.WindowHalfHeightMeters;
            if (local.HasWindow)
                frame.WindowCenterWorld = socket.TransformPoint(local.WindowCenterLocal);
            return frame;
        }

        /// <summary>
        /// 配件包围盒近似镜窗（未标定时的兜底，也是校准窗口"推导镜窗"按钮的实现）：
        /// 取挂点下 Att_* 配件全部 Renderer 的网格 OBB 角点折回挂点局部系求 AABB。
        /// 全模型包围盒含镜座（在窗下方），故中心沿挂点上移 12% 高度、半宽/半高取 30% 作为初值，
        /// 校准窗口可再微调写入正式值。
        /// </summary>
        public static bool TryApproximateWindowFromRenderers(Transform socket,
            out Vector3 centerLocal, out float halfWidth, out float halfHeight)
        {
            centerLocal = Vector3.zero;
            halfWidth = halfHeight = 0f;
            if (socket == null) return false;

            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (Transform child in socket)
            {
                if (!child.name.StartsWith("Att_")) continue;
                foreach (var renderer in child.GetComponentsInChildren<Renderer>())
                {
                    var mesh = renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
                    if (mesh == null) continue;
                    Bounds local = mesh.bounds;
                    for (int cx = 0; cx < 2; cx++)
                    for (int cy = 0; cy < 2; cy++)
                    for (int cz = 0; cz < 2; cz++)
                    {
                        var corner = renderer.transform.TransformPoint(new Vector3(
                            cx == 0 ? local.min.x : local.max.x,
                            cy == 0 ? local.min.y : local.max.y,
                            cz == 0 ? local.min.z : local.max.z));
                        var socketLocal = socket.InverseTransformPoint(corner);
                        lo = Vector3.Min(lo, socketLocal);
                        hi = Vector3.Max(hi, socketLocal);
                        any = true;
                    }
                }
            }
            if (!any) return false;

            Vector3 size = hi - lo;
            centerLocal = new Vector3((lo.x + hi.x) * 0.5f, (lo.y + hi.y) * 0.5f + size.y * 0.12f, (lo.z + hi.z) * 0.5f);
            halfWidth = size.x * 0.30f;
            halfHeight = size.y * 0.30f;
            return halfWidth > 0.001f && halfHeight > 0.001f;
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        private static bool IsFiniteWindow(float w, float h) => float.IsFinite(w) && float.IsFinite(h) && w > 0f && h > 0f;
    }
}
