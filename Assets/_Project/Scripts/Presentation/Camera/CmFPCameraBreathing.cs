using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 相机 Breathing（CM3 扩展）：静止时的呼吸微动（缓慢正弦），
    /// 仅 Idle 权重生效，移动即平滑淡出。只写 Position/Orientation Correction。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraBreathing : CinemachineExtension
    {
        [SerializeField, Min(0f)] private float cyclesPerSecond = 0.28f;
        [SerializeField, Min(0f)] private float verticalAmplitude = 0.006f;
        [SerializeField, Min(0f)] private float rollDegrees = 0.1f;
        [SerializeField, Min(0f)] private float settleSeconds = 0.4f;

        private PlayerStateView _state;
        private float _weight;
        private float _weightVelocity;
        private float _time;

        protected override void OnEnable()
        {
            base.OnEnable();
            _state = GetComponentInParent<PlayerStateView>();
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
                _weight = 0f;
                _weightVelocity = 0f;
                _time = 0f;
                return;
            }

            _time += deltaTime;
            bool idle = _state != null && _state.LocomotionState == LocomotionState.Idle;
            _weight = Mathf.SmoothDamp(_weight, idle ? 1f : 0f, ref _weightVelocity, settleSeconds, Mathf.Infinity, Mathf.Max(deltaTime, 0.0001f));
            if (_weight <= 0.001f) return;

            float breath = Mathf.Sin(_time * cyclesPerSecond * Mathf.PI * 2f) * _weight;
            var localOffset = new Vector3(0f, breath * verticalAmplitude, 0f);
            state.PositionCorrection += state.RawOrientation * localOffset;
            state.OrientationCorrection *= Quaternion.Euler(0f, 0f, breath * rollDegrees);
        }
    }
}
