using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 实体镜镜片几何的纯函数（P4 I4b，EditMode 可测）：从挂点局部系（-X=前向/+Y=上）的
    /// 瞄具包围盒 + 眼点校准推导镜片位姿。镜片 = 后镜圈端面处、以眼光轴为轴心的圆盘——
    /// 位置/尺寸全部由项目内瞄具模型与校准数据派生（项目派生镜片），不硬编码任何枪/镜组合。
    /// 输入异常（空包围盒/眼点脱出光轴截面）诚实返回 false，调用方降级既有 overlay 路径。
    /// </summary>
    public static class OpticLensMath
    {
        /// <summary>镜片盘相对后镜圈端面（bounds.max.x）向眼侧（+X）的外移（米）：避让瞄具模型自身玻璃面。</summary>
        public const float GlassSurfaceOffset = 0.0015f;

        /// <summary>镜片半径钳制（米）：瞄具横向包围盒推算值超出即视为几何异常。</summary>
        public const float MinLensRadius = 0.006f;
        public const float MaxLensRadius = 0.045f;

        /// <summary>镜片半视角钳制（度）：距眼过近/过远时防止镜内 FOV 发散。</summary>
        public const float MinLensHalfAngleDeg = 3f;
        public const float MaxLensHalfAngleDeg = 45f;

        /// <summary>
        /// 推导镜片帧。lensPositionLocal：挂点局部系镜片圆心（X=后镜圈端面+GlassSurfaceOffset，
        /// Y/Z=眼点光轴）；lensRadius：由横向（Z）包围盒推算的镜片半径。
        /// 校验：包围盒有效、眼点 Y/Z 落在光轴截面附近（半径 1.5 倍内）、眼点在端面后 0.5m 内。
        /// </summary>
        public static bool TryDeriveLensFrame(Bounds socketSpaceBounds, Vector3 eyePointLocal,
            out Vector3 lensPositionLocal, out float lensRadius)
        {
            lensPositionLocal = default;
            lensRadius = 0f;
            Vector3 size = socketSpaceBounds.size;
            if (size.x < 0.004f || size.z < 0.004f || !IsFinite(size)) return false;

            float radius = Mathf.Clamp(size.z * 0.45f, MinLensRadius, MaxLensRadius);
            float lateralLimit = radius * 1.5f;
            if (Mathf.Abs(eyePointLocal.y - socketSpaceBounds.center.y) > lateralLimit
                || Mathf.Abs(eyePointLocal.z - socketSpaceBounds.center.z) > lateralLimit) return false;
            if (eyePointLocal.x < socketSpaceBounds.max.x - 0.05f
                || eyePointLocal.x > socketSpaceBounds.max.x + 0.5f) return false;

            lensPositionLocal = new Vector3(
                socketSpaceBounds.max.x + GlassSurfaceOffset,
                eyePointLocal.y,
                eyePointLocal.z);
            lensRadius = radius;
            return IsFinite(lensPositionLocal) && float.IsFinite(lensRadius) && lensRadius > 0f;
        }

        /// <summary>镜片在眼点处的半视角（度）= atan(半径/垂直距眼)。距眼 ≤0 视为未入瞳，返回上限。</summary>
        public static float LensHalfAngleDegrees(float eyeDistance, float lensRadius)
        {
            if (!float.IsFinite(eyeDistance) || !float.IsFinite(lensRadius) || lensRadius <= 0f)
                return MaxLensHalfAngleDeg;
            if (eyeDistance <= 0.0005f) return MaxLensHalfAngleDeg;
            float halfAngle = Mathf.Atan(lensRadius / eyeDistance) * Mathf.Rad2Deg;
            return Mathf.Clamp(halfAngle, MinLensHalfAngleDeg, MaxLensHalfAngleDeg);
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
