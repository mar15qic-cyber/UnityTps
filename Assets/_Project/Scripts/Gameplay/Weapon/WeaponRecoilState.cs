using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 单发后坐结果（Docs/13 §5.2 v3）：五步顺序第④步产出、第⑤步随 WeaponShot 广播——
    /// 全部 Presentation（相机回声/Viewmodel/Shake）消费同一份，结构上不可能重复计算。
    /// PitchKick/YawKick 已含首枪倍率×累计缩放×ADS 倍率×AimRecoil 修饰（同源作用弹道与相机）。
    /// </summary>
    public readonly struct ShotRecoilResult
    {
        public readonly float PitchKickDeg;         // 本发瞄准后坐冲量（度）
        public readonly float YawKickDeg;           // 水平冲量（度，含可播种随机符号）
        public readonly float ViewModelBackM;       // Viewmodel 视觉后移（米）
        public readonly float ViewModelPitchDeg;    // Viewmodel 视觉上抬（度）
        public readonly float ShakeAmplitude;       // 纯手感位置脉冲（0..1，仅位置/Roll）
        public readonly int ShotIndex;              // burst 内 0 基序号
        public readonly bool FirstShot;             // 新 burst 首发

        public ShotRecoilResult(float pitchKickDeg, float yawKickDeg,
            float viewModelBackM, float viewModelPitchDeg, float shakeAmplitude,
            int shotIndex, bool firstShot)
        {
            PitchKickDeg = pitchKickDeg;
            YawKickDeg = yawKickDeg;
            ViewModelBackM = viewModelBackM;
            ViewModelPitchDeg = viewModelPitchDeg;
            ShakeAmplitude = shakeAmplitude;
            ShotIndex = shotIndex;
            FirstShot = firstShot;
        }
    }

    /// <summary>
    /// 瞄准后坐状态（Docs/13 §5.3-1/3/4）：全工程唯一的相机后坐弹簧存在处——FireRay 与
    /// CmFPCameraRecoil（回声）读同一个 CurrentOffset。
    /// 分层 API：ApplyImpulse/TickSpring 为低级原语（弹簧等价测试锚点）；
    /// OnShot(ctx,stats)/Tick(dt,stats) 为高级组合（首枪判定/Burst 累计/恢复门控，CP4）。
    /// 两套恢复状态独立（§5.3-4 v3）：BurstAccumulation 受 RecoveryDelay 门控按发/秒衰减；
    /// CurrentOffset 弹簧持续积分回零、不受门控。
    /// 纯 C#、dt 注入、可播种（seed=0 随机；非 0 确定性——测试/网络回放）。
    /// </summary>
    public sealed class WeaponRecoilState
    {
        private readonly System.Random _random;

        public Vector2 CurrentOffset => _offset;
        /// <summary>Burst 累计（发数等效，≤MaxAccumulation）；开火取"本发累计级"，发后增长。</summary>
        public float BurstAccumulation { get; private set; }
        /// <summary>当前 burst 已发数（首发前为 -1 表示无 burst）。</summary>
        public int ShotIndex { get; private set; } = -1;
        /// <summary>距上一发秒数（首枪判定与 Burst 衰减门控共用 RecoveryDelay，§5.3-4 v3）。</summary>
        public float TimeSinceLastShot { get; private set; }

        private Vector2 _offset;
        private Vector2 _velocity;

        public WeaponRecoilState() : this(0) { }

        public WeaponRecoilState(int seed)
            => _random = seed == 0 ? new System.Random() : new System.Random(seed);

        // ---------------- 高级组合（WeaponController 五步顺序调用） ----------------

        /// <summary>
        /// 第④步：算本发完整后坐（首枪×累计×ADS×修饰）并施加弹簧冲量。
        /// 累计模型：kickScale = 首发取 FirstShotMultiplier，否则 Lerp(1, Accumulation, shotIndex/MaxAccumulation)
        /// ——Accumulation 即"连射末端（达到 MaxAccumulation 发时）的放大倍数"，1=不放大。
        /// </summary>
        public ShotRecoilResult OnShot(in WeaponFireContext ctx, in ResolvedWeaponStats s)
        {
            var r = s.Stat.Recoil;
            bool first = TimeSinceLastShot > r.RecoveryDelay || ShotIndex < 0;
            int shotIndex = first ? 0 : ShotIndex + 1;

            float kickScale = first
                ? s.EffectiveFirstShotMultiplier
                : Mathf.Lerp(1f, r.Accumulation, Mathf.Clamp01((float)shotIndex / Mathf.Max(1f, r.MaxAccumulation)));
            float adsMul = Mathf.Lerp(1f, s.EffectiveAdsRecoilMultiplier, ctx.Ads01);

            float pitch = s.EffectiveRecoilPitchDeg * kickScale * adsMul;
            float yawMag = s.EffectiveRecoilYawDeg * kickScale * adsMul;
            // 水平随机：均匀 [-mag, +mag]（可播种源）
            float yaw = (float)(_random.NextDouble() * 2.0 - 1.0) * yawMag;

            var result = new ShotRecoilResult(
                pitch, yaw,
                s.EffectiveViewModelBack * kickScale,
                s.EffectiveViewModelPitchDeg * kickScale,
                r.ShakePositionAmplitude * kickScale,
                shotIndex, first);

            // 冲量（v0 += kick·ω，公式与旧 CmFPCameraRecoil 逐式一致）
            _velocity += new Vector2(pitch, yaw) * (r.SpringFrequency * Mathf.PI * 2f);

            ShotIndex = shotIndex;
            TimeSinceLastShot = 0f;
            BurstAccumulation = Mathf.Min(BurstAccumulation + 1f, r.MaxAccumulation);
            return result;
        }

        /// <summary>每帧：弹簧积分（持续）+ 停火门控后的 Burst 衰减（按发/秒）。</summary>
        public void Tick(float deltaTime, in ResolvedWeaponStats s)
        {
            if (deltaTime <= 0f) return;
            var r = s.Stat.Recoil;
            TickSpring(deltaTime, r.SpringFrequency, r.SpringDamping);

            TimeSinceLastShot += deltaTime;
            if (TimeSinceLastShot > r.RecoveryDelay && BurstAccumulation > 0f)
                BurstAccumulation = Mathf.Max(0f, BurstAccumulation - s.EffectiveRecoverySpeed * deltaTime);
        }

        /// <summary>硬重置：仅切枪/死亡/组件禁用（§5.3-4）。停火/换弹不调用——各自自然恢复。</summary>
        public void HardReset()
        {
            _offset = Vector2.zero;
            _velocity = Vector2.zero;
            BurstAccumulation = 0f;
            ShotIndex = -1;
            TimeSinceLastShot = 0f;
        }

        /// <summary>当前偏移对应旋转（CameraPivot 局部空间叠加用）。</summary>
        public Quaternion OffsetRotation => Quaternion.Euler(_offset.x, _offset.y, 0f);

        // ---------------- 低级原语（弹簧等价测试锚点；运行时走高级组合） ----------------

        /// <summary>直接施加冲量（度；测试/等价断言用）。</summary>
        internal void ApplyImpulse(float pitchKickDegrees, float yawKickDegrees, float springFrequency)
            => _velocity += new Vector2(pitchKickDegrees, yawKickDegrees) * (springFrequency * Mathf.PI * 2f);

        /// <summary>仅弹簧积分（dt≤0 no-op；测试/等价断言用）。</summary>
        internal void TickSpring(float deltaTime, float springFrequency, float springDamping)
        {
            if (deltaTime <= 0f) return;
            float omega = springFrequency * Mathf.PI * 2f;
            float damping = 2f * springDamping * omega;
            _velocity += (-omega * omega * _offset - damping * _velocity) * deltaTime;
            _offset += _velocity * deltaTime;
        }
    }
}
