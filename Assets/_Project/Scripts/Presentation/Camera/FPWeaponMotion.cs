using System.Collections.Generic;
using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
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
    /// V2 LPW 的枪模姿态由 LPWGunPoseDriver 独立写入；本组件仍只写
    /// FP_Weapon_Root（动画作者相机根、sway/bob/breathing 与旧版兼容后坐）。
    /// 只写本 Transform 的本地位置/旋转，不触碰 Gameplay。
    ///
    /// 抖动修复（ADS 开火抖动/近裁剪/黑屏/漂移）后的姿态所有权与求解规则：
    /// ① 基础姿态 + ADS 姿态 + 共享平移 + 后坐弹簧在统一局部空间一次组合、
    ///    单次绝对写回——不再有 transform.position += / localPosition += 混合叠加。
    /// ② 所有对位测量改为"root 空间测量（InverseTransformPoint，位姿不变）×
    ///    本帧干净根旋转"——上一帧的后坐旋转不再泄入测量（旧实现的世界差值测量
    ///    会在连射时把根姿态炸到 1e18 触发 AABB 损坏断言）。
    /// ③ 相机参考帧一律用 StableCameraRotation()：剔除 CmFPCameraRecoil 回声
    ///    （与 WeaponController.CurrentRecoilRotation 同源同值），瞄准解永不追逐
    ///    正在后坐的相机；look 旋转仍被逐帧追踪。
    /// ④ 满 ADS（adsBlend ≥ FullAdsSolveFreeze）冻结瞄准解：只在入镜过渡、
    ///    切枪（视图/定义变化）、瞄具配置变化、恰好抵达满 ADS 的边界帧重解。
    ///    射击与恢复期间基准保持冻结——每帧只做共享平移的残差测量（补偿 sway，
    ///    用稳定帧），不做姿态闭环。
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

        /// <summary>满 ADS 判定阈值：达到即冻结瞄准解（aim_in 已适配过渡窗，到高位即静态）。</summary>
        private const float FullAdsSolveFreeze = 0.9995f;

        /// <summary>对位解输出边界门：viewmodel 根的合法对位偏移远小于此值，
        /// 超界即视为链路污染产物并拒收（冻结语义下坏解会被永久锁定）。</summary>
        private const float MaxAlignmentOffset = 2f;

        private readonly List<WeaponView> _viewBuffer = new();
        private static readonly HashSet<string> OpticFallbackWarnings = new();
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
        private bool _anchoredDualPoseV2;
        private float _lastAdsBlend;

        // ---- ADS 瞄准解缓存（抖动修复④：满 ADS 冻结，合法失效点重解） ----
        private WeaponView _alignmentView;        // 解的所有者（切枪失效键）
        private string _alignmentOpticId;         // 瞄具情境（附件变化失效键）
        private bool _dynamicSolved;              // Legacy 动态枪轴解是否可用
        private Quaternion _dynamicTargetRotation; // 满 ADS 枢轴目标旋转（pivot 父系局部）
        private Vector3 _dynamicGripPivot;          // 右手握点（pivot 局部，瞄准不变量）
        private float _dynamicSightDepth;           // 授权瞄具深度（稳定帧前向投影）
        private bool _opticAimSolveActive;          // 光轴眼点解生效（Legacy 动态机瞄枢轴层必须让位，
                                                    // 否则后瞄对中会拖拽已被光轴解对齐的枪根）
        private int _alignmentCalibrationVersion = -1; // 校准数据版本失效键（校准窗口改表即重解）

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
            // WeaponShot is gameplay-owned data. A malformed recoil payload must not
            // enter the spring or poison every subsequent viewmodel frame.
            if (!IsFinite(shot.Recoil.YawKickDeg)
                || !IsFinite(shot.Recoil.ViewModelPitchDeg)
                || !IsFinite(shot.Recoil.ViewModelBackM)
                || !IsFinite(recoilYawMultiplier)
                || !IsFinite(recoilRollMultiplier)
                || !IsFinite(recoilLateralPerYaw))
            {
                ResetRecoilState();
                return;
            }

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
            if (!IsFinite(_recoilRotation) || !IsFinite(_recoilPosition))
                ResetRecoilState();
        }

        private void ResetRecoilState()
        {
            _recoilRotation = Vector3.zero;
            _recoilRotationVelocity = Vector3.zero;
            _recoilPosition = Vector3.zero;
            _recoilPositionVelocity = Vector3.zero;
        }

        // ---- 2026-09-16 审计 §4.3：死亡/复活边界（只重置运行态，不动作者挂点与校准资产）----

        /// <summary>
        /// 死亡边界：清后坐/sway/bob 运行余量并**作废 ADS 对位解缓存**。
        /// 为什么必须作废缓存：满 ADS 的对位解按"冻结"语义只在入镜/切枪/瞄具变化时重解，
        /// 死亡期间武器/瞄具/视图都可能变化，旧解不会自愈 → 复活后枪位停留在错误姿态
        /// （"死亡后必须重新按一次 ADS 才正常"的直接原因之一）。
        /// </summary>
        public void ApplyDeathState()
        {
            ResetRecoilState();
            _swaySmoothed = Vector2.zero;
            _swayVelocity = Vector2.zero;
            _bobWeight = 0f;
            _bobWeightVelocity = 0f;
            _time = 0f;
            InvalidateAlignmentSolve();
        }

        /// <summary>复活边界：与死亡同一套清理（保证"不按 ADS 也能恢复正常枪位"）。</summary>
        public void ApplyRespawnState() => ApplyDeathState();

        /// <summary>作废对位解缓存（下一次 LateUpdate 按当前视图/瞄具重解，含腰射基线）。</summary>
        private void InvalidateAlignmentSolve()
        {
            _alignmentView = null;
            _alignmentOpticId = null;
            _dynamicSolved = false;
            _dynamicTargetRotation = Quaternion.identity;
            _dynamicGripPivot = Vector3.zero;
            _dynamicSightDepth = 0f;
            _opticAimSolveActive = false;
            _alignmentCalibrationVersion = -1;
            _lastAdsBlend = 0f;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (!IsFinite(dt) || dt < 0f)
                dt = 0f;
            // Do not let a stalled editor frame become an unbounded integration
            // step. The closed-form spring is stable, but clamping keeps all
            // authored motion scalars and trigonometry in a predictable range.
            dt = Mathf.Min(dt, 0.25f);
            _time += dt;
            if (!IsFinite(_time)) _time = 0f;

            bool definitionChanged = false;
            if (_weapon != null && _weapon.Definition != _currentDefinition)
            {
                _currentDefinition = _weapon.Definition;
                definitionChanged = true;
                // Docs/18 §4.3 双轨：武器配齐 aim_in/aim_out 时 ADS 姿态由动画驱动
                // （FPWeaponAnimator）；对位策略也随之切换（§12 修订，见 ComputeAimAlignmentPose）
                _animationAds = _currentDefinition != null
                    && _currentDefinition.FirstPersonAnimations.HasAimClips;
            }

            WeaponView activeView = FindActiveView();
            FPWeaponPoseProfile activeProfile = activeView != null
                ? activeView.GetComponent<FPWeaponPoseProfile>()
                : null;
            bool viewChanged = !ReferenceEquals(activeView, _alignmentView);
            _anchoredDualPoseV2 = activeProfile != null && activeProfile.HasCompleteAnchoredDualPoseV2;

            float adsBlend = _rig != null ? _rig.AdsBlend : 0f;
            adsBlend = IsFinite(adsBlend) ? Mathf.Clamp01(adsBlend) : 0f;
            float motionScale = 1f - adsBlend * adsMotionDamping;
            float adsStableMotionScale = motionScale * (1f - adsBlend);
            // Day4 审计 §4：开火后坐独立保持系数——旧版与 sway/bob 共用 motionScale
            // （满 ADS 仅剩 15%），玩家最关注的 ADS 连射恰好反馈最弱。此处后坐通道单独缩放。
            float recoilScale = IsFinite(adsRecoilRetention)
                ? Mathf.Lerp(1f, Mathf.Clamp(adsRecoilRetention, 0.3f, 1f), adsBlend)
                : 1f;

            // ---- sway：鼠标增量反向滞后 ----
            Vector2 look = _input != null ? _input.LookDelta : Vector2.zero;
            if (!IsFinite(look)) look = Vector2.zero;
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
            float speed = _state != null ? _state.HorizontalSpeed : 0f;
            float speedScale = IsFinite(speed) && IsFinite(bobReferenceSpeed) && bobReferenceSpeed > 0f
                ? Mathf.Clamp01(speed / bobReferenceSpeed) : 0f;
            _bobWeight = Mathf.SmoothDamp(_bobWeight, groundedMove ? speedScale : 0f, ref _bobWeightVelocity, 0.15f, Mathf.Infinity, Mathf.Max(dt, 0.0001f));
            float gaitPhase = _state != null ? _state.GaitPhase : 0f;
            float phase = IsFinite(gaitPhase) ? gaitPhase * Mathf.PI * 2f : 0f;
            float bobX = Mathf.Sin(phase) * bobLateralAmplitude * _bobWeight;
            float bobY = Mathf.Abs(Mathf.Cos(phase)) * bobVerticalAmplitude * _bobWeight - bobVerticalAmplitude * 0.5f * _bobWeight;

            // ---- breathing：仅 Idle ----
            bool idle = _state != null && _state.LocomotionState == LocomotionState.Idle;
            float breath = idle && IsFinite(breathingCyclesPerSecond)
                ? Mathf.Sin(_time * breathingCyclesPerSecond * Mathf.PI * 2f) : 0f;

            // ---- recoil 弹簧（rotation=x pitch/y yaw/z roll；position=x lateral/z back）----
            // 解析精确阻尼谐振子步进（抖动修复⑤）：原显式欧拉在 editor 卡顿/低帧率的
            // dt 尖峰下失稳（ω²≈2527，dt>~0.04s 即发散），一步放大近 19 倍直至 NaN，
            // 且积分路径无钳制——NaN 一旦产生永久污染 viewmodel（实测根姿态飞到 1e24+
            // 并触发 Unity AABB 损坏断言）。解析解对任意 dt 无条件稳定且能量守恒。
            float omega = IsFinite(recoilSpringFrequency) && recoilSpringFrequency > 0f
                ? recoilSpringFrequency * Mathf.PI * 2f : 0f;
            _recoilRotation = StepDampedSpringAxis3(_recoilRotation, _recoilRotationVelocity, omega, recoilDampingRatio, dt, out _recoilRotationVelocity);
            _recoilPosition = StepDampedSpringAxis3(_recoilPosition, _recoilPositionVelocity, omega, recoilDampingRatio, dt, out _recoilPositionVelocity);

            // ---- 预共享姿态：基础+ADS+sway/bob/breathing（不含共享平移与后坐）----
            // 后续所有对位测量以它为"干净根旋转"基准——上一帧写入的后坐永不进入测量。
            if (!IsFinite(_aimLocalPosition)) _aimLocalPosition = Vector3.zero;
            if (!IsFinite(_aimLocalRotation)) _aimLocalRotation = Quaternion.identity;
            Vector3 preSharedPosition = Vector3.Lerp(Vector3.zero, _aimLocalPosition, adsBlend) + new Vector3(
                (swayX + bobX) * adsStableMotionScale,
                (swayY + bobY) * adsStableMotionScale + breath * breathingAmplitude * (1f - adsBlend),
                0f);
            Quaternion preSharedRotation = Quaternion.Slerp(Quaternion.identity, _aimLocalRotation, adsBlend)
                * Quaternion.Euler(
                    swayPitch * adsStableMotionScale,
                    swayYaw * adsStableMotionScale,
                    0f);
            if (!IsFinite(preSharedPosition)) preSharedPosition = Vector3.zero;
            if (!IsFinite(preSharedRotation)) preSharedRotation = Quaternion.identity;

            // ---- ADS 瞄准解（冻结缓存④）：入镜过渡逐帧重解；满 ADS 冻结 ----
            string opticId = _weapon != null ? _weapon.CurrentOpticAim.ItemId : null;
            bool opticChanged = !string.Equals(opticId, _alignmentOpticId, System.StringComparison.Ordinal);
            int calibrationVersion = ResolveCalibrationVersion();
            bool calibrationChanged = calibrationVersion != _alignmentCalibrationVersion;
            bool transitioning = adsBlend < FullAdsSolveFreeze;
            bool enteringFullAds = adsBlend >= FullAdsSolveFreeze && _lastAdsBlend < FullAdsSolveFreeze;
            if (adsBlend > 0f && (transitioning || enteringFullAds || definitionChanged || viewChanged
                || opticChanged || calibrationChanged))
            {
                ComputeAimAlignmentPose(activeView, activeProfile, preSharedRotation,
                    out Vector3 solvedPosition, out Quaternion solvedRotation, out bool opticSolveActive);
                // 边界门：解输出异常（链路曾被污染后测得天文数字）时保留上一次有效缓存——
                // 冻结语义下坏解一旦写入会被永久锁定。
                if (IsFinite(solvedPosition) && solvedPosition.magnitude <= MaxAlignmentOffset
                    && IsFinite(solvedRotation))
                {
                    _aimLocalPosition = solvedPosition;
                    _aimLocalRotation = solvedRotation;
                }
                _opticAimSolveActive = opticSolveActive;
                if (_opticAimSolveActive)
                {
                    // 光轴解接管枪根姿态：清掉动态机瞄枢轴可能残留的混合旋转，
                    // 避免旧解的枢轴偏移叠加在光轴对齐后的枪根上。
                    if (activeProfile != null && activeProfile.AdsPivot != null)
                    {
                        activeProfile.AdsPivot.localPosition = Vector3.zero;
                        activeProfile.AdsPivot.localRotation = Quaternion.identity;
                    }
                    _dynamicSolved = false;
                }
                else if (!_anchoredDualPoseV2)
                {
                    SolveDynamicLpwGunAdsCache(activeView, activeProfile, preSharedRotation);
                }
                _alignmentView = activeView;
                _alignmentOpticId = opticId;
                _alignmentCalibrationVersion = calibrationVersion;
            }

            // A failed solve keeps the last valid cache, but a cache can also be
            // invalidated by an externally changed prefab/attachment. Fail closed
            // before dynamic pivot math or Transform assignment.
            if (!IsFinite(_aimLocalPosition) || !IsFinite(_aimLocalRotation))
            {
                _aimLocalPosition = Vector3.zero;
                _aimLocalRotation = Quaternion.identity;
            }

            // ---- Legacy 动态枪轴：应用冻结解的混合 + 逐帧共享平移残差 ----
            // （V2 的枪层姿态与后坐由 LPWGunPoseDriver 独占，本组件不触碰）
            // 光轴眼点解生效时让位：机瞄后瞄对中会拖拽已被光轴解对齐的枪根（双写者冲突）。
            Vector3 sharedParent = Vector3.zero;
            if (!_anchoredDualPoseV2 && !_opticAimSolveActive)
                sharedParent = ApplyDynamicLpwGunAdsPose(activeView, activeProfile, adsBlend,
                    preSharedPosition, preSharedRotation);

            // ---- 统一单次绝对写回（①）+ 有限性防护：任何来源的 NaN/天文数字
            // 都不允许到达 Transform（Unity 拒收会造成错误日志刷屏与状态冻结）----
            if (_anchoredDualPoseV2)
            {
                if (IsFinite(preSharedPosition) && IsFinite(preSharedRotation))
                {
                    transform.localPosition = preSharedPosition;
                    transform.localRotation = preSharedRotation;
                }
            }
            else
            {
                Vector3 finalPosition = preSharedPosition + sharedParent
                    + _recoilPosition * recoilScale;
                Quaternion finalRotation = preSharedRotation
                    * Quaternion.Euler(_recoilRotation * recoilScale);
                if (IsFinite(finalPosition) && IsFinite(finalRotation))
                {
                    transform.localPosition = finalPosition;
                    transform.localRotation = finalRotation;
                }
            }
            _lastAdsBlend = adsBlend;
        }

        /// <summary>ADS 对位姿态（Docs/18 §12 实机审计修订）。
        /// 实测（AnimationMode + BakeMesh 探针）：LPFP aim 动画把枪模铁瞄前后瞄尖对中到
        /// Armature/camera 节点（作者相机标记，与 LPFP 原版 Gun Camera (0,0.09,-0.18) 重合，
        /// 前后瞄尖恰在该高度 ±7mm 且同高=瞄准线水平）。我们的 FP View Camera 与该节点差
        /// (−0.05, −0.006, −0.12)，导致开镜后照门停在屏幕中心下方 ~10%（腰射感）。
        /// 修复：动画 ADS 武器把 viewmodel 根平移 (相机−节点)，使相机精确落到作者相机位置，
        /// 铁瞄按原设计对中（z 分量一并修正，瞄具画面尺寸=作者设计）。
        /// 无 aim clip 武器（程序化轨道）：照门/枪口 x/y 对中，z 不动。
        /// 抖动修复②：所有参考点先经 InverseTransformPoint 折回 root 空间（位姿不变量），
        /// 再乘 cleanRootRotation 得父系偏移——旧实现的世界差值测量把上一帧后坐旋转
        /// 带进解里，连射时形成闭环并数值爆炸。</summary>
        private void ComputeAimAlignmentPose(WeaponView view, FPWeaponPoseProfile profile,
            Quaternion cleanRootRotation, out Vector3 localPosition, out Quaternion localRotation,
            out bool opticSolveActive)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            opticSolveActive = false;
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            if (_viewCamera == null || transform.parent == null) return;
            if (view == null) return;

            // 相机在父系位置（父系与相机系旋转一致，平移差不影响 delta 方向）
            Vector3 camParent = transform.parent.InverseTransformPoint(_viewCamera.transform.position);
            if (!IsFinite(camParent) || !IsFinite(cleanRootRotation)
                || !IsFinite(_viewCamera.transform.position)
                || !IsFinite(_viewCamera.transform.rotation)) return;
            var aimPoint = view.SightReference != null ? view.SightReference : view.Muzzle;

            // 瞄具眼光轴对位（AttachmentCalibration.OpticAimRows 数据驱动，校准工具产物）：
            // 有校准的瞄具以"完整眼距/轴向解"替代机瞄瞄线——2026-09-19 ADS 审计 A1 修复：
            // 旧实现只对 x/y、z 恒 0、旋转不动，眼点停留在作者腰射深度 → 眼睛远在镜后，
            // 镜窗又远又小（参考图一现象）。完整解把眼点送到 FP 相机、光轴对齐稳定相机前向、
            // 滚转对齐相机 up，眼距与镜窗尺寸由校准数据直接决定（与分划投影共用 OpticAimGeometry 语义）。
            // 无记录时 TryResolveOpticAimRoot 返回 false，下方机瞄全部分支原样执行（零行为变化）。
            if (TryResolveOpticAimRoot(view, out Vector3 opticEyeRoot, out Vector3 opticAxisRoot, out Vector3 opticUpRoot))
            {
                if (_anchoredDualPoseV2)
                {
                    // V2 枪层由 LPWGunPoseDriver 独占：根层旋转会与枪层解耦失配，
                    // 维持旧 x/y 对位 + 作者 z（LPW 实验线行为不变，非本次样板目标）。
                    Vector3 eyeOffsetParentV2 = cleanRootRotation * opticEyeRoot;
                    if (!IsFinite(eyeOffsetParentV2)) return;
                    localPosition = new Vector3(
                        camParent.x - eyeOffsetParentV2.x,
                        camParent.y - eyeOffsetParentV2.y,
                        profile != null && profile.HasAdsCalibration
                            ? profile.AdsViewmodelLocalPosition.z
                            : 0f);
                    opticSolveActive = true;
                    return;
                }

                // 目标帧 = 父系 (前向 +Z, 上 +Y)；解 = source 帧的逆（OpticAimGeometry 纯数学，
                // 固定点性质由 EditMode 测试锁定：应用后眼点=camParent、光轴=+Z，与相机旋转无关）。
                Quaternion solvedRotation = OpticAimGeometry.SolveAimLocalRotation(opticAxisRoot, opticUpRoot);
                if (!IsFinite(solvedRotation)) return;
                Vector3 rotatedEyeParent = solvedRotation * opticEyeRoot;
                if (!IsFinite(rotatedEyeParent)) return;
                localPosition = OpticAimGeometry.SolveAimLocalPosition(solvedRotation, opticEyeRoot, camParent);
                localRotation = solvedRotation;
                opticSolveActive = true;
                return;
            }

            // AnchoredDualPoseV2 owns the gun layer separately.  The arms/root layer
            // is allowed to correct only the camera-plane sight offset; preserving
            // its authored forward depth is what keeps the hands out of the near clip.
            if (_anchoredDualPoseV2 && aimPoint != null && profile != null
                && profile.TryGetSightLine(aimPoint, out Vector3 anchoredRearWorld, out _))
            {
                Vector3 anchoredRoot = transform.InverseTransformPoint(anchoredRearWorld);
                Vector3 sightOffsetParent = cleanRootRotation * anchoredRoot;
                if (!IsFinite(anchoredRoot) || !IsFinite(sightOffsetParent)) return;
                localPosition = new Vector3(
                    camParent.x - sightOffsetParent.x,
                    camParent.y - sightOffsetParent.y,
                    profile.HasAdsCalibration
                        ? profile.AdsViewmodelLocalPosition.z
                        : 0f);
                return;
            }

            if (view.AlignAdsToSightAxis && aimPoint != null && profile != null && profile.AdsPivot != null)
            {
                // The authored aim animation owns the armature/camera placement. Exact
                // gun-axis correction is applied later to profile.AdsPivot only.
                Transform camNode = view.transform.Find("Armature/camera");
                if (camNode != null)
                {
                    Vector3 nodeRoot = transform.InverseTransformPoint(camNode.position);
                    Vector3 nodeOffsetParent = cleanRootRotation * nodeRoot;
                    if (!IsFinite(nodeRoot) || !IsFinite(nodeOffsetParent)) return;
                    localPosition = new Vector3(
                        camParent.x - nodeOffsetParent.x,
                        camParent.y - nodeOffsetParent.y,
                        0f);
                }
                return;
            }

            // Schema-7 anchored weapons still use the authored animation root as
            // the arms/camera baseline, even when the animation definition does
            // not expose the legacy aim clip flag. LPWGunPoseDriver owns only the
            // absolute gun layer below this root.
            if (_anchoredDualPoseV2 || _animationAds)
            {
                var camNode = view.transform.Find("Armature/camera");
                if (camNode != null)
                {
                    Vector3 nodeRoot = transform.InverseTransformPoint(camNode.position);
                    Vector3 nodeOffsetParent = cleanRootRotation * nodeRoot;
                    if (!IsFinite(nodeRoot) || !IsFinite(nodeOffsetParent)) return;
                    localPosition = new Vector3(
                        camParent.x - nodeOffsetParent.x,
                        camParent.y - nodeOffsetParent.y,
                        0f);
                    return;
                }
                // 非常规底座（无作者相机节点）：退回标记对中
            }

            if (aimPoint == null) return;
            // 正确方向 = 相机位置 − 参考点位置（把参考点推到相机中线的 x/y 上）
            Vector3 markerRoot = transform.InverseTransformPoint(aimPoint.position);
            Vector3 markerOffsetParent = cleanRootRotation * markerRoot;
            if (!IsFinite(markerRoot) || !IsFinite(markerOffsetParent)) return;
            localPosition = new Vector3(camParent.x - markerOffsetParent.x, camParent.y - markerOffsetParent.y, 0f);
        }

        /// <summary>解析当前瞄具的光轴瞄准数据（全部光学档位 + 校准表有记录 + 视图有 Optic 挂点），
        /// 折算到 root 局部系：眼点坐标、光轴方向（眼点→目标，默认挂点前向 -X）、滚转参考 up。
        /// 眼点/方向定义在挂点局部系（-X=前向/+Y=上），随挂点/枪身姿态走，与对位写入无反馈回路。</summary>
        private bool TryResolveOpticAimRoot(WeaponView view,
            out Vector3 eyeRoot, out Vector3 axisRoot, out Vector3 upRoot)
        {
            eyeRoot = default;
            axisRoot = default;
            upRoot = default;
            if (view == null || _weapon == null || _weapon.Definition == null) return false;
            var ctx = _weapon.CurrentOpticAim;
            if (ctx.ItemId == null) return false;
            if (ctx.Tier == OpticAimTier.None)
                return false;
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var calibration = catalog != null ? catalog.Calibration : null;
            if (calibration == null)
            {
                WarnOpticFallback(ctx, "缺少 AttachmentCalibration");
                return false;
            }
            if (!calibration.TryGetOpticAim(_weapon.Definition.CatalogItemId, ctx.ItemId, out var data))
            {
                WarnOpticFallback(ctx, "缺少眼点校准");
                return false;
            }
            var attachments = view.GetComponent<WeaponAttachmentView>();
            var socket = attachments != null ? attachments.GetSocketTransform(AttachmentSlotType.Optic) : null;
            if (socket == null)
            {
                WarnOpticFallback(ctx, "视图缺少 Attach_Optic 挂点");
                return false;
            }
            Vector3 eyeWorld = socket.TransformPoint(data.EyePointLocal);
            Vector3 axisWorld = socket.TransformDirection(data.AxisDirectionLocal);
            if (!IsFinite(eyeWorld) || !IsFinite(axisWorld) || axisWorld.sqrMagnitude < 1e-8f)
            {
                WarnOpticFallback(ctx, "眼点校准包含非法数值");
                return false;
            }
            axisWorld.Normalize();
            Vector3 rawUpWorld = socket.TransformDirection(Vector3.up);
            Vector3 upOrtho = Vector3.ProjectOnPlane(rawUpWorld, axisWorld);
            Vector3 upWorld = upOrtho.sqrMagnitude > 1e-8f ? upOrtho.normalized : Vector3.up;

            eyeRoot = transform.InverseTransformPoint(eyeWorld);
            axisRoot = transform.InverseTransformDirection(axisWorld).normalized;
            upRoot = transform.InverseTransformDirection(upWorld).normalized;
            if (!IsFinite(eyeRoot) || !IsFinite(axisRoot) || !IsFinite(upRoot)
                || axisRoot.sqrMagnitude < 0.5f)
            {
                WarnOpticFallback(ctx, "眼点校准包含非法数值");
                eyeRoot = default;
                axisRoot = default;
                upRoot = default;
                return false;
            }
            return true;
        }

        /// <summary>当前校准数据版本（无表时 0）。FPWeaponMotion 以此作废冻结解缓存，
        /// 校准窗口改表保存后无需重进场景。</summary>
        private int ResolveCalibrationVersion()
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var calibration = catalog != null ? catalog.Calibration : null;
            return calibration != null ? calibration.DataVersion : 0;
        }

        private void WarnOpticFallback(OpticAimContext ctx, string reason)
        {
            string weaponId = _weapon != null && _weapon.Definition != null ? _weapon.Definition.CatalogItemId : "<unknown>";
            string key = weaponId + "|" + ctx.ItemId + "|" + reason;
            if (!OpticFallbackWarnings.Add(key)) return;
            Debug.LogWarning($"[FPWeaponMotion] 真开镜校准降级为机瞄：{weaponId}/{ctx.ItemId}（{reason}）", this);
        }

        /// <summary>后坐剥离的稳定相机帧（抖动修复③）：CmFPCameraRecoil 在 Aim 阶段把
        /// WeaponController.CurrentRecoilRotation 乘进相机姿态，回声值与此处剔除值同源，
        /// 剔除后即得无后坐瞄准帧（与 FireRay 权威帧一致）。look 旋转保留、回声去除：
        /// 满开镜连射时瞄准解不再追逐正在上跳的相机。</summary>
        private Quaternion StableCameraRotation()
        {
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            if (_viewCamera == null) return Quaternion.identity;
            Quaternion recoil = _weapon != null ? _weapon.CurrentRecoilRotation : Quaternion.identity;
            if (!IsFinite(recoil) || !IsFinite(_viewCamera.transform.rotation))
                return Quaternion.identity;
            Quaternion stable = _viewCamera.transform.rotation * Quaternion.Inverse(recoil);
            return IsFinite(stable) ? NormalizeOrIdentity(stable) : Quaternion.identity;
        }

        /// <summary>求解 Legacy 动态枪轴满 ADS 目标（抖动修复④的缓存填充）。
        /// 只在合法失效点调用：入镜过渡、切枪、瞄具变化、抵达满 ADS 边界帧。
        /// 求解时枢轴复位到 identity（授权 Idle 层），测量全部为位姿不变量。</summary>
        private void SolveDynamicLpwGunAdsCache(WeaponView view, FPWeaponPoseProfile profile,
            Quaternion cleanRootRotation)
        {
            _dynamicSolved = false;
            if (view == null || profile == null || profile.AdsPivot == null || !view.AlignAdsToSightAxis)
                return;
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            if (_viewCamera == null) return;
            Transform pivot = profile.AdsPivot;
            Transform aimPoint = view.SightReference != null ? view.SightReference : view.Muzzle;
            if (aimPoint == null) return;

            // Identity is the authored Idle layer. Reset before measuring so the solve
            // never feeds last frame's ADS correction back into itself.
            pivot.localPosition = Vector3.zero;
            pivot.localRotation = Quaternion.identity;

            if (!TryComputeDynamicLpwAdsPivotPose(view, profile, pivot, aimPoint, cleanRootRotation,
                    out _dynamicGripPivot, out _dynamicTargetRotation, out _dynamicSightDepth))
                return;
            _dynamicSolved = IsFinite(_dynamicGripPivot) && IsFinite(_dynamicTargetRotation)
                && float.IsFinite(_dynamicSightDepth);
        }

        /// <summary>每帧应用缓存解：混合枢轴旋转（绕右手握点）+ 共享平移残差。
        /// 返回父系共享平移（抖动修复①：由统一写回消费，不再 world position +=）。
        /// 残差测量用稳定相机帧重建 desiredRearWorld——look 被追踪、后坐回声不被追踪，
        /// sway 仍被逐帧补偿（照门在满 ADS 保持稳定），但不与后坐形成闭环。</summary>
        private Vector3 ApplyDynamicLpwGunAdsPose(WeaponView view, FPWeaponPoseProfile profile, float adsBlend,
            Vector3 preSharedPosition, Quaternion preSharedRotation)
        {
            if (profile == null || profile.AdsPivot == null) return Vector3.zero;
            Transform pivot = profile.AdsPivot;
            if (adsBlend <= 0f || view == null || !view.AlignAdsToSightAxis || !_dynamicSolved)
            {
                // Identity is the authored Idle layer.
                pivot.localPosition = Vector3.zero;
                pivot.localRotation = Quaternion.identity;
                return Vector3.zero;
            }
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            if (_viewCamera == null) return Vector3.zero;

            // Interpolate the rotation itself, then derive the matching orbital offset.
            // The frozen solve keeps the grip invariant during aim-in.
            Quaternion blendedRotation = Quaternion.Slerp(Quaternion.identity, _dynamicTargetRotation, adsBlend);
            if (!IsFinite(blendedRotation) || !IsFinite(_dynamicGripPivot))
            {
                pivot.localPosition = Vector3.zero;
                pivot.localRotation = Quaternion.identity;
                return Vector3.zero;
            }
            pivot.localRotation = blendedRotation;
            Vector3 pivotPosition = _dynamicGripPivot - blendedRotation * _dynamicGripPivot;
            if (!IsFinite(pivotPosition))
            {
                pivot.localPosition = Vector3.zero;
                pivot.localRotation = Quaternion.identity;
                return Vector3.zero;
            }
            pivot.localPosition = pivotPosition;

            Transform aimPoint = view.SightReference != null ? view.SightReference : view.Muzzle;
            if (aimPoint == null || !profile.TryGetSightLine(aimPoint, out Vector3 blendedRearWorld, out _))
                return Vector3.zero;

            // Rear-sight offset in root space is pose-invariant; predict where it lands
            // under the pre-shared root pose (base+sway, no recoil, no shared translation)
            // so the correction can never consume the previous frame's kick.
            Vector3 rearInRoot = transform.InverseTransformPoint(blendedRearWorld);
            if (!IsFinite(rearInRoot) || !IsFinite(preSharedPosition)
                || !IsFinite(preSharedRotation)) return Vector3.zero;
            Vector3 predictedRearWorld = transform.parent.TransformPoint(
                preSharedPosition + preSharedRotation * rearInRoot);
            if (!IsFinite(predictedRearWorld)) return Vector3.zero;

            // Desired rear-sight point on the recoil-stripped aim line at the frozen
            // authored depth; rebuilt per frame so look rotation is tracked.
            Quaternion stableRotation = StableCameraRotation();
            Vector3 stableForward = (stableRotation * Vector3.forward).normalized;
            if (!IsFinite(stableForward) || stableForward.sqrMagnitude < .999f) return Vector3.zero;
            Vector3 desiredRearWorld = _viewCamera.transform.position + stableForward * _dynamicSightDepth;
            if (!IsFinite(desiredRearWorld) || !IsFinite(_dynamicSightDepth)) return Vector3.zero;

            // The root translation is shared by the animated arms and the gun. At full
            // ADS the rear sight is exact; during aim-in it converges without snapping.
            // Dynamic centering is a root-layer correction.  Never consume the
            // camera-forward component: authored root depth is an invariant for
            // every legacy gun (the old full-vector correction pushed AKM II
            // roughly 329 mm toward/through the near clip).
            Vector3 sharedWorld = Vector3.ProjectOnPlane(
                (desiredRearWorld - predictedRearWorld) * adsBlend,
                stableForward);
            Vector3 sharedParent = transform.parent.InverseTransformVector(sharedWorld);
            return IsFinite(sharedWorld) && IsFinite(sharedParent)
                && sharedParent.sqrMagnitude <= MaxAlignmentOffset * MaxAlignmentOffset
                ? sharedParent : Vector3.zero;
        }

        private bool TryComputeDynamicLpwAdsPivotPose(WeaponView view, FPWeaponPoseProfile profile,
            Transform pivot, Transform aimPoint, Quaternion cleanRootRotation,
            out Vector3 gripPivot, out Quaternion localRotation, out float authoredDepth)
        {
            gripPivot = Vector3.zero;
            localRotation = Quaternion.identity;
            authoredDepth = 0f;
            if (profile == null || profile.WeaponRoot == null || pivot.parent == null || aimPoint == null)
                return false;

            if (!profile.TryGetSightLine(aimPoint, out Vector3 rearWorld, out Vector3 frontWorld))
                return false;
            if (!IsFinite(rearWorld) || !IsFinite(frontWorld)
                || !IsFinite(cleanRootRotation) || !IsFinite(pivot.rotation)
                || !IsFinite(pivot.parent.rotation) || !IsFinite(transform.rotation)
                || !IsFinite(transform.parent.rotation)) return false;

            Vector3 rearPivot = pivot.InverseTransformPoint(rearWorld);
            Vector3 frontPivot = pivot.InverseTransformPoint(frontWorld);
            Vector3 sourceForward = frontPivot - rearPivot;
            if (!IsFinite(rearPivot) || !IsFinite(frontPivot)
                || !IsFinite(sourceForward) || sourceForward.sqrMagnitude < .000001f) return false;
            sourceForward.Normalize();

            // The weapon root is an animation/calibration transform, not a visual
            // sight frame.  Its authored roll can legitimately differ from the
            // receiver (AKM II exposed this as a small but visible cant).  Prefer
            // the explicit sight marker's up vector, which is authored in the same
            // source/model space as the sight line; only old views without a marker
            // fall back to the root for backwards compatibility.
            Transform sightFrame = profile.RearSight != null ? profile.RearSight : view.SightReference;
            Vector3 sourceUpWorld = sightFrame != null ? sightFrame.up : profile.WeaponRoot.up;
            Vector3 sourceUp = pivot.InverseTransformDirection(sourceUpWorld);
            sourceUp = Vector3.ProjectOnPlane(sourceUp, sourceForward).normalized;
            if (!IsFinite(sourceUp) || sourceUp.sqrMagnitude < .000001f) return false;

            // Target frame from the recoil-stripped camera (抖动修复③)：旧实现读取
            // 正在后坐的相机前向，满 ADS 连射时枪模追逐相机踢动→抖动被放大。
            Quaternion stableRotation = StableCameraRotation();
            Vector3 targetForwardWorld = (stableRotation * Vector3.forward).normalized;
            Vector3 targetUpWorld = Vector3.ProjectOnPlane(stableRotation * Vector3.up, targetForwardWorld).normalized;
            if (!IsFinite(targetForwardWorld) || !IsFinite(targetUpWorld)
                || targetForwardWorld.sqrMagnitude < .999f || targetUpWorld.sqrMagnitude < .999f) return false;
            Quaternion sourceFrame = Quaternion.LookRotation(sourceForward, sourceUp);
            Quaternion targetFrameWorld = Quaternion.LookRotation(targetForwardWorld, targetUpWorld);
            if (!IsFinite(sourceFrame) || !IsFinite(targetFrameWorld)) return false;

            // pivot.parent 的世界旋转含上一帧写入的根后坐旋转；root→武器骨链先折回
            // root 空间（位姿不变量），再与干净根旋转合成，缓存目标永不携带后坐。
            Quaternion chainLocal = Quaternion.Inverse(transform.rotation) * pivot.parent.rotation;
            Quaternion pivotParentWorldClean = transform.parent.rotation * cleanRootRotation * chainLocal;
            localRotation = Quaternion.Inverse(pivotParentWorldClean) * targetFrameWorld;
            if (!IsFinite(chainLocal) || !IsFinite(pivotParentWorldClean)
                || !IsFinite(localRotation)) return false;

            authoredDepth = ResolveAuthoredSightDepth(view, rearWorld);
            if (authoredDepth <= _viewCamera.nearClipPlane) return false;

            // The pivot is identity while measuring, so this is also the original point
            // in pivot-parent space. Keeping it invariant makes the gun rotate around the
            // weapon-specific RightHandGrip instead of around the armature origin.
            Transform rightGrip = profile.RightHandGrip;
            if (rightGrip == null) return false;
            gripPivot = pivot.InverseTransformPoint(rightGrip.position);
            return IsFinite(gripPivot) && IsFinite(localRotation) && float.IsFinite(authoredDepth);
        }

        private float ResolveAuthoredSightDepth(WeaponView view, Vector3 fallbackRearWorld)
        {
            Transform authoredSight = view.transform.Find("Armature/weapon/SightReference");
            Vector3 referenceWorld = authoredSight != null ? authoredSight.position : fallbackRearWorld;
            // Measure with FP_Weapon_Root at identity. Reading the already aligned world
            // position feeds the ADS translation back into depth and clamps every gun to
            // the near plane, producing the full-screen black receiver seen in testing.
            // Depth projects onto the recoil-stripped forward: the authored depth is an
            // invariant of the aim line, not of the kicking camera.
            Vector3 referenceRoot = transform.InverseTransformPoint(referenceWorld);
            Vector3 referenceAtIdentityWorld = transform.parent.TransformPoint(referenceRoot);
            float depth = Vector3.Dot(referenceAtIdentityWorld - _viewCamera.transform.position,
                StableCameraRotation() * Vector3.forward);
            if (!IsFinite(referenceWorld) || !IsFinite(referenceRoot)
                || !IsFinite(referenceAtIdentityWorld) || !IsFinite(depth)) return 0f;
            float near = IsFinite(_viewCamera.nearClipPlane) ? Mathf.Max(0.001f, _viewCamera.nearClipPlane) : 0.01f;
            return Mathf.Clamp(depth, near + .02f, 1.5f);
        }

        private static bool IsFinite(float value) => float.IsFinite(value);

        private static bool IsFinite(Vector2 value) => float.IsFinite(value.x)
            && float.IsFinite(value.y);

        private static bool IsFinite(Vector3 value) => float.IsFinite(value.x)
            && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool IsFinite(Quaternion value) => float.IsFinite(value.x)
            && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);

        private static Quaternion NormalizeOrIdentity(Quaternion value)
        {
            if (!IsFinite(value)) return Quaternion.identity;
            float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y
                + value.z * value.z + value.w * value.w);
            if (!IsFinite(magnitude) || magnitude < 0.000001f) return Quaternion.identity;
            return new Quaternion(value.x / magnitude, value.y / magnitude,
                value.z / magnitude, value.w / magnitude);
        }

        /// <summary>阻尼谐振子的解析精确步进（单轴）。对任意 dt 无条件稳定；
        /// 遇到非有限输入直接自愈归零（防止单次异常永久污染弹簧状态）。
        /// 供 FPWeaponMotion 与 LPWGunPoseDriver 共用（同程序集）。</summary>
        public static Vector3 StepDampedSpringAxis3(Vector3 position, Vector3 velocity,
            float omega, float dampingRatio, float dt, out Vector3 outVelocity)
        {
            outVelocity = velocity;
            if (!IsFinite(dt) || dt <= 0f) return IsFinite(position) ? position : Vector3.zero;
            if (!IsFinite(position) || !IsFinite(velocity)
                || !IsFinite(omega) || omega <= 0f || !IsFinite(dampingRatio))
            {
                outVelocity = Vector3.zero;
                return Vector3.zero;
            }
            dampingRatio = Mathf.Clamp(dampingRatio, 0f, 4f);
            var result = new Vector3(
                StepDampedSpringAxis(position.x, ref outVelocity.x, omega, dampingRatio, dt),
                StepDampedSpringAxis(position.y, ref outVelocity.y, omega, dampingRatio, dt),
                StepDampedSpringAxis(position.z, ref outVelocity.z, omega, dampingRatio, dt));
            if (!IsFinite(result) || !IsFinite(outVelocity))
            {
                outVelocity = Vector3.zero;
                return Vector3.zero;
            }
            return result;
        }

        /// <summary>x'' = -2ζω x' - ω² x 的闭式解一步推进（x 返回，v 以 ref 更新）。</summary>
        private static float StepDampedSpringAxis(float x, ref float v, float omega, float zeta, float dt)
        {
            if (!float.IsFinite(x) || !float.IsFinite(v))
            {
                v = 0f;
                return 0f;
            }
            if (!float.IsFinite(omega) || omega <= 0f || !float.IsFinite(zeta)
                || !float.IsFinite(dt) || dt <= 0f)
            {
                v = 0f;
                return 0f;
            }
            zeta = Mathf.Clamp(zeta, 0f, 4f);
            if (zeta < 1f)
            {
                // 欠阻尼：x(t) = e^(-ζωt)·(x0·cos(ωd·t) + b·sin(ωd·t)), b=(v0+ζωx0)/ωd
                float omegaD = omega * Mathf.Sqrt(1f - zeta * zeta);
                float decay = Mathf.Exp(-zeta * omega * dt);
                float cos = Mathf.Cos(omegaD * dt);
                float sin = Mathf.Sin(omegaD * dt);
                float b = (v + zeta * omega * x) / omegaD;
                float nextV = decay * (v * cos - (zeta * omega * v + omega * omega * x) / omegaD * sin);
                float nextX = decay * (x * cos + b * sin);
                if (!float.IsFinite(nextX) || !float.IsFinite(nextV)) { v = 0f; return 0f; }
                v = nextV;
                return nextX;
            }
            if (zeta < 1.0001f)
            {
                // 临界阻尼：x(t) = e^(-ωt)·(x0 + (v0+ωx0)·t)
                float decay = Mathf.Exp(-omega * dt);
                float c = v + omega * x;
                float nextV = decay * (c - omega * (x + c * dt));
                float nextX = decay * (x + c * dt);
                if (!float.IsFinite(nextX) || !float.IsFinite(nextV)) { v = 0f; return 0f; }
                v = nextV;
                return nextX;
            }
            // 过阻尼：两实根 r1/r2 的指数组合
            float s = Mathf.Sqrt(zeta * zeta - 1f);
            float r1 = -omega * (zeta - s);
            float r2 = -omega * (zeta + s);
            float denominator = r1 - r2;
            float a = (v - r2 * x) / denominator;
            float bCoeff = (r1 * x - v) / denominator;
            float e1 = Mathf.Exp(r1 * dt);
            float e2 = Mathf.Exp(r2 * dt);
            float overdampedV = a * r1 * e1 + bCoeff * r2 * e2;
            float overdampedX = a * e1 + bCoeff * e2;
            if (!float.IsFinite(overdampedX) || !float.IsFinite(overdampedV)) { v = 0f; return 0f; }
            v = overdampedV;
            return overdampedX;
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
