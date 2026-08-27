using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// FP 相机 Pitch（俯仰）唯一写者：Yaw 由 Locomotor 写身体，本组件只把
    /// 鼠标垂直增量应用到相机俯仰并夹紧。挂在 CameraPivot 上，CinemachineCamera
    /// Follow 该节点（HardLock + RotateWithFollowTarget），Main Camera 由 Brain 驱动。
    /// 执行顺序需早于 CinemachineBrain（默认 0）的 LateUpdate，避免俯仰滞后一帧。
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public sealed class FPMouseLook : MonoBehaviour
    {
        [SerializeField, Range(0.01f, 1f)] private float pitchSensitivity = 0.1f;
        [SerializeField] private float minPitch = -89f;
        [SerializeField] private float maxPitch = 89f;

        private Game.Gameplay.Player.InputReader _input;
        private WeaponController _weapon;
        private float _pitch;

        private void Awake()
        {
            _input = GetComponentInParent<Game.Gameplay.Player.InputReader>();
            _weapon = GetComponentInParent<WeaponController>();
        }

        private void LateUpdate()
        {
            if (_input == null) return;
            // LookDelta.y>0 表示鼠标向上，语义为“基础 Pitch 向上”。后坐债务先消费
            // 反向输入，只有剩余输入才修改基础俯仰，避免停火恢复把已压住的视角再弹下去。
            float pitchUpDelta = _input.LookDelta.y * pitchSensitivity;
            if (_weapon != null)
                pitchUpDelta = _weapon.ConsumeRecoilCompensation(new Vector2(pitchUpDelta, 0f)).x;
            _pitch = Mathf.Clamp(_pitch - pitchUpDelta, minPitch, maxPitch);
            transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }
}
