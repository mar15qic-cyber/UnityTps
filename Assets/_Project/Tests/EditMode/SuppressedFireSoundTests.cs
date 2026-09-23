using System.Collections.Generic;
using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 消音枪声（2026-09-03，按族 5 条 AI 生成）：
    /// WeaponAudioView.SelectFireVariants 选池语义（消音池优先/空池诚实降级/未消音普通池）+
    /// 配件目录 isSuppressor 标记（恰 5 条枪口条目）+ 5 族 Profile 消音变体接线。
    /// </summary>
    public sealed class SuppressedFireSoundTests
    {
        private static WeaponAudioProfile ProfileWithVariants(bool withSuppressed)
        {
            var profile = ScriptableObject.CreateInstance<WeaponAudioProfile>();
            var clip = AudioClip.Create("normal", 16, 1, 44100, false);
            profile.FireVariants = new[]
            {
                new WeaponAudioProfile.ClipEntry { Clip = clip, VolumeRange = Vector2.one, PitchRange = Vector2.one }
            };
            if (withSuppressed)
            {
                var supClip = AudioClip.Create("suppressed", 16, 1, 44100, false);
                profile.FireVariantsSuppressed = new[]
                {
                    new WeaponAudioProfile.ClipEntry { Clip = supClip, VolumeRange = Vector2.one, PitchRange = Vector2.one }
                };
            }
            return profile;
        }

        [Test]
        public void SelectFireVariants_SuppressedWithVariants_ReturnsSuppressedPool()
        {
            var profile = ProfileWithVariants(withSuppressed: true);
            var selected = WeaponAudioView.SelectFireVariants(profile, isSuppressed: true);
            Assert.AreSame(profile.FireVariantsSuppressed, selected);
            Assert.AreEqual("suppressed", selected[0].Clip.name);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void SelectFireVariants_SuppressedPoolEmpty_FallsBackToNormal()
        {
            // 诚实降级（§9-11 拍板）：没配消音变体就用普通池，不假装完成
            var profile = ProfileWithVariants(withSuppressed: false);
            Assert.AreSame(profile.FireVariants, WeaponAudioView.SelectFireVariants(profile, isSuppressed: true));
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void SelectFireVariants_NotSuppressed_ReturnsNormalPool()
        {
            var profile = ProfileWithVariants(withSuppressed: true);
            Assert.AreSame(profile.FireVariants, WeaponAudioView.SelectFireVariants(profile, isSuppressed: false));
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void SelectFireVariants_NullProfile_ReturnsNull()
        {
            Assert.IsNull(WeaponAudioView.SelectFireVariants(null, isSuppressed: true));
        }

        [Test]
        public void Catalog_SuppressorFlags_ExactlyFiveMuzzleEntries()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(
                "Assets/_Project/Resources/AttachmentAssetCatalog.asset");
            Assert.NotNull(catalog, "配件目录缺失");
            var flagged = new List<string>();
            foreach (var e in catalog.Entries)
                if (e != null && e.isSuppressor) flagged.Add(e.itemId);
            Assert.That(flagged, Is.EquivalentTo(new[]
            {
                "attach.lpfp.muffler.01",  // 经典消音器（原生武器）
                "attach.rifle.muzzle",     // 通行证步枪消音器
                "attach.pistol.muzzle",    // 通行证手枪消音器
            }));
            foreach (var e in catalog.Entries)
                if (e != null && e.isSuppressor)
                    Assert.AreEqual(AttachmentSlotType.Muzzle, e.slot, $"{e.itemId} 消音标记必须在枪口槽");
        }

        [Test]
        public void FamilyProfiles_HaveSuppressedVariantsWired()
        {
            // 狙击族无枪口槽（配件矩阵），不在消音覆盖范围
            string[] profiles =
            {
                "AudioProfile_Handgun", "AudioProfile_Rifle", "AudioProfile_Rifle02",
                "AudioProfile_Shotgun", "AudioProfile_SMG",
            };
            foreach (var name in profiles)
            {
                var profile = AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>(
                    $"Assets/_Project/ScriptableObjects/Audio/{name}.asset");
                Assert.NotNull(profile, $"{name} 缺失");
                Assert.NotNull(profile.FireVariantsSuppressed, $"{name} 消音池为 null");
                Assert.GreaterOrEqual(profile.FireVariantsSuppressed.Length, 1, $"{name} 消音池为空");
                Assert.NotNull(profile.FireVariantsSuppressed[0].Clip, $"{name} 消音 clip 未接线");
                Assert.That(profile.FireVariantsSuppressed[0].Clip.name, Does.StartWith("Fire_Suppressed_"));
            }
        }
    }
}
