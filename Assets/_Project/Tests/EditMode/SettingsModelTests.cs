using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using Game.Gameplay.Settings;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>SettingsModel + SettingsKeyMap 纯逻辑契约（音量/分辨率/锁帧/键位映射）。</summary>
    public sealed class SettingsModelTests
    {
        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(SettingsModel.MasterVolumeKey);
            PlayerPrefs.DeleteKey(SettingsModel.MusicVolumeKey);
            PlayerPrefs.DeleteKey(SettingsModel.SensitivityKey);
            PlayerPrefs.DeleteKey(SettingsModel.ResolutionKey);
            PlayerPrefs.DeleteKey(SettingsModel.FullscreenKey);
            PlayerPrefs.DeleteKey(SettingsModel.FrameCapKey);
            foreach (var b in SettingsKeyMap.Bindings) PlayerPrefs.DeleteKey(b.prefsKey);
            SettingsKeyMap.InvalidateCache();
            PlayerPrefs.Save();
        }

        // ---- 音量 ----

        [Test]
        public void MasterVolume_ClampedToUnit()
        {
            SettingsModel.MasterVolume = 1.6f;
            Assert.That(SettingsModel.MasterVolume, Is.EqualTo(1f));
            SettingsModel.MasterVolume = -0.2f;
            Assert.That(SettingsModel.MasterVolume, Is.EqualTo(0f));
            SettingsModel.MasterVolume = 0.35f;
            Assert.That(SettingsModel.MasterVolume, Is.EqualTo(0.35f).Within(0.0001f));
        }

        [Test]
        public void ApplyMasterVolume_WritesAudioListener()
        {
            SettingsModel.MasterVolume = 0.42f;
            SettingsModel.ApplyMasterVolume();
            Assert.That(AudioListener.volume, Is.EqualTo(0.42f).Within(0.0001f));
            SettingsModel.MasterVolume = 1f;
            SettingsModel.ApplyMasterVolume();
        }

        // ---- 画质 ----

        [Test]
        public void Resolution_ParsesAndRoundTrips()
        {
            SettingsModel.Resolution = (2560, 1440);
            Assert.That(SettingsModel.Resolution, Is.EqualTo((2560, 1440)));
            Assert.That(PlayerPrefs.GetString(SettingsModel.ResolutionKey), Is.EqualTo("2560x1440"));
        }

        [Test]
        public void ParseResolution_InvalidFallsBack()
        {
            Assert.That(SettingsModel.ParseResolution("garbage", (1920, 1080)), Is.EqualTo((1920, 1080)));
            Assert.That(SettingsModel.ParseResolution("100x100", (1920, 1080)), Is.EqualTo((1920, 1080)), "below min should fall back");
            Assert.That(SettingsModel.ParseResolution("1600x900", (1920, 1080)), Is.EqualTo((1600, 900)));
        }

        [Test]
        public void FrameCap_FormatsAndClamps()
        {
            SettingsModel.FrameCap = 120;
            Assert.That(SettingsModel.FrameCap, Is.EqualTo(120));
            Assert.That(SettingsModel.FormatFrameCap(120), Is.EqualTo("120 FPS"));
            Assert.That(SettingsModel.FormatFrameCap(-1), Is.EqualTo("无限制"));
            Assert.That(SettingsModel.FormatFrameCap(0), Is.EqualTo("无限制"));
        }

        [Test]
        public void ApplyFrameCap_SetsTargetFrameRateAndDisablesVsync()
        {
            SettingsModel.FrameCap = 144;
            SettingsModel.ApplyFrameCap();
            Assert.That(Application.targetFrameRate, Is.EqualTo(144));
            Assert.That(QualitySettings.vSyncCount, Is.EqualTo(0));

            SettingsModel.FrameCap = -1;
            SettingsModel.ApplyFrameCap();
            Assert.That(Application.targetFrameRate, Is.EqualTo(-1));
        }

        // ---- 键位映射 ----

        [Test]
        public void KeyMap_DefaultsMatchLegacyHardcoded()
        {
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveForward), Is.EqualTo(Key.W));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveBack), Is.EqualTo(Key.S));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveLeft), Is.EqualTo(Key.A));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveRight), Is.EqualTo(Key.D));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Sprint), Is.EqualTo(Key.LeftShift));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Jump), Is.EqualTo(Key.Space));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Reload), Is.EqualTo(Key.R));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Slot1), Is.EqualTo(Key.Digit1));
        }

        [Test]
        public void KeyMap_SetPersistsAndOverridesDefault()
        {
            SettingsKeyMap.Set(SettingsKeyMap.Action.Reload, Key.F);
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Reload), Is.EqualTo(Key.F));
            Assert.That(SettingsKeyMap.IsCustomized(SettingsKeyMap.Action.Reload), Is.True);

            SettingsKeyMap.InvalidateCache(); // simulate reload from prefs
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Reload), Is.EqualTo(Key.F), "persisted value should survive cache reload");
        }

        [Test]
        public void KeyMap_ResetRestoresDefault()
        {
            SettingsKeyMap.Set(SettingsKeyMap.Action.Jump, Key.C);
            SettingsKeyMap.Reset(SettingsKeyMap.Action.Jump);
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Jump), Is.EqualTo(Key.Space));
            Assert.That(SettingsKeyMap.IsCustomized(SettingsKeyMap.Action.Jump), Is.False);
        }

        [Test]
        public void KeyMap_BindingsCoverMoveSprintJumpReloadAndSlots()
        {
            Assert.That(SettingsKeyMap.Bindings.Length, Is.GreaterThanOrEqualTo(10));
            foreach (SettingsKeyMap.Action action in System.Enum.GetValues(typeof(SettingsKeyMap.Action)))
                Assert.That(SettingsKeyMap.Find(action), Is.Not.Null, $"binding for {action} missing");
        }
    }
}
