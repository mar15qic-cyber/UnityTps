using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 相机 Bob（CM3 扩展）：步态上下起伏 + 左右摆动。相位直接取 Locomotor 的
    /// 确定性 GaitPhase（与 TP 动画脚步同源，不另设计时器，避免双频跳变）；
    /// 空中/落地状态权重归零，ADS 时幅度收敛。只写 PositionCorrection。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraBob : CinemachineExtension
    {
        [SerializeField, Min(0f)] private float verticalAmplitude = 0.028f;
        [SerializeField, Min(0f)] private float lateralAmplitude = 0.018f;
        [SerializeField, Min(0.01f)] private float referenceSpeed = 3.44f;
        [SerializeField, Min(0f)] private float settleSeconds = 0.15f;
        [SerializeField, Range(0f, 1f)] private float adsDamping = 0.75f;

        private PlayerStateView _state;
        private FPCameraRig _rig;
        private float _weight;
        private float _weightVelocity;

        protected override void OnEnable()
        {
            base.OnEnable();
            _state = GetComponentInParent<PlayerStateView>();
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
                _weight = 0f;
                _weightVelocity = 0f;
                return;
            }

            bool groundedMove = _state != null
                && (_state.LocomotionState == LocomotionState.Walk || _state.LocomotionState == LocomotionState.Sprint);
            float speedScale = _state != null ? Mathf.Clamp01(_state.HorizontalSpeed / referenceSpeed) : 0f;
            float targetWeight = groundedMove ? speedScale : 0f;
            _weight = Mathf.SmoothDamp(_weight, targetWeight, ref _weightVelocity, settleSeconds, Mathf.Infinity, Mathf.Max(deltaTime, 0.0001f));

            if (_weight <= 0.001f) return;

            float phase = _state != null ? _state.GaitPhase * Mathf.PI * 2f : 0f;
            float adsScale = 1f - (_rig != null ? _rig.AdsBlend * adsDamping : 0f);
            float scale = _weight * adsScale;

            // 一个步态周期 = 左右各一步：垂直 2 频、侧向 1 频；偏移为相机局部空间
            Vector3 localOffset = new Vector3(
                Mathf.Sin(phase) * lateralAmplitude * scale,
                Mathf.Abs(Mathf.Cos(phase)) * verticalAmplitude * scale - verticalAmplitude * 0.5f * scale,
                0f);
            state.PositionCorrection += state.RawOrientation * localOffset;
        }
    }
}
