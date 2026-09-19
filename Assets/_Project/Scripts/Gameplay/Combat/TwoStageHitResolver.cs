using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// P4 两段权威命中（Docs/26 §5，I4a + R10 审计修复）纯决策核心：
    /// ① 相机候选：可信相机视点沿实际射击方向 → 候选命中点；
    /// ② 枪口验证：服务器逻辑枪口向候选点遮挡检测——候选点前被遮挡 → 改用枪口路径最近命中；
    /// ③ 身体锚点防伸墙（R10 语义修正）：身体锚点到逻辑枪口被墙体阻断（枪口伸进墙体）时
    ///    【不豁免枪口验证】——枪口被墙吞没，相机候选不可信；采纳身体→枪口段的可信侧遮挡命中
    ///    （打在眼前墙上或段内目标），绝不放行"相机可见而武器被近墙挡住"的目标。
    /// 距离比较（R10 语义修正）：枪口路径命中距离与候选点距离【同源于枪口】（旧实现拿"枪口起量"
    /// 与"相机起量"直接比较，0.05m 容差没有共同原点）。
    /// 判定与射线执行解耦（Decide 纯函数 EditMode 可测）；伤害只对最终结果应用一次（CombatResolver 负责）。
    /// 逻辑枪口/身体锚点由服务器玩家姿态推导（武器配置与权威附件的精确推导后续票据化）。
    /// </summary>
    public static class TwoStageHitResolver
    {
        /// <summary>枪口路径命中点与候选点视为同点的容差（米；两段同以枪口为原点度量）。</summary>
        public const float OcclusionEpsilon = 0.05f;

        /// <summary>逻辑枪口相对玩家根的偏移（上 1.4m / 前 0.6m——服务器姿态推导近似）。</summary>
        public const float MuzzleUpMeters = 1.4f;
        public const float MuzzleForwardMeters = 0.6f;
        /// <summary>身体侧可信锚点高度（胸口）。</summary>
        public const float BodyAnchorUpMeters = 1.0f;

        public enum TwoStageDecision
        {
            /// <summary>采纳相机候选（含无遮挡/同点两种情形）。</summary>
            UseCameraCandidate,
            /// <summary>枪口路径在候选点前被遮挡 → 枪口路径最近命中。</summary>
            UseMuzzleHit,
            /// <summary>枪口伸进墙体（身体→枪口受阻）→ 身体段的可信侧遮挡命中（R10：不再豁免）。</summary>
            UseBodySegmentHit,
        }

        /// <summary>
        /// 两段判定（纯函数）：candidateDistanceFromMuzzle = 候选点到【枪口】的距离（相机命中时为
        /// 枪口→候选命中点，未命中时为枪口→相机远点）；muzzleInsideWall = 身体锚点→枪口被遮挡；
        /// muzzleHit/muzzleDist = 枪口向候选点路径是否被遮挡及距离（枪口起量）。
        /// </summary>
        public static TwoStageDecision Decide(bool cameraHit, float candidateDistanceFromMuzzle,
            bool muzzleInsideWall, bool muzzleHit, float muzzleDist)
        {
            if (muzzleInsideWall)
                return TwoStageDecision.UseBodySegmentHit;
            float candidateDistance = Mathf.Max(0f, candidateDistanceFromMuzzle);
            if (muzzleHit && muzzleDist < candidateDistance - OcclusionEpsilon)
                return TwoStageDecision.UseMuzzleHit;
            return TwoStageDecision.UseCameraCandidate;
        }

        /// <summary>服务器逻辑枪口位置（玩家根姿态推导）。</summary>
        public static Vector3 LogicalMuzzle(Vector3 rootPosition, Vector3 rootForward, Vector3 rootUp)
            => rootPosition + rootUp * MuzzleUpMeters + rootForward * MuzzleForwardMeters;

        /// <summary>身体侧可信锚点（胸口）。</summary>
        public static Vector3 BodyAnchor(Vector3 rootPosition) => rootPosition + Vector3.up * BodyAnchorUpMeters;
    }
}
