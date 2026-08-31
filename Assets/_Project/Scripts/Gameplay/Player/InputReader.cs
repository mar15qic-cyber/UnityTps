using UnityEngine;
using UnityEngine.InputSystem;
using Game.Gameplay.Settings;

namespace Game.Gameplay.Player
{
    /// <summary>
    /// 输入唯一采样点（架构表A）：所有玩家输入只经此组件读取，其余系统只读其属性。
    /// 键位经 SettingsKeyMap 可重绑；未自定义时与旧硬编码行为一致。
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public class InputReader : MonoBehaviour
    {
        [SerializeField] private float jumpBufferTime = 0.15f;

        /// <summary>光标是否已锁定（游戏运行中）。Escape 可切换。</summary>
        public bool CursorLocked { get; private set; }

        /// <summary>WASD 移动向量，已归一化（幅值 ≤ 1）。</summary>
        public Vector2 Move { get; private set; }

        /// <summary>LeftShift 按住。</summary>
        public bool Sprint { get; private set; }

        /// <summary>鼠标帧增量（像素）。</summary>
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

        public void ConsumeJump() => _jumpBufferTimer = 0f;

        /// <summary>复位开镜切换态。换弹/切枪占用动作槽或光标解锁时由 PlayerAimState/本组件调用，
        /// 避免动作结束后 ADS 自动回弹（长按模式下无效果）。</summary>
        public void ResetAimToggle() => _adsToggled = false;

        private void OnEnable()
        {
            LockCursor();
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            CursorLocked = true;
        }

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
            AimHeld = false;
            if (kb == null) return;

            // Escape 切换光标锁定，便于编辑器调试时操作其他窗口
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (CursorLocked)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                    CursorLocked = false;
                }
                else
                {
                    LockCursor();
                }
                return;
            }
            if (!CursorLocked)
            {
                _adsToggled = false; // 光标自由时开镜意图不保留（切换模式）
                return;
            }

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
                LookDelta = mouse.delta.ReadValue();
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
