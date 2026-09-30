using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>
    /// CP2 瞄准权威（Docs/13 §5.3-6）：ADS 状态属于玩家而非武器——由输入与动作槽推导 Ads01，
    /// 是全工程唯一的 ADS 混合状态源。FPCameraRig（FOV/表现收敛）、未来的 WeaponFireContext
    /// （Spread/Recoil 情境倍率）与准心 HUD 都只读本组件。切枪不重建（换武器不重置混合）。
    /// 原 FPCameraRig 的推导逻辑（AimHeld && !ActionSystem.IsBusy）原样上收，行为等价。
    /// 2026-09-03 起 FOV/开镜灵敏度同步上收：CurrentFov（含瞄具分档覆盖）与
    /// LookSensitivityScale 由本组件按 AdsFovMath 求值，FPCameraRig 只应用到镜头、
    /// InputReader 只在源头乘倍率（所有视角消费者自动一致，含网络预测回放）。
    /// </summary>
    [DefaultExecutionOrder(-80)] // 晚于 ActionSystem(-100)/早于 WeaponController(-50)
    public sealed class PlayerAimState : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] private float adsTransitionSeconds = 0.16f;
        [SerializeField, Range(30f, 120f), Tooltip("腰射 FOV（灵敏度倍率基准；FPCameraRig 的镜头值来源）")]
        private float hipFov = 60f;

        /// <summary>ADS 混合值：0 = 腰射，1 = 完全瞄准。表现与数值消费者只读。</summary>
        public float Ads01 { get; private set; }

        /// <summary>ADS 过渡窗时长（秒）。FPWeaponAnimator 用它做 aim clip 播速适配（Docs/18 §4.2）。</summary>
        public float AdsTransitionSeconds => adsTransitionSeconds;

        /// <summary>当前渲染 FOV（度）：hip → 有效 ADS FOV（武器 Stat.AdsFov 经瞄具分档覆盖）按 Ads01 插值。</summary>
        public float CurrentFov { get; private set; } = 60f;

        /// <summary>开镜灵敏度倍率（焦距比）：InputReader 在源头乘到 LookDelta，1 = 与腰射同速。</summary>
        public float LookSensitivityScale { get; private set; } = 1f;

        /// <summary>调试/校准覆盖（null=输入驱动；仅编辑器校准窗口用）：强制 Ads01，FOV/灵敏度随之推导。</summary>
        public float? DebugAdsOverride { get; set; }

        private InputReader _input;
        private ActionSystem _actions;
        private WeaponController _weapon;
        private bool? _remoteAimIntent;
        private readonly Game.Gameplay.Network.CombatAimTimeline _timeline = new();
        private bool? _timelineTarget;
        internal float AdsAt(double seconds) => _timeline.Evaluate(seconds, adsTransitionSeconds);

        public void SetRemoteAimIntent(bool wantsAim) => _remoteAimIntent = wantsAim;
        public void ClearRemoteAimIntent() => _remoteAimIntent = null;

        private void Awake()
        {
            _input = GetComponentInParent<InputReader>();
            _actions = GetComponentInParent<ActionSystem>();
            _weapon = GetComponentInParent<WeaponController>();
            CurrentFov = hipFov;
        }

        private void OnEnable()
        {
            if (_input == null) _input = GetComponentInParent<InputReader>();
            if (_actions == null) _actions = GetComponentInParent<ActionSystem>();
            if (_weapon == null) _weapon = GetComponentInParent<WeaponController>();
        }

        private void Update()
        {
            // 换弹/切枪占用上半身动作槽时强制收镜（与原 FPCameraRig 行为一致）
            bool wantsAim = _remoteAimIntent ?? (_input != null && _input.AimHeld);
            bool actionFree = (_actions == null || !_actions.IsBusy) && (_input == null || !_input.WeaponInputBlocked);
            if (!actionFree)
            {
                // 切换开镜模式：动作收镜后不回弹，复位 InputReader 的切换态（长按模式无效果）
                _input?.ResetAimToggle();
            }
            float target = wantsAim && actionFree ? 1f : 0f;
            bool aimTarget = target > 0f;
            if (_timelineTarget != aimTarget)
            {
                _timelineTarget = aimTarget;
                double started = System.Math.Max(0, Time.timeAsDouble - Time.deltaTime);
                _timeline.SetTarget(aimTarget, started, adsTransitionSeconds);
                GetComponent<Game.Gameplay.Network.NetworkCombatAuthority>()?.SubmitAimIntentAt(aimTarget, started);
            }
            Ads01 = AdsAt(Time.timeAsDouble);
            if (DebugAdsOverride.HasValue) Ads01 = Mathf.Clamp01(DebugAdsOverride.Value);

            // FOV/灵敏度求值（AdsFovMath 共享公式）：瞄具分档覆盖优先于武器默认 AdsFov。
            // P4 实体镜（I4b）：放大率 >1 的变焦瞄具由镜内 RT 相机承担放大（独立倍率）——
            // 世界相机只收束到武器默认 ADS FOV、不再套用 adsFovOverride（避免世界+镜内双重放大）；
            // 灵敏度基准视场在满开镜时切换为镜内 FOV（AdsFovMath.ScopeFov 以腰射为基准按 mag 压缩，
            // 缩放后恰为 1/mag），使开镜鼠标速度与镜内可见放大率一致，过渡期随 Ads01 混入。
            float weaponAdsFov = _weapon != null && _weapon.Stat.AdsFov > 1f ? _weapon.Stat.AdsFov : hipFov;
            OpticAimContext optic = _weapon != null ? _weapon.CurrentOpticAim : OpticAimContext.None;
            float adsFov = optic.IsPhysicalScope ? weaponAdsFov : optic.EffectiveAdsFov(weaponAdsFov);
            CurrentFov = AdsFovMath.EvaluateCurrentFov(hipFov, adsFov, Ads01);
            float sensitivityFov = optic.IsPhysicalScope
                ? Mathf.Lerp(CurrentFov, AdsFovMath.ScopeFov(hipFov, optic.Magnification), Ads01)
                : CurrentFov;
            LookSensitivityScale = AdsFovMath.SensitivityScale(hipFov, sensitivityFov);
        }
    }
}
