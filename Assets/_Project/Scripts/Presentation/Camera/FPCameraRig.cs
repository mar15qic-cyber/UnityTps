using Game.Gameplay.Action;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 第一人称相机总管（表现层）：唯一的 ADS 混合状态源。
    /// 职责：1) 由 InputReader.AimHeld 与 ActionSystem 槽位状态推导 ADS 混合值；
    /// 2) 把 WeaponStat.AdsFov（Lua 数值）应用到 CinemachineCamera.Lens（世界相机变焦，
    ///    FP View Camera（overlay 45°）不变，viewmodel 不随 FOV 拉伸）；
    /// 3) 向 sway/bob/武器姿态等表现组件公开 AdsBlend（只读）。
    /// 不写 Gameplay 任何状态；换弹/切枪动作期间自动松开 ADS。
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class FPCameraRig : MonoBehaviour
    {
        [SerializeField] private CinemachineCamera cinemachineCamera;
        [SerializeField, Range(1f, 120f)] private float hipFov = 60f;
        [SerializeField, Min(0.01f)] private float adsTransitionSeconds = 0.16f;

        /// <summary>ADS 混合值：0 = 腰射，1 = 完全瞄准。其它表现组件只读。</summary>
        public float AdsBlend { get; private set; }

        private InputReader _input;
        private WeaponController _weapon;
        private ActionSystem _actions;

        private void Awake()
        {
            if (cinemachineCamera == null) cinemachineCamera = GetComponentInChildren<CinemachineCamera>();
            _input = GetComponentInParent<InputReader>();
            _weapon = GetComponentInParent<WeaponController>();
            _actions = GetComponentInParent<ActionSystem>();
        }

        private void Update()
        {
            // 换弹/切枪占用上半身动作槽时强制收镜（表现决策，不改 Gameplay 真相）
            bool wantsAim = _input != null && _input.AimHeld;
            bool actionFree = _actions == null || !_actions.IsBusy;
            float target = wantsAim && actionFree ? 1f : 0f;
            float speed = adsTransitionSeconds <= 0f
                ? Mathf.Infinity
                : Time.deltaTime / adsTransitionSeconds;
            AdsBlend = Mathf.MoveTowards(AdsBlend, target, speed);

            if (cinemachineCamera == null) return;
            float adsFov = _weapon != null && _weapon.Stat.AdsFov > 1f ? _weapon.Stat.AdsFov : hipFov;
            var lens = cinemachineCamera.Lens;
            lens.FieldOfView = Mathf.Lerp(hipFov, adsFov, AdsBlend);
            cinemachineCamera.Lens = lens;
        }
    }
}
