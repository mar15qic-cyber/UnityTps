using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// CP2 相机 Breathing（CM3 扩展，v3 阻断修正 1 后仅剩 Roll）：静止时的呼吸微倾。
    /// 位置通道（原 verticalAmplitude）已按 Docs/13 §5.3-2 迁移至 Viewmodel（FPWeaponMotion
    /// 既有 breathing 段承接观感）——相机任何位置修正都会使屏幕中心射线与 FireRay 平行不同线，
    /// 近墙/门框/掩体边缘出现准心与命中点错位。Roll 绕前向轴，不改变 Forward 与射线原点。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraBreathing : CinemachineExtension
    {
        [SerializeField, Min(0f)] private float cyclesPerSecond = 0.28f;
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

            // 仅 Roll（绕前向轴）：不改变 Forward 方向与相机位置 → 恒等约束不受影响
            float breath = Mathf.Sin(_time * cyclesPerSecond * Mathf.PI * 2f) * _weight;
            state.OrientationCorrection *= Quaternion.Euler(0f, 0f, breath * rollDegrees);
        }
    }
}
