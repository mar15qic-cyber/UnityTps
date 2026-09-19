using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>
    /// ADS FOV 与开镜灵敏度缩放的共享公式（Gameplay 唯一权威）：
    /// PlayerAimState（FOV/灵敏度权威）每帧求值，FPCameraRig 只把结果应用到镜头，
    /// 避免镜头与灵敏度两处各自实现漂移。纯函数，EditMode 可测。
    /// </summary>
    public static class AdsFovMath
    {
        /// <summary>ADS 过渡中的当前 FOV：hip → ads 按 ads01 线性插值（与既有 FPCameraRig 行为一致）。</summary>
        public static float EvaluateCurrentFov(float hipFov, float adsFov, float ads01)
            => Mathf.Lerp(hipFov, adsFov, Mathf.Clamp01(ads01));

        /// <summary>
        /// 实体镜镜内视场（P4 I4b）：镜内 RT 相机/灵敏度基准的 FOV =
        /// 2·atan(tan(referenceFov/2)/mag)（计划公式 scopeFov = 2·atan(tan(worldFov/2)/mag)）。
        /// referenceFov 由调用方按口径给定：灵敏度 = 腰射 FOV（缩放后恰为 1/mag）；
        /// RT 相机 = 镜片在眼点处取代的世界视场（2·θL，项目派生几何）——穿透放大率恒等于 mag。
        /// mag ≤ 1 视为非变焦，原样返回。
        /// </summary>
        public static float ScopeFov(float referenceFovDeg, float magnification)
        {
            if (magnification <= 1.01f) return referenceFovDeg;
            float tanHalf = Mathf.Tan(Mathf.Clamp(referenceFovDeg, 1f, 150f) * 0.5f * Mathf.Deg2Rad) / magnification;
            return 2f * Mathf.Atan(tanHalf) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 开镜灵敏度倍率 = 焦距比（tan 半视角）：FOV 越小，同样鼠标增量转的角度越小，
        /// 屏幕空间手感与腰射一致（CS/Valorant "zoom sensitivity" 同款语义）。
        /// 上限 1（配置异常 currentFov>hipFov 时不变快），下限 0.05 防极端配置锁死。
        /// </summary>
        public static float SensitivityScale(float hipFov, float currentFov)
        {
            float hip = Mathf.Tan(Mathf.Max(1f, hipFov) * 0.5f * Mathf.Deg2Rad);
            float cur = Mathf.Tan(Mathf.Clamp(currentFov, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
            return hip > 1e-5f ? Mathf.Clamp(cur / hip, 0.05f, 1f) : 1f;
        }
    }
}
