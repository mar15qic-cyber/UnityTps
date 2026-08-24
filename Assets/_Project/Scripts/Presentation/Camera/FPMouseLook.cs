using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// FP 相机 Pitch（俯仰）唯一写者：Yaw 由 Locomotor 写身体，本组件只把
    /// 鼠标垂直增量应用到相机俯仰并夹紧。Day4 替换为 CinemachineCamera + 自定义组件。
    /// </summary>
    public sealed class FPMouseLook : MonoBehaviour
    {
        [SerializeField, Range(0.01f, 1f)] private float pitchSensitivity = 0.1f;
        [SerializeField] private float minPitch = -89f;
        [SerializeField] private float maxPitch = 89f;

        private Game.Gameplay.Player.InputReader _input;
        private float _pitch;

        private void Awake()
        {
            _input = GetComponentInParent<Game.Gameplay.Player.InputReader>();
        }

        private void LateUpdate()
        {
            if (_input == null) return;
            _pitch = Mathf.Clamp(_pitch - _input.LookDelta.y * pitchSensitivity, minPitch, maxPitch);
            transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }
}
