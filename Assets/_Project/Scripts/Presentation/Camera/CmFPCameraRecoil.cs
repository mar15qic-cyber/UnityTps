using Game.Gameplay.Weapon;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Camera recoil reads the gameplay aim debt and the per-weapon SO positional impulse.
    /// FireRay 与相机朝向消费同一个 Offset，恒等约束（相机中心射线 ≡ FireRay）由此成立；
    /// 表现层不再可能通过"清零/清状态"影响弹道（旧 deltaTime&lt;0 复位类问题整类消失）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraRecoil : CinemachineExtension
    {
        private WeaponController _weapon;
        private float _backKickMeters;

        protected override void OnEnable()
        {
            base.OnEnable();
            _weapon = GetComponentInParent<WeaponController>();
            if (_weapon != null) _weapon.OnShotFired += HandleShot;
        }

        private void OnDisable()
        {
            if (_weapon != null) _weapon.OnShotFired -= HandleShot;
            _backKickMeters = 0f;
        }

        private void HandleShot(WeaponShot shot)
        {
            // ShakePositionAmplitude is authored separately for every weapon in
            // Day2_DemoBalance. A bounded translation adds impact without desyncing
            // the center FireRay and the gameplay pitch/yaw recoil.
            float adsScale = Mathf.Lerp(1f, .65f, shot.Ads01);
            _backKickMeters = Mathf.Min(.055f,
                _backKickMeters + shot.Recoil.ShakeAmplitude * .045f * adsScale);
        }

        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage,
            ref CameraState state,
            float deltaTime)
        {
            if (_weapon == null)
            {
                _weapon = GetComponentInParent<WeaponController>();
                if (_weapon == null) return;
                _weapon.OnShotFired += HandleShot;
            }

            if (stage == CinemachineCore.Stage.Aim)
                state.OrientationCorrection *= _weapon.CurrentRecoilRotation;
            else if (stage == CinemachineCore.Stage.Finalize)
            {
                if (deltaTime < 0f) _backKickMeters = 0f;
                else _backKickMeters *= Mathf.Exp(-14f * deltaTime);
                state.PositionCorrection += state.RawOrientation * Vector3.back * _backKickMeters;
            }
        }
    }
}
