using UnityEngine;
using UnityEngine.InputSystem;
using Game.Gameplay.Menu;
using Game.Gameplay.Settings;

namespace Game.Gameplay.Player
{
    /// <summary>
    /// 输入唯一采样点（架构表A）：所有玩家输入只经此组件读取，其余系统只读其属性。
    /// 键位经 SettingsKeyMap 可重绑；未自定义时与旧硬编码行为一致。
    /// ESC/光标所有权已迁移到 GameplayMenuController（Phase A）：本组件不消费 Escape、
    /// 不写光标；只在 GameplayInputGate.InputBlocked 时输出全零快照并清内部意图。
    /// 用户灵敏度（SettingsRuntime.Sensitivity）在源头只乘一次——所有视角消费者
    /// （Locomotor/FPMouseLook/网络命令/远端俯仰/sway）拿到的是同一份缩放后的 LookDelta。
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public class InputReader : MonoBehaviour
    {
        [SerializeField] private float jumpBufferTime = 0.15f;

        /// <summary>WASD 移动向量，已归一化（幅值 ≤ 1）。</summary>
        public Vector2 Move { get; private set; }

        /// <summary>LeftShift 按住。</summary>
        public bool Sprint { get; private set; }

        /// <summary>鼠标帧增量（像素，已乘用户灵敏度与 PlayerAimState.LookSensitivityScale 开镜倍率——
        /// 用户灵敏度与开镜倍率都只在源头应用一次，禁止消费方再乘）。</summary>
        public Vector2 LookDelta { get; private set; }

        /// <summary>鼠标左键按住；由武器定义决定按住是否连续开火。</summary>
        public bool FireHeld { get; private set; }

        /// <summary>鼠标左键本帧按下。</summary>
        public bool FirePressed { get; private set; }

        /// <summary>开镜意图。长按模式 = 右键按住；切换模式 = 右键点按翻转（AdsInputMode）。</summary>
        public bool AimHeld { get; private set; }

        /// <summary>R 键本帧按下。</summary>
        public bool ReloadPressed { get; private set; }

        /// <summary>数字键槽位选择（0 基；-1 = 本帧无选择）。</summary>
        public int SlotPressed { get; private set; } = -1;

        /// <summary>滚轮切枪方向（Docs/23 表现迭代 2026-09-05）：+1=滚轮上（上一把）、-1=滚轮下（下一把）、0=无。
        /// 只取本帧滚动方向，帧内多格不放大。</summary>
        public float SwapAxis { get; private set; }

        /// <summary>快速切枪键（默认 Q，SettingsKeyMap.QuickSwap 可重绑）本帧按下。</summary>
        public bool QuickSwapPressed { get; private set; }

        /// <summary>缓冲窗口内存在未消费的跳跃请求。</summary>
        public bool JumpQueued => _jumpBufferTimer > 0f;

        private float _jumpBufferTimer;
        private uint _pressSequence;
        private uint _wOrder;
        private uint _sOrder;
        private uint _aOrder;
        private uint _dOrder;
        private bool _adsToggled;
        private bool _lastAdsToggleMode;
        private PlayerAimState _aimState;

        public void ConsumeJump() => _jumpBufferTimer = 0f;

        /// <summary>复位开镜切换态。换弹/切枪占用动作槽或光标解锁时由 PlayerAimState/本组件调用，
        /// 避免动作结束后 ADS 自动回弹（长按模式下无效果）。</summary>
        public void ResetAimToggle() => _adsToggled = false;

        private void Awake()
        {
            _aimState = GetComponentInChildren<PlayerAimState>();
        }

        private void OnEnable()
        {
            // 光标锁定由 GameplayMenuController 统一负责（挂载时按状态应用）；此处不再写光标
            if (_aimState == null) _aimState = GetComponentInChildren<PlayerAimState>();
        }

        private void OnDisable()
        {
            // 光标交还由菜单控制器场景清理统一处理（远端实例本组件本就被禁用，不参与）
        }

        /// <summary>LookDelta 合成（纯函数，EditMode 可测「灵敏度只应用一次」契约）：
        /// raw 鼠标像素 × 用户灵敏度 × ADS 焦距比倍率——除此之外任何消费方不得再乘灵敏度。</summary>
        public static Vector2 ComputeLookDelta(Vector2 raw, float adsSensitivityScale, float userSensitivity)
            => raw * Mathf.Clamp(userSensitivity, 0.1f, 5f) * Mathf.Clamp(adsSensitivityScale, 0.05f, 1f);

        private void Update()
        {
            var kb = Keyboard.current;
            Move = Vector2.zero;
            Sprint = false;
            LookDelta = Vector2.zero;
            FireHeld = false;
            FirePressed = false;
            ReloadPressed = false;
            SlotPressed = -1;
            SwapAxis = 0f;
            QuickSwapPressed = false;
            AimHeld = false;

            // 输入门控（菜单打开/硬锁/死亡/恢复宽限）：全零快照 + 清内部意图，不读任何设备
            if (GameplayInputGate.InputBlocked)
            {
                _jumpBufferTimer = 0f;       // 跳跃缓冲一并清零（菜单内不保留起跳意图）
                if (GameplayInputGate.MenuOpen || GameplayInputGate.HardLocked)
                    _adsToggled = false;     // 菜单/硬锁清开镜切换态，避免关闭后 ADS 自动回弹
                return;
            }
            if (kb == null) return;

            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveForward)].wasPressedThisFrame) _wOrder = ++_pressSequence;
            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveBack)].wasPressedThisFrame) _sOrder = ++_pressSequence;
            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveLeft)].wasPressedThisFrame) _aOrder = ++_pressSequence;
            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveRight)].wasPressedThisFrame) _dOrder = ++_pressSequence;

            var move = new Vector2(
                ResolveOpposingAxis(kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveRight)].isPressed, _dOrder, kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveLeft)].isPressed, _aOrder),
                ResolveOpposingAxis(kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveForward)].isPressed, _wOrder, kb[SettingsKeyMap.Get(SettingsKeyMap.Action.MoveBack)].isPressed, _sOrder));
            Move = Vector2.ClampMagnitude(move, 1f);

            Sprint = kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Sprint)].isPressed;
            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Jump)].wasPressedThisFrame)
                _jumpBufferTimer = jumpBufferTime;
            ReloadPressed = kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Reload)].wasPressedThisFrame;
            // 快速切枪（Q，可重绑）
            QuickSwapPressed = kb[SettingsKeyMap.Get(SettingsKeyMap.Action.QuickSwap)].wasPressedThisFrame;
            if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Slot1)].wasPressedThisFrame) SlotPressed = 0;
            else if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Slot2)].wasPressedThisFrame) SlotPressed = 1;
            else if (kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Slot3)].wasPressedThisFrame) SlotPressed = 2;
            else if (kb.digit4Key.wasPressedThisFrame) SlotPressed = 3;
            else if (kb.digit5Key.wasPressedThisFrame) SlotPressed = 4;
            else if (kb.digit6Key.wasPressedThisFrame) SlotPressed = 5;
            else if (kb.digit7Key.wasPressedThisFrame) SlotPressed = 6;
            else if (kb.digit8Key.wasPressedThisFrame) SlotPressed = 7;
            else if (kb.digit9Key.wasPressedThisFrame) SlotPressed = 8;
            else if (kb.digit0Key.wasPressedThisFrame) SlotPressed = 9;

            _jumpBufferTimer -= Time.deltaTime;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                // 滚轮切枪方向（只取方向，不累计格数——切枪按次触发）
                float scrollY = mouse.scroll.ReadValue().y;
                SwapAxis = scrollY > 0f ? 1f : scrollY < 0f ? -1f : 0f;
                // 灵敏度唯一应用点：用户灵敏度（SettingsRuntime 实时值，设置页即时预览）
                // × 开镜倍率（PlayerAimState 上帧求值，-300 先执行=一帧滞后，过渡窗内无感）
                LookDelta = ComputeLookDelta(
                    mouse.delta.ReadValue(),
                    _aimState != null ? _aimState.LookSensitivityScale : 1f,
                    SettingsRuntime.Sensitivity);
                FireHeld = mouse.leftButton.isPressed;
                FirePressed = mouse.leftButton.wasPressedThisFrame;
                var toggleMode = AdsInputMode.Toggle;
                if (toggleMode != _lastAdsToggleMode)
                {
                    _lastAdsToggleMode = toggleMode;
                    _adsToggled = false; // 设置页切换模式时清态，避免旧切换态带进新模式
                }
                if (toggleMode)
                {
                    if (mouse.rightButton.wasPressedThisFrame) _adsToggled = !_adsToggled;
                    AimHeld = _adsToggled;
                }
                else
                {
                    AimHeld = mouse.rightButton.isPressed;
                }
            }
        }

        private static float ResolveOpposingAxis(
            bool positivePressed,
            uint positiveOrder,
            bool negativePressed,
            uint negativeOrder)
        {
            if (positivePressed && negativePressed)
                return positiveOrder >= negativeOrder ? 1f : -1f;
            if (positivePressed) return 1f;
            if (negativePressed) return -1f;
            return 0f;
        }
    }
}
