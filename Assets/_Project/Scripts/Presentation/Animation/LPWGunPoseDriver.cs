using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Schema-7 LPW gun-layer pose writer.  The animated weapon bone remains the
    /// parent frame; this component only blends the authored absolute HipGunPose
    /// and AdsGunPose and adds one procedural recoil spring around RightHandGrip.
    /// </summary>
    [DefaultExecutionOrder(30)]
    [DisallowMultipleComponent]
    public sealed class LPWGunPoseDriver : MonoBehaviour
    {
        [SerializeField] private FPWeaponPoseProfile poseProfile;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private WeaponController controller;
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

        private Vector3 _recoilRotation;
        private Vector3 _recoilRotationVelocity;
        private Vector3 _recoilPosition;
        private Vector3 _recoilPositionVelocity;

        public float Ads01 => aimState != null ? aimState.Ads01 : 0f;
        public Vector3 CurrentRecoilPosition => _recoilPosition;
        public Vector3 CurrentRecoilRotation => _recoilRotation;
        public int ProceduralFireCount { get; private set; }

        private void Awake()
        {
            poseProfile ??= GetComponent<FPWeaponPoseProfile>();
            aimState ??= GetComponentInParent<PlayerAimState>();
            controller ??= GetComponentInParent<WeaponController>();
        }

        private void OnEnable()
        {
            if (poseProfile == null) poseProfile = GetComponent<FPWeaponPoseProfile>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (controller != null) controller.OnShotFired += HandleShot;
            ResetRecoil();
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnShotFired -= HandleShot;
            ResetRecoil();
        }

        private void LateUpdate()
        {
            if (poseProfile == null || !poseProfile.HasCompleteAnchoredDualPoseV2
                || poseProfile.WeaponRoot == null)
                return;

            float dt = Mathf.Max(0f, Time.deltaTime);
            IntegrateSpring(dt);

            float ads = Mathf.Clamp01(Ads01);
            Vector3 basePosition;
            Quaternion baseRotation;
            if (ads >= 0.999999f)
            {
                // Full ADS is an absolute pose.  Do not read HipGunPose here so
                // editing Idle cannot move a settled sight picture.
                basePosition = poseProfile.AdsGunLocalPosition;
                baseRotation = poseProfile.AdsGunLocalRotation;
            }
            else if (ads <= 0.000001f)
            {
                basePosition = poseProfile.CalibratedRootLocalPosition;
                baseRotation = Quaternion.Euler(poseProfile.CalibratedRootLocalEulerAngles);
            }
            else
            {
                basePosition = Vector3.Lerp(
                    poseProfile.CalibratedRootLocalPosition,
                    poseProfile.AdsGunLocalPosition,
                    ads);
                baseRotation = Quaternion.Slerp(
                    Quaternion.Euler(poseProfile.CalibratedRootLocalEulerAngles),
                    poseProfile.AdsGunLocalRotation,
                    ads);
            }

            Transform root = poseProfile.WeaponRoot;
            Transform grip = poseProfile.RightHandGrip;
            Quaternion recoilRotation = Quaternion.Euler(_recoilRotation);
            Quaternion finalRotation = baseRotation * recoilRotation;
            Vector3 gripLocal = grip != null ? grip.localPosition : Vector3.zero;
            Vector3 baseGrip = baseRotation * gripLocal;
            Vector3 finalGrip = finalRotation * gripLocal;

            root.localPosition = basePosition + baseGrip - finalGrip + _recoilPosition;
            root.localRotation = finalRotation;
        }

        private void HandleShot(WeaponShot shot)
        {
            if (poseProfile == null || !poseProfile.HasCompleteAnchoredDualPoseV2) return;

            ProceduralFireCount++;
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

        public void ResetRecoil()
        {
            _recoilRotation = Vector3.zero;
            _recoilRotationVelocity = Vector3.zero;
            _recoilPosition = Vector3.zero;
            _recoilPositionVelocity = Vector3.zero;
        }

        private void IntegrateSpring(float dt)
        {
            if (dt <= 0f) return;
            // 解析精确阻尼谐振子步进（与 FPWeaponMotion 同源修复）：显式欧拉在
            // dt 尖峰（卡顿/低帧率）下发散直至 NaN 且无自愈路径——曾把枪层姿态
            // 炸到天文数字并触发 AABB 损坏断言。解析解对任意 dt 无条件稳定。
            float omega = recoilSpringFrequency * Mathf.PI * 2f;
            _recoilRotation = Game.Presentation.Camera.FPWeaponMotion.StepDampedSpringAxis3(
                _recoilRotation, _recoilRotationVelocity, omega, recoilDampingRatio, dt,
                out _recoilRotationVelocity);
            _recoilPosition = Game.Presentation.Camera.FPWeaponMotion.StepDampedSpringAxis3(
                _recoilPosition, _recoilPositionVelocity, omega, recoilDampingRatio, dt,
                out _recoilPositionVelocity);
        }
    }
}
