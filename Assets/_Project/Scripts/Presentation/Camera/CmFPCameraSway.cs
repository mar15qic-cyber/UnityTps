using Game.Gameplay.Player;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 相机 Sway（CM3 扩展）：鼠标快速转向时相机产生轻微反向滞后旋转，
    /// 随 SmoothDamp 回中。只写 OrientationCorrection（与其它扩展乘法叠加），
    /// 不写 Raw 通道；ADS 时幅度收敛。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraSway : CinemachineExtension
    {
        [SerializeField, Min(0f)] private float yawDegreesPerPixel = 0.02f;
        [SerializeField, Min(0f)] private float pitchDegreesPerPixel = 0.014f;
        [SerializeField, Min(0f)] private float maxDegrees = 2.2f;
        [SerializeField, Min(0f)] private float smoothingSeconds = 0.09f;
        [SerializeField, Range(0f, 1f)] private float adsDamping = 0.7f;

        private InputReader _input;
        private FPCameraRig _rig;
        private Vector2 _smoothed;
        private Vector2 _velocity;

        protected override void OnEnable()
        {
            base.OnEnable();
            _input = GetComponentInParent<InputReader>();
            _rig = GetComponentInParent<FPCameraRig>();
        }

        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage,
            ref CameraState state,
            float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Aim) return;

            if (deltaTime < 0f)
            {
                _smoothed = Vector2.zero;
                _velocity = Vector2.zero;
                return;
            }

            Vector2 target = _input != null ? _input.LookDelta : Vector2.zero;
            // deltaTime<=0 已排除；扩展每帧只随 Brain 更新一次，此处直接消费当帧增量
            float adsScale = 1f - (_rig != null ? _rig.AdsBlend * adsDamping : 0f);
            _smoothed = smoothingSeconds <= 0f
                ? target
                : Vector2.SmoothDamp(_smoothed, target, ref _velocity, smoothingSeconds, Mathf.Infinity, Mathf.Max(deltaTime, 0.0001f));

            float yaw = Mathf.Clamp(-_smoothed.x * yawDegreesPerPixel, -maxDegrees, maxDegrees) * adsScale;
            float pitch = Mathf.Clamp(_smoothed.y * pitchDegreesPerPixel, -maxDegrees, maxDegrees) * adsScale;
            state.OrientationCorrection *= Quaternion.Euler(pitch, yaw, 0f);
        }
    }
}
