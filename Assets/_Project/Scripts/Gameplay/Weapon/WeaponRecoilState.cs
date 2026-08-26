using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// CP2 瞄准后坐弹簧（Docs/13 §5.3-1/3）：全工程唯一的相机后坐状态存在处——
    /// WeaponController 持有并驱动；FireRay（下一发弹道）与 CmFPCameraRecoil（视觉回声）
    /// 读同一个 CurrentOffset，恒等约束（相机中心射线 ≡ FireRay）由此成立。
    /// 纯 C#、dt 注入、可播种随机（网络/确定性预留，Docs/13 §5.3-8）。
    /// CP2 为行为等价迁移：参数（1.1°/0.3°/9Hz/ζ0.75）与弹簧/冲量公式原样来自旧
    /// CmFPCameraRecoil；CP4 起参数进 WeaponStat 数值管线（首枪冲量/累计/恢复延迟为 CP4 扩展）。
    /// </summary>
    public sealed class WeaponRecoilState
    {
        private readonly System.Random _random;

        /// <summary>当前瞄准偏移（度）：x=pitch（正=抬头），y=yaw（正=向左）。FireRay 与相机回声共用。</summary>
        public Vector2 CurrentOffset => _offset;

        private Vector2 _offset;
        private Vector2 _velocity;

        public WeaponRecoilState() : this(0) { }

        public WeaponRecoilState(int seed)
        {
            // seed=0 → 随机种子（默认玩法）；非 0 → 确定性（测试/网络回放）
            _random = seed == 0 ? new System.Random() : new System.Random(seed);
        }

        /// <summary>开火冲量（五步顺序第④步，Docs/13 §5.3-5）：作用于弹簧速度，影响下一发。</summary>
        public void OnShot(float pitchKickDegrees, float yawKickDegrees, float springFrequency)
        {
            // 冲量公式与旧 CmFPCameraRecoil.HandleShot 逐式一致：v0 += kick·ω
            // yaw 随机：均匀 [-yawKick, +yawKick]（可播种源替代 UnityEngine.Random）
            float yawKick = (float)(_random.NextDouble() * 2.0 - 1.0) * yawKickDegrees;
            _velocity += new Vector2(pitchKickDegrees, yawKick) * (springFrequency * Mathf.PI * 2f);
        }

        /// <summary>弹簧积分（欠阻尼回中带回弹）。dt≤0 不积分。每帧由 WeaponController 调用一次。</summary>
        public void Tick(float deltaTime, float springFrequency, float springDamping)
        {
            if (deltaTime <= 0f) return;
            float omega = springFrequency * Mathf.PI * 2f;
            float damping = 2f * springDamping * omega;
            _velocity += (-omega * omega * _offset - damping * _velocity) * deltaTime;
            _offset += _velocity * deltaTime;
        }

        /// <summary>硬重置：仅切枪/死亡/组件禁用（Docs/13 §5.3-4）。停火/换弹不调用——弹簧自然回零。</summary>
        public void HardReset()
        {
            _offset = Vector2.zero;
            _velocity = Vector2.zero;
        }

        /// <summary>当前偏移对应的旋转（CameraPivot 局部空间叠加用）。</summary>
        public Quaternion OffsetRotation => Quaternion.Euler(_offset.x, _offset.y, 0f);
    }
}
