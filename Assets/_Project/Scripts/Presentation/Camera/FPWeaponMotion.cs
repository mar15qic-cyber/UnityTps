using System.Collections.Generic;
using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>
    /// Day4 第一人称武器 viewmodel 动效（FP_Weapon_Root 本地姿态唯一写者）：
    /// sway（鼠标反向滞后）/ bob（与相机共用 GaitPhase，步频一致）/ breathing（Idle）/
    /// recoil（开火后坐：后移+上抬，弹簧回中）/ ADS（枪口自动对准屏幕中心的程序化瞄准姿态）。
    /// CP2 起 viewmodel 独自承担全部位置/旋转手感——相机侧 Sway/Bob 组件已删除、
    /// Breathing 位置通道已按 Docs/13 §5.3-2 迁移（相机零位置修正，保 FireRay 与
    /// 屏幕中心射线同线），本组件的 sway/bob/breathing 即全部剩余观感来源。
    /// ADS 姿态在换枪时按当前激活视图的 SightReference/Muzzle 自动推导。
    /// 只写本 Transform 的本地位置/旋转，不触碰 Gameplay。
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class FPWeaponMotion : MonoBehaviour
    {
        [Header("Sway（鼠标反向滞后）")]
        [SerializeField, Min(0f)] private float swayPositionAmplitude = 0.01f;
        [SerializeField, Min(0f)] private float swayDegreesPerPixel = 0.06f;
        [SerializeField, Min(0f)] private float swayMaxDegrees = 3.5f;
        [SerializeField, Min(0f)] private float swaySmoothingSeconds = 0.08f;

        [Header("Bob（步态起伏）")]
        [SerializeField, Min(0f)] private float bobVerticalAmplitude = 0.012f;
        [SerializeField, Min(0f)] private float bobLateralAmplitude = 0.008f;
        [SerializeField, Min(0.01f)] private float bobReferenceSpeed = 3.44f;

        [Header("Breathing（Idle 呼吸）")]
        [SerializeField, Min(0f)] private float breathingCyclesPerSecond = 0.28f;
        [SerializeField, Min(0f)] private float breathingAmplitude = 0.004f;

        [Header("Recoil（kick 幅度来自 WeaponShot.Recoil=数据驱动；此处仅视觉回中弹簧参数）")]
        [SerializeField, Min(0.1f)] private float recoilSpringFrequency = 8f;
        [SerializeField, Range(0f, 1f)] private float recoilDampingRatio = 0.7f;
        [SerializeField, Min(0f)] private float recoilYawMultiplier = 2f;
        [SerializeField, Min(0f)] private float recoilRollMultiplier = 0.75f;
        [SerializeField, Min(0f)] private float recoilLateralPerYaw = 0.01f;
        [SerializeField, Min(0f)] private float recoilMaxPitch = 10f;
        [SerializeField, Min(0f)] private float recoilMaxYaw = 4f;
        [SerializeField, Min(0f)] private float recoilMaxRoll = 2f;
        [SerializeField, Min(0f)] private float recoilMaxBack = 0.1f;
        [SerializeField, Min(0f)] private float recoilMaxLateral = 0.02f;

        [Header("ADS")]
        [Tooltip("ADS 时 sway/bob 位置阻尼（0=无阻尼 1=全阻）")]
        [SerializeField, Range(0f, 1f)] private float adsMotionDamping = 0.85f;
        [Tooltip("ADS 时开火后坐的视觉保持系数（Day4 审计 §4：满 ADS 不得只剩 15% 反馈）。1=与腰射同强度")]
        [SerializeField, Range(0.3f, 1f)] private float adsRecoilRetention = 0.75f;

        private readonly List<WeaponView> _viewBuffer = new();
        private InputReader _input;
        private PlayerStateView _state;
        private FPCameraRig _rig;
        private WeaponController _weapon;
        private UnityEngine.Camera _viewCamera;
        private WeaponDefinition _currentDefinition;

        private Vector2 _swaySmoothed;
        private Vector2 _swayVelocity;
        private float _bobWeight;
        private float _bobWeightVelocity;
        private Vector3 _recoilRotation;
        private Vector3 _recoilRotationVelocity;
        private Vector3 _recoilPosition;
        private Vector3 _recoilPositionVelocity;
        private float _time;
        private Vector3 _aimLocalPosition;
        private Quaternion _aimLocalRotation = Quaternion.identity;
        private bool _animationAds;   // 动画 ADS 轨道（Docs/18 §4.3）：对位策略切换开关（§12 修订）

        private void Awake()
        {
            _input = GetComponentInParent<InputReader>();
            _state = GetComponentInParent<PlayerStateView>();
            _rig = GetComponentInParent<FPCameraRig>();
            _weapon = GetComponentInParent<WeaponController>();
            _viewCamera = ResolveViewCamera();
        }

        private void OnEnable()
        {
            if (_weapon == null) _weapon = GetComponentInParent<WeaponController>();
            if (_weapon != null) _weapon.OnShotFired += HandleShot;
        }

        private void OnDisable()
        {
            if (_weapon != null) _weapon.OnShotFired -= HandleShot;
        }

        private void HandleShot(WeaponShot shot)
        {
            // 只消费本发 ShotRecoilResult，不重新随机。Unity 局部 +X 欧拉角会让枪口下压，
            // 因而“上抬”为负 X；枪口朝局部 +Z，后移为负 Z。
            float yaw = shot.Recoil.YawKickDeg * recoilYawMultiplier;
            _recoilRotation += new Vector3(
                -shot.Recoil.ViewModelPitchDeg,
                yaw,
                -shot.Recoil.YawKickDeg * recoilRollMultiplier);
            _recoilPosition += new Vector3(
                -shot.Recoil.YawKickDeg * recoilLateralPerYaw,
                0f,
                -shot.Recoil.ViewModelBackM);

            _recoilRotation.x = Mathf.Clamp(_recoilRotation.x, -recoilMaxPitch, 0f);
            _recoilRotation.y = Mathf.Clamp(_recoilRotation.y, -recoilMaxYaw, recoilMaxYaw);
            _recoilRotation.z = Mathf.Clamp(_recoilRotation.z, -recoilMaxRoll, recoilMaxRoll);
            _recoilPosition.x = Mathf.Clamp(_recoilPosition.x, -recoilMaxLateral, recoilMaxLateral);
            _recoilPosition.z = Mathf.Clamp(_recoilPosition.z, -recoilMaxBack, 0f);
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            _time += dt;

            if (_weapon != null && _weapon.Definition != _currentDefinition)
            {
                _currentDefinition = _weapon.Definition;
                // Docs/18 §4.3 双轨：武器配齐 aim_in/aim_out 时 ADS 姿态由动画驱动
                // （FPWeaponAnimator）；对位策略也随之切换（§12 修订，见 ComputeAimAlignmentOffset）
                _animationAds = _currentDefinition != null
                    && _currentDefinition.FirstPersonAnimations.HasAimClips;
            }

            float adsBlend = _rig != null ? _rig.AdsBlend : 0f;
            // ADS 对位每帧重算（Docs/18 §12 修订）：aim_in 过渡中姿态逐帧变化；
            // 测量经 InverseTransformPoint 抵消 root 自身变换——与本组件的写入无反馈回路。
            // LPW 替换枪还会用显式 SightReference 旋转修正机械瞄准轴。
            if (adsBlend > 0f)
                ComputeAimAlignmentPose(out _aimLocalPosition, out _aimLocalRotation);

            float motionScale = 1f - adsBlend * adsMotionDamping;
            // Day4 审计 §4：开火后坐独立保持系数——旧版与 sway/bob 共用 motionScale
            // （满 ADS 仅剩 15%），玩家最关注的 ADS 连射恰好反馈最弱。此处后坐通道单独缩放。
            float recoilScale = Mathf.Lerp(1f, adsRecoilRetention, adsBlend);

            // ---- sway：鼠标增量反向滞后 ----
            Vector2 look = _input != null ? _input.LookDelta : Vector2.zero;
            _swaySmoothed = swaySmoothingSeconds <= 0f
                ? look
                : Vector2.SmoothDamp(_swaySmoothed, look, ref _swayVelocity, swaySmoothingSeconds, Mathf.Infinity, Mathf.Max(dt, 0.0001f));
            float swayX = Mathf.Clamp(-_swaySmoothed.x * swayPositionAmplitude * 0.01f, -swayPositionAmplitude, swayPositionAmplitude);
            float swayY = Mathf.Clamp(_swaySmoothed.y * swayPositionAmplitude * 0.01f, -swayPositionAmplitude, swayPositionAmplitude);
            float swayYaw = Mathf.Clamp(-_swaySmoothed.x * swayDegreesPerPixel, -swayMaxDegrees, swayMaxDegrees);
            float swayPitch = Mathf.Clamp(_swaySmoothed.y * swayDegreesPerPixel, -swayMaxDegrees, swayMaxDegrees);

            // ---- bob：与相机 Bob 同源步态相位 ----
            bool groundedMove = _state != null
                && (_state.LocomotionState == LocomotionState.Walk || _state.LocomotionState == LocomotionState.Sprint);
            float speedScale = _state != null ? Mathf.Clamp01(_state.HorizontalSpeed / bobReferenceSpeed) : 0f;
            _bobWeight = Mathf.SmoothDamp(_bobWeight, groundedMove ? speedScale : 0f, ref _bobWeightVelocity, 0.15f, Mathf.Infinity, Mathf.Max(dt, 0.0001f));
            float phase = _state != null ? _state.GaitPhase * Mathf.PI * 2f : 0f;
            float bobX = Mathf.Sin(phase) * bobLateralAmplitude * _bobWeight;
            float bobY = Mathf.Abs(Mathf.Cos(phase)) * bobVerticalAmplitude * _bobWeight - bobVerticalAmplitude * 0.5f * _bobWeight;

            // ---- breathing：仅 Idle ----
            bool idle = _state != null && _state.LocomotionState == LocomotionState.Idle;
            float breath = idle ? Mathf.Sin(_time * breathingCyclesPerSecond * Mathf.PI * 2f) : 0f;

            // ---- recoil 弹簧（rotation=x pitch/y yaw/z roll；position=x lateral/z back）----
            float omega = recoilSpringFrequency * Mathf.PI * 2f;
            float damping = 2f * recoilDampingRatio * omega;
            _recoilRotationVelocity += (-omega * omega * _recoilRotation - damping * _recoilRotationVelocity) * dt;
            _recoilRotation += _recoilRotationVelocity * dt;
            _recoilPositionVelocity += (-omega * omega * _recoilPosition - damping * _recoilPositionVelocity) * dt;
            _recoilPosition += _recoilPositionVelocity * dt;

            // ---- 合成：腰射姿态（identity）↔ ADS 姿态 + 各动效 ----
            Vector3 basePos = Vector3.Lerp(Vector3.zero, _aimLocalPosition, adsBlend);
            transform.localPosition = basePos + new Vector3(
                (swayX + bobX) * motionScale,
                (swayY + bobY) * motionScale + breath * breathingAmplitude,
                0f) + _recoilPosition * recoilScale;
            Quaternion motionRotation = Quaternion.Euler(
                swayPitch * motionScale + _recoilRotation.x * recoilScale,
                swayYaw * motionScale + _recoilRotation.y * recoilScale,
                _recoilRotation.z * recoilScale);
            transform.localRotation = Quaternion.Slerp(Quaternion.identity, _aimLocalRotation, adsBlend)
                * motionRotation;

        }

        /// <summary>ADS 对位姿态（Docs/18 §12 实机审计修订）。
        /// 实测（AnimationMode + BakeMesh 探针）：LPFP aim 动画把枪模铁瞄前后瞄尖对中到
        /// Armature/camera 节点（作者相机标记，与 LPFP 原版 Gun Camera (0,0.09,-0.18) 重合，
        /// 前后瞄尖恰在该高度 ±7mm 且同高=瞄准线水平）。我们的 FP View Camera 与该节点差
        /// (−0.05, −0.006, −0.12)，导致开镜后照门停在屏幕中心下方 ~10%（腰射感）。
        /// 修复：动画 ADS 武器把 viewmodel 根平移 (相机−节点)，使相机精确落到作者相机位置，
        /// 铁瞄按原设计对中（z 分量一并修正，瞄具画面尺寸=作者设计）。
        /// 无 aim clip 武器（程序化轨道）：照门/枪口 x/y 对中，z 不动。
        /// 注意旧实现符号相反（+偏差而非−偏差），武器向远离中线方向平移——
        /// Day4 以来"ADS 只是变焦"的根因之一。</summary>
        private void ComputeAimAlignmentPose(out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            if (_viewCamera == null || transform.parent == null) return;
            var view = FindActiveView();
            if (view == null) return;

            // 相机在父系位置（父系与相机系旋转一致，平移差不影响 delta 方向）
            Vector3 camParent = transform.parent.InverseTransformPoint(_viewCamera.transform.position);
            var aimPoint = view.SightReference != null ? view.SightReference : view.Muzzle;

            if (view.AlignAdsToSightAxis && aimPoint != null)
            {
                // LPW guns keep one statically calibrated prefab transform for hip and ADS.
                // This root motion only preserves the authored LPFP arm/camera relationship;
                // no per-ADS translation is applied to LPW_Gun at runtime.
                if (_animationAds)
                {
                    var authorCamera = view.transform.Find("Armature/camera");
                    if (authorCamera != null)
                    {
                        Vector3 authorCameraRoot = transform.InverseTransformPoint(authorCamera.position);
                        localPosition = camParent - authorCameraRoot;
                    }
                }
                return;
            }

            if (_animationAds)
            {
                var camNode = view.transform.Find("Armature/camera");
                if (camNode != null)
                {
                    // root 自身变换经 InverseTransformPoint 抵消：测量稳定、与写入无反馈
                    Vector3 nodeRoot = transform.InverseTransformPoint(camNode.position);
                    localPosition = camParent - nodeRoot;
                    return;
                }
                // 非常规底座（无作者相机节点）：退回标记对中
            }

            if (aimPoint == null) return;
            // 正确方向 = 相机位置 − 参考点位置（把参考点推到相机中线的 x/y 上）
            Vector3 sightRoot = transform.InverseTransformPoint(aimPoint.position);
            localPosition = new Vector3(camParent.x - sightRoot.x, camParent.y - sightRoot.y, 0f);
        }

        private WeaponView FindActiveView()
        {
            GetComponentsInChildren(false, _viewBuffer);
            WeaponView best = null;
            foreach (var candidate in _viewBuffer)
                if (candidate != null && candidate.isActiveAndEnabled) { best = candidate; break; }
            _viewBuffer.Clear();
            return best;
        }

        /// <summary>FP View Camera 是 Main Camera（父节点）下渲染 layer 9 的 overlay 相机。
        /// 注意：Game.Presentation.Camera 命名空间遮蔽 UnityEngine.Camera 简名，必须全限定。</summary>
        private UnityEngine.Camera ResolveViewCamera()
        {
            if (transform.parent == null) return null;
            foreach (var cam in transform.parent.GetComponentsInChildren<UnityEngine.Camera>(false))
                if (cam.gameObject != transform.parent.gameObject) return cam;
            return null;
        }
    }
}
