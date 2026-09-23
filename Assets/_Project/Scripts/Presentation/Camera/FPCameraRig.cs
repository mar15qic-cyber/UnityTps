using Game.Gameplay.Player;
using Game.Gameplay.Network;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 第一人称相机总管（表现层）。CP2 起 ADS 混合状态源上收为
    /// Gameplay 侧 PlayerAimState（Docs/13 §5.3-6：ADS 属于玩家，切枪不重建）；
    /// 2026-09-03 起 FOV 求值（含瞄具分档覆盖）也上收 PlayerAimState——
    /// 本组件只把 aimState.CurrentFov 应用到世界相机镜头，并向 sway/bob/武器姿态等
    /// 表现组件转发只读 AdsBlend。不写 Gameplay 任何状态。
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class FPCameraRig : MonoBehaviour
    {
        [SerializeField] private CinemachineCamera cinemachineCamera;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField, Range(1f, 120f), Tooltip("aimState 缺失时的兜底腰射 FOV（正常路径不消费，权威值在 PlayerAimState）")]
        private float hipFov = 60f;

        private NetworkCombatAuthority combat;
        private bool deathActive;
        private float deathElapsed;
        private Vector3 standingPosition;
        private float standingDutch;
        private Transform leanWeaponRoot;
        private Game.Gameplay.Movement.Locomotor locomotor;

        /// <summary>倒地视角参数：相机降到该局部高度并侧倾，历时 DeathFallSeconds。</summary>
        private const float DeathFallY = 0.25f;
        private const float DeathRollDegrees = 75f;
        private const float DeathFallSeconds = 0.65f;

        /// <summary>ADS 混合值：0 = 腰射，1 = 完全瞄准（转发自 PlayerAimState，只读）。</summary>
        public float AdsBlend => aimState != null ? aimState.Ads01 : 0f;

        private void Awake()
        {
            if (cinemachineCamera == null) cinemachineCamera = GetComponentInChildren<CinemachineCamera>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            combat = GetComponentInParent<NetworkCombatAuthority>();
            locomotor = GetComponentInParent<Game.Gameplay.Movement.Locomotor>();
            if (cinemachineCamera != null && cinemachineCamera.GetComponent<CmFPCameraLean>() == null)
                cinemachineCamera.gameObject.AddComponent<CmFPCameraLean>();
            var weaponRoot = transform.Find("FP_Weapon_Root");
            if (weaponRoot != null)
            {
                leanWeaponRoot = transform.Find("LeanWeaponRoot");
                if (leanWeaponRoot == null)
                {
                    leanWeaponRoot = new GameObject("LeanWeaponRoot").transform;
                    leanWeaponRoot.SetParent(transform, false);
                }
                weaponRoot.SetParent(leanWeaponRoot, false);
            }
        }

        private void Update()
        {
            if (cinemachineCamera == null) return;
            UpdateDeathView();
            if (leanWeaponRoot != null)
            {
                float lean = !deathActive && locomotor != null ? locomotor.Lean.Amount : 0f;
                leanWeaponRoot.localPosition = Vector3.right * (lean * Game.Gameplay.Player.LeanProfile.EyeSideMeters);
                leanWeaponRoot.localRotation = Quaternion.AngleAxis(
                    -lean * Game.Gameplay.Player.LeanProfile.MaxCameraRollDegrees, Vector3.forward);
            }
            var lens = cinemachineCamera.Lens;
            lens.FieldOfView = deathActive ? hipFov : aimState != null ? aimState.CurrentFov : hipFov;
            if (deathActive)
                lens.Dutch = Mathf.Lerp(standingDutch, DeathRollDegrees,
                    Mathf.SmoothStep(0f, 1f, deathElapsed / DeathFallSeconds));
            cinemachineCamera.Lens = lens;
        }

        private void UpdateDeathView()
        {
            bool dead = combat != null && combat.IsOwnerPlayer && combat.IsDead;
            if (dead && !deathActive)
            {
                deathActive = true;
                deathElapsed = 0f;
                standingPosition = transform.localPosition;
                standingDutch = cinemachineCamera.Lens.Dutch;
                // 审计 2026-09-16 §3.3-1：本组件**不再**保存/恢复 FP Renderer——那是 FPWeaponRig
                // （FPViewModelVisibility）的职责。旧实现与 Rig 各自快照同一批 Renderer.enabled，
                // "相机关→Rig 把 false 存成原始状态→复活相机恢复→Rig 下一帧又写回 false"造成
                // 死亡后当前枪永久隐形。相机只管位置/侧倾/FOV。
                Debug.Log($"[FPDeathView] 本人死亡倒地视角已激活 fallY={DeathFallY} dutch={standingDutch:F1}->{DeathRollDegrees}");
            }
            if (!dead) { RestoreDeathView(); return; }
            deathElapsed += Time.unscaledDeltaTime;
            var fallen = standingPosition;
            fallen.y = DeathFallY;
            transform.localPosition = Vector3.Lerp(standingPosition, fallen,
                Mathf.SmoothStep(0f, 1f, deathElapsed / DeathFallSeconds));
        }

        private void OnDisable() => RestoreDeathView();

        private void RestoreDeathView()
        {
            if (!deathActive) return;
            deathActive = false;
            transform.localPosition = standingPosition;
            if (cinemachineCamera != null)
            {
                var lens = cinemachineCamera.Lens;
                lens.Dutch = standingDutch;
                cinemachineCamera.Lens = lens;
            }
            Debug.Log("[FPDeathView] 倒地视角已还原（位置/侧倾；Renderer 由 FPWeaponRig 原因合成负责）");
        }
    }
}
