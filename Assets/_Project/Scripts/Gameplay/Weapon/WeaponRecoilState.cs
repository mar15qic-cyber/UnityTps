using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 单发后坐结果。PitchKickDeg 为“屏幕向上”为正，YawKickDeg 为“屏幕向右”为正；
    /// 所有 Presentation 消费同一份结果，不重新随机或积分。
    /// </summary>
    public readonly struct ShotRecoilResult
    {
        public readonly float PitchKickDeg;
        public readonly float YawKickDeg;
        public readonly float ViewModelBackM;
        public readonly float ViewModelPitchDeg;
        public readonly float ShakeAmplitude;
        public readonly int ShotIndex;
        public readonly bool FirstShot;

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
    /// 唯一瞄准后坐状态。连续开火时保存“后坐债务”，停火超过 RecoveryDelay 后才恢复；
    /// 玩家反向输入可先消费债务，避免压枪后自动回中造成二次下坠。
    /// 纯 C#、dt 注入、可播种，FireRay 与 CmFPCameraRecoil 共用 CurrentOffset。
    /// </summary>
    public sealed class WeaponRecoilState
    {
        // 武器数据仍表达每发后坐；债务缩放是全局手感层，避免迁移旧资产后 30 发直接抬到天花板。
        internal const float AimDebtScale = 0.45f;
        internal const float MaxPitchDebtDeg = 12f;
        internal const float MaxYawDebtDeg = 4f;

        private readonly System.Random _random;

        public Vector2 CurrentOffset => _offset;
        public float BurstAccumulation { get; private set; }
        public int ShotIndex { get; private set; } = -1;
        public float TimeSinceLastShot { get; private set; }

        private Vector2 _offset;

        public WeaponRecoilState() : this(0) { }

        public WeaponRecoilState(int seed)
            => _random = seed == 0 ? new System.Random() : new System.Random(seed);

        public ShotRecoilResult OnShot(in WeaponFireContext ctx, in ResolvedWeaponStats s)
        {
            var r = s.Stat.Recoil;
            bool first = TimeSinceLastShot > r.RecoveryDelay || ShotIndex < 0;
            int shotIndex = first ? 0 : ShotIndex + 1;

            float kickScale = first
                ? s.EffectiveFirstShotMultiplier
                : Mathf.Lerp(1f, r.Accumulation,
                    Mathf.Clamp01((float)shotIndex / Mathf.Max(1f, r.MaxAccumulation)));
            float adsMul = Mathf.Lerp(1f, s.EffectiveAdsRecoilMultiplier, ctx.Ads01);

            float pitch = s.EffectiveRecoilPitchDeg * kickScale * adsMul;
            float yawMagnitude = s.EffectiveRecoilYawDeg * kickScale * adsMul;
            float yaw = (float)(_random.NextDouble() * 2.0 - 1.0) * yawMagnitude;

            var result = new ShotRecoilResult(
                pitch, yaw,
                s.EffectiveViewModelBack * kickScale,
                s.EffectiveViewModelPitchDeg * kickScale,
                r.ShakePositionAmplitude * kickScale,
                shotIndex, first);

            _offset = new Vector2(
                Mathf.Clamp(_offset.x + pitch * AimDebtScale, 0f, MaxPitchDebtDeg),
                Mathf.Clamp(_offset.y + yaw * AimDebtScale, -MaxYawDebtDeg, MaxYawDebtDeg));

            ShotIndex = shotIndex;
            TimeSinceLastShot = 0f;
            BurstAccumulation = Mathf.Min(BurstAccumulation + 1f, r.MaxAccumulation);
            return result;
        }

        /// <summary>
        /// 消费与当前后坐相反的玩家瞄准输入。输入单位为“Pitch 向上/Yaw 向右”的度数；
        /// 返回未被后坐债务消费的剩余输入，调用方再将其写入基础视角。
        /// </summary>
        public Vector2 ConsumeCompensation(Vector2 requestedAimDeltaDeg)
        {
            requestedAimDeltaDeg.x = ConsumeAxis(ref _offset.x, requestedAimDeltaDeg.x, 0f, MaxPitchDebtDeg);
            requestedAimDeltaDeg.y = ConsumeAxis(ref _offset.y, requestedAimDeltaDeg.y, -MaxYawDebtDeg, MaxYawDebtDeg);
            return requestedAimDeltaDeg;
        }

        private static float ConsumeAxis(ref float offset, float requested, float min, float max)
        {
            if (Mathf.Abs(offset) <= 0.0001f || offset * requested >= 0f)
                return requested;

            float consumed = Mathf.Min(Mathf.Abs(requested), Mathf.Abs(offset));
            float oldSign = Mathf.Sign(offset);
            offset = Mathf.Clamp(offset - oldSign * consumed, min, max);
            return requested + oldSign * consumed;
        }

        public void Tick(float deltaTime, in ResolvedWeaponStats s)
        {
            if (deltaTime <= 0f) return;
            var r = s.Stat.Recoil;
            TimeSinceLastShot += deltaTime;

            if (TimeSinceLastShot > r.RecoveryDelay)
            {
                float recovery = Mathf.Max(0f, r.RecoverySpeed) * deltaTime;
                _offset.x = Mathf.MoveTowards(_offset.x, 0f, recovery);
                _offset.y = Mathf.MoveTowards(_offset.y, 0f, recovery);
                if (BurstAccumulation > 0f)
                    BurstAccumulation = Mathf.Max(0f, BurstAccumulation - recovery);
            }
        }

        public void HardReset()
        {
            _offset = Vector2.zero;
            BurstAccumulation = 0f;
            ShotIndex = -1;
            TimeSinceLastShot = 0f;
        }

        /// <summary>把“上抬为正”的偏移转换为 Unity 局部旋转；Unity +X 欧拉角实际向下。</summary>
        public Quaternion OffsetRotation => Quaternion.Euler(-_offset.x, _offset.y, 0f);
    }
}
