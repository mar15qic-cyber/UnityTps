using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class MainlineWeaponTuningTests
    {
        private const string BalancePath = "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset";

        [Test]
        public void EveryMainlineWeaponHasItsOwnValidAssetAndAdsSpeed()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            Assert.That(balance, Is.Not.Null);
            var expected = new Dictionary<string, (float pitch, float adsSpeed)>
            {
                ["pistol.day2"] = (.75f, .98f), ["handgun.02"] = (.85f, .97f),
                ["handgun.03"] = (1.15f, .95f), ["handgun.04"] = (.78f, .99f),
                ["rifle.day3"] = (1.25f, .75f), ["rifle.02"] = (.85f, .83f),
                ["rifle.03"] = (1.05f, .80f), ["smg.01"] = (.65f, .92f),
                ["smg.02"] = (.73f, .89f), ["smg.03"] = (.68f, .94f),
                ["smg.04"] = (.67f, .90f), ["smg.05"] = (.69f, .91f),
                ["shotgun.01"] = (2.30f, .84f), ["sniper.01"] = (1.75f, .96f),
                ["sniper.02"] = (3.40f, .58f), ["sniper.03"] = (1.85f, .78f)
            };
            var seen = new HashSet<string>();
            Assert.That(balance.MainlineProfiles, Has.Length.EqualTo(expected.Count));
            foreach (var profile in balance.MainlineProfiles)
            {
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile.Validate(out var error), Is.True, error);
                Assert.That(seen.Add(profile.WeaponId), Is.True, "Weapon IDs must be unique");
                Assert.That(expected.TryGetValue(profile.WeaponId, out var values), Is.True, profile.WeaponId);
                Assert.That(profile.Stat.Recoil.PitchDeg, Is.EqualTo(values.pitch).Within(.001f));
                Assert.That(profile.AdsGroundSpeedMultiplier, Is.EqualTo(values.adsSpeed).Within(.001f));
                Assert.That(balance.GetWeaponStat(profile.WeaponId).Damage, Is.EqualTo(profile.Stat.Damage));
            }
        }

        [Test]
        public void SniperHeadshotsAreLethalAndTorsoShotsRequireFollowup()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            foreach (var id in new[] { "sniper.01", "sniper.02", "sniper.03" })
            {
                var stat = balance.GetWeaponStat(id);
                var zones = balance.GetHitRegionMultipliers(id);
                Assert.That(stat.Damage, Is.LessThan(100), $"{id} torso must need a second shot");
                Assert.That(Mathf.RoundToInt(stat.Damage * zones.Head), Is.GreaterThanOrEqualTo(100),
                    $"{id} headshot must kill full health");
            }
            var shotgun = balance.GetWeaponStat("shotgun.01");
            Assert.That(shotgun.Damage, Is.EqualTo(60));
            Assert.That(shotgun.Ballistic.PelletCount, Is.EqualTo(9));
            int sum = 0;
            for (int i = 0; i < shotgun.Ballistic.PelletCount; i++)
                sum += shotgun.Damage / shotgun.Ballistic.PelletCount
                    + (i < shotgun.Damage % shotgun.Ballistic.PelletCount ? 1 : 0);
            Assert.That(sum, Is.EqualTo(shotgun.Damage));
        }

        [Test]
        public void EveryWeaponShotAppliesItsOwnCameraAndFireRayDebt()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            foreach (var profile in balance.MainlineProfiles)
            {
                var stat = profile.Stat;
                var resolved = new ResolvedWeaponStats(stat,
                    verticalRecoilDeg: stat.Recoil.PitchDeg, horizontalRecoilDeg: stat.Recoil.YawDeg,
                    aimRecoilScale: 1f, viewModelKickScale: 1f, recoilRecoveryScale: 1f,
                    patternScale: 1f, firstShotScale: 1f, adsRecoilScale: 1f, spreadScale: 1f);
                var hip = new WeaponRecoilState(123);
                var ads = new WeaponRecoilState(123);
                var hipShot = hip.OnShot(new WeaponFireContext(0f, 0f, false, true, false), resolved);
                var adsShot = ads.OnShot(new WeaponFireContext(1f, 0f, false, true, false), resolved);
                float expectedHip = stat.Recoil.PitchDeg * stat.Recoil.FirstShotMultiplier;
                Assert.That(hipShot.PitchKickDeg, Is.EqualTo(expectedHip).Within(.001f), profile.WeaponId);
                Assert.That(hip.CurrentOffset.x, Is.EqualTo(expectedHip).Within(.001f), profile.WeaponId);
                Assert.That(adsShot.PitchKickDeg,
                    Is.EqualTo(expectedHip * stat.Recoil.AdsRecoilMultiplier).Within(.001f), profile.WeaponId);
                Assert.That(ads.CurrentOffset.x, Is.EqualTo(adsShot.PitchKickDeg).Within(.001f), profile.WeaponId);
                Assert.That(Vector3.Angle(Vector3.forward, hip.OffsetRotation * Vector3.forward),
                    Is.GreaterThan(.1f), profile.WeaponId);
            }
        }
    }
}
