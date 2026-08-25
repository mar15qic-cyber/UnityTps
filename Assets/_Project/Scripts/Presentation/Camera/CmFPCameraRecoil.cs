using Game.Gameplay.Weapon;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 相机 Recoil（CM3 扩展）：开火时相机上抬 + 随机水平偏移，弹簧回中。
    /// 只写 OrientationCorrection；因射击射线取自相机中心，视觉后座即影响下一发弹道
    /// （当前单人 V1 的取舍，联网版由服务器权威射线裁决）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraRecoil : CinemachineExtension
    {
        [SerializeField, Min(0f)] private float kickPitchDegrees = 1.1f;
        [SerializeField, Min(0f)] private float kickYawDegrees = 0.3f;
        [SerializeField, Min(0f)] private float springFrequency = 9f;
        [SerializeField, Range(0f, 1f)] private float dampingRatio = 0.75f;

        private WeaponController _weapon;
        private Vector2 _offset;
        private Vector2 _velocity;

        protected override void OnEnable()
        {
            base.OnEnable();
            _weapon = GetComponentInParent<WeaponController>();
            if (_weapon != null) _weapon.OnShotFired += HandleShot;
        }

        private void OnDisable()
        {
            if (_weapon != null) _weapon.OnShotFired -= HandleShot;
        }

        private void HandleShot(WeaponShot _) =>
            _velocity += new Vector2(kickPitchDegrees, Random.Range(-kickYawDegrees, kickYawDegrees))
                * (springFrequency * Mathf.PI * 2f);

        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage,
            ref CameraState state,
            float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Aim) return;

            if (deltaTime < 0f)
            {
                _offset = Vector2.zero;
                _velocity = Vector2.zero;
                return;
            }

            // 欠阻尼弹簧：带一点回弹的回中，手感近似真实枪口上跳
            float omega = springFrequency * Mathf.PI * 2f;
            float c = 2f * dampingRatio * omega;
            _velocity += (-omega * omega * _offset - c * _velocity) * deltaTime;
            _offset += _velocity * deltaTime;

            // 正 X 欧拉角 = 抬头：后座把镜头顶向上方，弹簧欠阻尼回中带轻微回弹
            state.OrientationCorrection *= Quaternion.Euler(_offset.x, _offset.y, 0f);
        }
    }
}
