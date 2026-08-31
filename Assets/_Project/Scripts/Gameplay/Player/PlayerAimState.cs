using Game.Gameplay.Action;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>
    /// CP2 瞄准权威（Docs/13 §5.3-6）：ADS 状态属于玩家而非武器——由输入与动作槽推导 Ads01，
    /// 是全工程唯一的 ADS 混合状态源。FPCameraRig（FOV/表现收敛）、未来的 WeaponFireContext
    /// （Spread/Recoil 情境倍率）与准心 HUD 都只读本组件。切枪不重建（换武器不重置混合）。
    /// 原 FPCameraRig 的推导逻辑（AimHeld && !ActionSystem.IsBusy）原样上收，行为等价。
    /// </summary>
    [DefaultExecutionOrder(-80)] // 晚于 ActionSystem(-100)/早于 WeaponController(-50)
    public sealed class PlayerAimState : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] private float adsTransitionSeconds = 0.16f;

        /// <summary>ADS 混合值：0 = 腰射，1 = 完全瞄准。表现与数值消费者只读。</summary>
        public float Ads01 { get; private set; }

        /// <summary>ADS 过渡窗时长（秒）。FPWeaponAnimator 用它做 aim clip 播速适配（Docs/18 §4.2）。</summary>
        public float AdsTransitionSeconds => adsTransitionSeconds;

        private InputReader _input;
        private ActionSystem _actions;

        private void Awake()
        {
            _input = GetComponentInParent<InputReader>();
            _actions = GetComponentInParent<ActionSystem>();
        }

        private void OnEnable()
        {
            if (_input == null) _input = GetComponentInParent<InputReader>();
            if (_actions == null) _actions = GetComponentInParent<ActionSystem>();
        }

        private void Update()
        {
            // 换弹/切枪占用上半身动作槽时强制收镜（与原 FPCameraRig 行为一致）
            bool wantsAim = _input != null && _input.AimHeld;
            bool actionFree = _actions == null || !_actions.IsBusy;
            if (!actionFree)
            {
                // 切换开镜模式：动作收镜后不回弹，复位 InputReader 的切换态（长按模式无效果）
                _input?.ResetAimToggle();
            }
            float target = wantsAim && actionFree ? 1f : 0f;
            float speed = adsTransitionSeconds <= 0f
                ? Mathf.Infinity
                : Time.deltaTime / adsTransitionSeconds;
            Ads01 = Mathf.MoveTowards(Ads01, target, speed);
        }
    }
}
