using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心 Presenter（Docs/13 §5.2）：订阅 Gameplay 事件与只读状态 → 计算目标值 → 写 Model。
    /// Gap 映射（§6.4）：GapPx = tan(spread)·(屏高/2)/tan(主相机 vFOV/2)·GapScale，用主相机
    /// （弹着所在投影）；ADS 时准心隐藏（≥0.5，同旧 OnGUI 行为）。Shotgun 外沿=CurrentSpread
    /// （合成值已含全部动态项；PelletSpread 属弹丸分布不含主锥——外沿另加，见 OnShot）。
    /// Day4 回归修复：Gap 只由真实 CurrentSpreadDegrees 驱动；不再叠加所有武器相同的固定脉冲。
    /// </summary>
    public sealed class CrosshairPresenter : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private PlayerStateView playerState;
        [SerializeField] private CrosshairConfig config;
        [Tooltip("开火时输出准心诊断（spread/物理Gap/显示Gap）——调参期用，默认关")]
        [SerializeField] private bool debugGap;

        public CrosshairModel Model { get; } = new();

        private UnityEngine.Camera _mainCam;   // Game.Presentation.Camera 命名空间遮蔽 UnityEngine.Camera，全限定

        private void Awake()
        {
            if (controller == null) controller = FindObjectOfType<WeaponController>();
            if (aimState == null) aimState = FindObjectOfType<PlayerAimState>();
            if (playerState == null) playerState = FindObjectOfType<PlayerStateView>();
        }

        private void OnEnable()
        {
            if (controller != null) controller.OnShotFired += HandleShot;
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnShotFired -= HandleShot;
        }

        private void HandleShot(WeaponShot shot)
        {
            if (shot.Result.Damaged) Model.HitMarkerRemaining = config != null ? config.HitMarkerSeconds : 0.25f;
            if (debugGap)
                Debug.Log($"[Crosshair] shot#{shot.ShotIndex} spread={Model.LastSpreadDegrees:F2}° " +
                          $"physGap={Model.TargetGap:F1}px display={Model.CurrentGap:F1}px", this);
        }

        // ---- 联网 Owner 重绑（2026-09-18 实机问题4：对局无准星）----
        // 在线模式场景作者玩家被模式门整树禁用，Awake 的一次性 FindObjectOfType 绑到禁用对象
        // → controller.IsInitialized 恒 false → 准星恒隐藏。WeaponHudView 有 RebindToOwnerPlayer
        // （弹药正常显示），本组件缺同样机制——按同款 0.5s 重扫补齐。离线无网络状态组件，重绑不发生。
        private Game.Gameplay.Network.NetworkWeaponState _netWeaponState;
        private float _netScanTimer;

        private void Update()
        {
            if (_netWeaponState == null)
            {
                _netScanTimer -= Time.unscaledDeltaTime;
                if (_netScanTimer <= 0f)
                {
                    _netScanTimer = 0.5f;
                    TryRebindToOwnerPlayer();
                }
            }
            UpdateCrosshair();
        }

        private void TryRebindToOwnerPlayer()
        {
            foreach (var state in FindObjectsByType<Game.Gameplay.Network.NetworkWeaponState>(FindObjectsSortMode.None))
            {
                if (state == null || !state.IsOwnerPlayerSafe) continue;
                var ownerController = state.GetComponentInChildren<WeaponController>(true);
                var ownerAim = state.GetComponentInChildren<PlayerAimState>(true);
                var ownerStateView = state.GetComponentInChildren<PlayerStateView>(true);
                if (ownerController == null) continue; // 玩家对象尚未完全装配，下轮重扫
                _netWeaponState = state;
                Rebind(ownerController, ownerAim, ownerStateView);
                Debug.Log("[CrosshairPresenter] 准星已重绑到联网 Owner 玩家（在线模式 authored 玩家不参与）", this);
                return;
            }
        }

        /// <summary>重绑准星数据源（internal=测试接缝）：换绑控制器并精确迁移开火事件订阅。</summary>
        internal void Rebind(WeaponController nextController, PlayerAimState nextAimState, PlayerStateView nextPlayerState)
        {
            if (nextController != null && nextController != controller)
            {
                if (controller != null) controller.OnShotFired -= HandleShot;
                controller = nextController;
                if (isActiveAndEnabled) controller.OnShotFired += HandleShot;
            }
            if (nextAimState != null) aimState = nextAimState;
            if (nextPlayerState != null) playerState = nextPlayerState;
        }

        private void UpdateCrosshair()
        {
            if (controller == null || !controller.IsInitialized) { Model.Visible = false; return; }
            if (_mainCam == null) _mainCam = UnityEngine.Camera.main;
            if (_mainCam == null) return;

            float spread = controller.CurrentSpreadDegrees;
            // Shotgun：外沿覆盖弹丸分布（§6.4 v3：CurrentSpread + PelletSpread）
            if (controller.Stat.Ballistic.PelletCount > 1)
                spread = Mathf.Min(45f, spread + controller.Stat.Ballistic.PelletSpread);
            Model.LastSpreadDegrees = spread;

            // 物理映射：视口半高 px / tan(vFOV/2) = 每弧度像素；×tan(spread)
            float pxPerRad = (Screen.height * 0.5f) / Mathf.Tan(_mainCam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float targetGap = Mathf.Clamp(
                Mathf.Tan(spread * Mathf.Deg2Rad) * pxPerRad * config.GapScale,
                config.MinGap, config.MaxGap);

            Model.TargetGap = targetGap;
            // 扩张即时、收拢平滑：开火与跳跃不会被同一条低速 SmoothDamp 掩盖。
            if (Model.CurrentGap < targetGap)
                Model.CurrentGap = targetGap;
            else
                Model.CurrentGap = Mathf.MoveTowards(Model.CurrentGap, targetGap,
                    Mathf.Max(0f, config.SmoothSpeed) * Time.deltaTime);

            bool sprint = playerState != null && playerState.LocomotionState == Game.Gameplay.Movement.LocomotionState.Sprint;
            Model.Visible = (aimState == null ? 0f : aimState.Ads01) < 0.5f
                            && !(config.HideOnSprint && sprint);

            if (Model.HitMarkerRemaining > 0f)
                Model.HitMarkerRemaining -= Time.deltaTime;
        }
    }
}
