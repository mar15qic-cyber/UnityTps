using Game.Gameplay.Weapon;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// CP2 相机后坐回声（Docs/13 §5.3-1/3）：无状态、无参数、无随机——每帧只读
    /// WeaponController.CurrentRecoilOffset（Gameplay 弹簧唯一真相）写入 OrientationCorrection。
    /// FireRay 与相机朝向消费同一个 Offset，恒等约束（相机中心射线 ≡ FireRay）由此成立；
    /// 表现层不再可能通过"清零/清状态"影响弹道（旧 deltaTime&lt;0 复位类问题整类消失）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CmFPCameraRecoil : CinemachineExtension
    {
        private WeaponController _weapon;

        protected override void OnEnable()
        {
            base.OnEnable();
            _weapon = GetComponentInParent<WeaponController>();
        }

        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage,
            ref CameraState state,
            float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Aim) return;
            if (_weapon == null)
            {
                _weapon = GetComponentInParent<WeaponController>();
                if (_weapon == null) return;
            }

            // 回声：与 WeaponController.AimDirection 完全同源的偏移（x=pitch 正=抬头，y=yaw）
            var offset = _weapon.CurrentRecoilOffset;
            state.OrientationCorrection *= Quaternion.Euler(offset.x, offset.y, 0f);
        }
    }
}
