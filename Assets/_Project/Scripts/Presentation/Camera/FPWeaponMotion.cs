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
        [SerializeField, Min(0.1f)] private float recoilSpringFrequency = 11f;
        [SerializeField, Range(0f, 1f)] private float recoilDampingRatio = 0.6f;

        [Header("ADS")]
        [SerializeField, Range(0f, 1f)] private float adsMotionDamping = 0.85f;

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
        private Vector2 _recoil;
        private Vector2 _recoilVelocity;
        private float _time;
        private Vector3 _aimLocalPosition;
        private bool _aimPoseDirty = true;

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
            // CP5：kick 幅度数据驱动——来自 WeaponShot.Recoil（Gameplay 五步顺序第④步产物，
            // 已含首枪/累计/ADS/ViewModelKick 修饰）。回中弹簧频率/阻尼是视觉层参数（与相机
            // 弹簧独立、不影响弹道），保留组件序列化（Docs/13 §13.3 决策记录）。
            _recoilVelocity += new Vector2(shot.Recoil.ViewModelPitchDeg, shot.Recoil.ViewModelBackM)
                * (recoilSpringFrequency * Mathf.PI * 2f);
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            _time += dt;

            if (_weapon != null && _weapon.Definition != _currentDefinition)
            {
                _currentDefinition = _weapon.Definition;
                _aimPoseDirty = true;
            }
            if (_aimPoseDirty) TryComputeAimPose();

            float adsBlend = _rig != null ? _rig.AdsBlend : 0f;
            float motionScale = 1f - adsBlend * adsMotionDamping;

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

            // ---- recoil 弹簧（x=上抬°，y=后坐 m）----
            float omega = recoilSpringFrequency * Mathf.PI * 2f;
            float damping = 2f * recoilDampingRatio * omega;
            _recoilVelocity += (-omega * omega * _recoil - damping * _recoilVelocity) * dt;
            _recoil += _recoilVelocity * dt;

            // ---- 合成：腰射姿态（identity）↔ ADS 姿态 + 各动效 ----
            Vector3 basePos = Vector3.Lerp(Vector3.zero, _aimLocalPosition, adsBlend);
            transform.localPosition = basePos + new Vector3(
                (swayX + bobX) * motionScale,
                (swayY + bobY) * motionScale + breath * breathingAmplitude,
                _recoil.y * motionScale);
            transform.localRotation = Quaternion.Euler(
                (swayPitch + _recoil.x) * motionScale,
                swayYaw * motionScale,
                0f);
        }

        /// <summary>把激活视图的瞄准参考点平移到 FP View Camera 视口中心线（程序化 ADS 姿态）。
        /// 优先 SightReference（瞄具线，物理 Muzzle 与瞄具线有 sight-height 高度差）；
        /// 未配置的武器回退 Muzzle（行为与旧版一致）。</summary>
        private void TryComputeAimPose()
        {
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            var view = FindActiveView();
            var aimPoint = view != null ? (view.SightReference != null ? view.SightReference : view.Muzzle) : null;
            if (aimPoint == null || _viewCamera == null) return;

            // 参考点在 viewCam 本地空间的 (x,y) 就是要抵消的偏差（两相机朝向一致，平移同空间可加）
            Vector3 aimInViewCam = _viewCamera.transform.InverseTransformPoint(aimPoint.position);
            _aimLocalPosition = new Vector3(aimInViewCam.x, aimInViewCam.y, 0f);
            _aimPoseDirty = false;
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
