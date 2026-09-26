using UnityEngine;

namespace Game.Core
{
    public enum HitBodyRegion : byte
    {
        Torso = 0,
        Head = 1,
        Arm = 2,
        Leg = 3
    }

    [System.Serializable]
    public struct HitRegionMultipliers
    {
        public float Head;
        public float Torso;
        public float Arm;
        public float Leg;

        public float For(HitBodyRegion region) => region == HitBodyRegion.Head ? Head
            : region == HitBodyRegion.Arm ? Arm : region == HitBodyRegion.Leg ? Leg : Torso;

        public static HitRegionMultipliers Standard => new HitRegionMultipliers
        {
            Head = 1.5f, Torso = 1f, Arm = .8f, Leg = .7f
        };
    }

    /// <summary>One authoritative, editable data asset for each LPFP weapon.</summary>
    [CreateAssetMenu(menuName = "UnityFps/Balance/Weapon Tuning Profile")]
    public sealed class WeaponTuningProfile : ScriptableObject
    {
        public string WeaponId;
        public WeaponStat Stat;
        [Range(.1f, 1f)] public float AdsGroundSpeedMultiplier = 1f;
        public HitRegionMultipliers HitRegions = HitRegionMultipliers.Standard;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(WeaponId)) error = "empty WeaponId";
            else if (Stat.Damage < 1 || Stat.Damage > 500 || Stat.Rpm < 1 || Stat.Rpm > 1800
                || Stat.MagSize < 1 || Stat.MagSize > 200 || Stat.ReserveAmmo < 0
                || !InRange(Stat.ReloadTime, .05f, 20f) || !InRange(Stat.Spread, 0f, 10f)
                || !InRange(Stat.MaxRange, 1f, 2000f) || !InRange(Stat.AdsFov, 5f, 120f))
                error = "invalid base weapon stat";
            else if (!InRange(Stat.Recoil.PitchDeg, .05f, 15f)
                || !InRange(Stat.Recoil.YawDeg, 0f, 5f)
                || !InRange(Stat.Recoil.FirstShotMultiplier, 1f, 3f)
                || !InRange(Stat.Recoil.Accumulation, .5f, 2f)
                || !InRange(Stat.Recoil.MaxAccumulation, 1f, 30f)
                || !InRange(Stat.Recoil.RecoveryDelay, 0f, 1f)
                || !InRange(Stat.Recoil.RecoverySpeed, .5f, 20f)
                || !InRange(Stat.Recoil.SpringFrequency, 2f, 20f)
                || !InRange(Stat.Recoil.SpringDamping, .1f, 1f)
                || !InRange(Stat.Recoil.AdsRecoilMultiplier, .1f, 1f)
                || !InRange(Stat.Recoil.ShakePositionAmplitude, 0f, 1f)
                || !InRange(Stat.Recoil.ViewModelKickBack, 0f, .2f)
                || !InRange(Stat.Recoil.ViewModelKickPitch, 0f, 15f))
                error = "invalid recoil profile";
            else if (!InRange(Stat.Accuracy.BaseHipSpread, .001f, 10f)
                || !InRange(Stat.Accuracy.BaseAdsSpread, .001f, Stat.Accuracy.BaseHipSpread)
                || !InRange(Stat.Accuracy.MovementSpreadMax, 0f, 10f)
                || !InRange(Stat.Accuracy.SprintSpreadExtra, 0f, 10f)
                || !InRange(Stat.Accuracy.AirborneSpreadExtra, 0f, 10f)
                || !InRange(Stat.Accuracy.ShotBloomPerShot, 0f, 5f)
                || !InRange(Stat.Accuracy.MaxBloom, Stat.Accuracy.ShotBloomPerShot, 10f)
                || !InRange(Stat.Accuracy.BloomRecoveryDelay, 0f, 1f)
                || !InRange(Stat.Accuracy.BloomRecoverySpeed, 1f, 60f))
                error = "invalid accuracy profile";
            else if (Stat.Ballistic.PelletCount < 1 || Stat.Ballistic.PelletCount > 16
                || !InRange(Stat.Ballistic.PelletSpread, 0f, 15f))
                error = "invalid ballistic profile";
            else if (!InRange(AdsGroundSpeedMultiplier, .1f, 1f))
                error = "invalid ADS ground speed";
            else if (!InRange(HitRegions.Head, .1f, 5f)
                || !InRange(HitRegions.Torso, .1f, 5f)
                || !InRange(HitRegions.Arm, .1f, 5f)
                || !InRange(HitRegions.Leg, .1f, 5f))
                error = "invalid hit region multipliers";
            return error == null;
        }

        private static bool InRange(float value, float minimum, float maximum)
            => float.IsFinite(value) && value >= minimum && value <= maximum;
    }
}
