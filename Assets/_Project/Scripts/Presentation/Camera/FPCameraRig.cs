using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 第一人称相机总管（表现层）。CP2 起 ADS 混合状态源上收为
    /// Gameplay 侧 PlayerAimState（Docs/13 §5.3-6：ADS 属于玩家，切枪不重建）；
    /// 本组件只读 Ads01 驱动世界相机 FOV（60 → Stat.AdsFov），并向 sway/bob/武器姿态等
    /// 表现组件转发只读 AdsBlend。不写 Gameplay 任何状态。
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class FPCameraRig : MonoBehaviour
    {
        [SerializeField] private CinemachineCamera cinemachineCamera;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField, Range(1f, 120f)] private float hipFov = 60f;

        /// <summary>ADS 混合值：0 = 腰射，1 = 完全瞄准（转发自 PlayerAimState，只读）。</summary>
        public float AdsBlend => aimState != null ? aimState.Ads01 : 0f;

        private WeaponController _weapon;

        private void Awake()
        {
            if (cinemachineCamera == null) cinemachineCamera = GetComponentInChildren<CinemachineCamera>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            _weapon = GetComponentInParent<WeaponController>();
        }

        private void Update()
        {
            if (cinemachineCamera == null) return;
            float adsFov = _weapon != null && _weapon.Stat.AdsFov > 1f ? _weapon.Stat.AdsFov : hipFov;
            var lens = cinemachineCamera.Lens;
            lens.FieldOfView = Mathf.Lerp(hipFov, adsFov, AdsBlend);
            cinemachineCamera.Lens = lens;
        }
    }
}
