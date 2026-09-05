using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Player;
using Game.Gameplay.Settings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 共享设置 Phase B 契约锁定：
    /// ① SettingsDraft 捕获/应用/回滚/恢复默认（含 PlayerPrefs 层验证）；
    /// ② 键位冲突规则（冲突检测/交换/Escape 保留/非持久预览不落盘）；
    /// ③ AudioBus 线性合成边界（0=真静音、1=不放大、分类独立）；
    /// ④ 灵敏度唯一应用点（InputReader.ComputeLookDelta 纯函数）；
    /// ⑤ SettingsModel 迁移后 SFX 新键 + 命名空间可用性。
    /// </summary>
    public sealed class SettingsRuntimeDraftTests
    {
        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(SettingsModel.MasterVolumeKey);
            PlayerPrefs.DeleteKey(SettingsModel.MusicVolumeKey);
            PlayerPrefs.DeleteKey(SettingsModel.SfxVolumeKey);
            PlayerPrefs.DeleteKey(SettingsModel.SensitivityKey);
            PlayerPrefs.DeleteKey(SettingsModel.ResolutionKey);
            PlayerPrefs.DeleteKey(SettingsModel.FullscreenKey);
            PlayerPrefs.DeleteKey(SettingsModel.FrameCapKey);
            PlayerPrefs.DeleteKey(AdsInputMode.PrefsKey);
            foreach (var b in SettingsKeyMap.Bindings) PlayerPrefs.DeleteKey(b.prefsKey);
            SettingsKeyMap.InvalidateCache();
            AudioBus.MusicVolume = 1f;
            AudioBus.SfxVolume = 1f;
            SettingsRuntime.ResetForTests();
            PlayerPrefs.Save();
        }

        // ---- AudioBus 音量合成（线性域；0 = 真静音） ----

        [Test]
        public void AudioBus_ZeroMeansSilence()
        {
            AudioBus.SfxVolume = 0f;
            Assert.That(AudioBus.ComputeVolume(0.8f, AudioBus.Category.Sfx), Is.EqualTo(0f), "0 值必须真静音（线性 0）");
            AudioBus.MusicVolume = 0f;
            Assert.That(AudioBus.ComputeVolume(1f, AudioBus.Category.Music), Is.EqualTo(0f));
        }

        [Test]
        public void AudioBus_FullFactorPreservesBaseVolume()
        {
            AudioBus.SfxVolume = 1f;
            Assert.That(AudioBus.ComputeVolume(0.6f, AudioBus.Category.Sfx), Is.EqualTo(0.6f).Within(0.0001f), "因子 1 不得放大或衰减基础音量");
        }

        [Test]
        public void AudioBus_FactorsIndependentAndClamped()
        {
            AudioBus.SfxVolume = 0.25f;
            AudioBus.MusicVolume = 0.5f;
            Assert.That(AudioBus.ComputeVolume(1f, AudioBus.Category.Sfx), Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(AudioBus.ComputeVolume(1f, AudioBus.Category.Music), Is.EqualTo(0.5f).Within(0.0001f));
            AudioBus.SetCategoryVolume(AudioBus.Category.Sfx, 7f);
            Assert.That(AudioBus.Factor(AudioBus.Category.Sfx), Is.EqualTo(1f).Within(0.0001f), "超界输入夹紧到 1");
            AudioBus.SetCategoryVolume(AudioBus.Category.Music, -3f);
            Assert.That(AudioBus.Factor(AudioBus.Category.Music), Is.EqualTo(0f).Within(0.0001f), "负值夹紧到 0（静音）");
        }

        // ---- SettingsRuntime 实时层 ----

        [Test]
        public void SettingsRuntime_SetLiveAppliesImmediatelyWithoutPersist()
        {
            SettingsRuntime.Initialize();
            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, 2.5f);
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(2.5f).Within(0.0001f));
            Assert.That(PlayerPrefs.GetFloat(SettingsModel.SensitivityKey, -1f), Is.EqualTo(-1f), "实时预览不得写 PlayerPrefs");

            SettingsRuntime.SetLive(SensitivityTarget.Sfx, 0.3f);
            Assert.That(AudioBus.Factor(AudioBus.Category.Sfx), Is.EqualTo(0.3f).Within(0.0001f), "SFX 因子即时生效（消费点在音频源侧）");

            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, 99f);
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(5f).Within(0.0001f), "灵敏度上限 5");

            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, 0.01f);
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(0.1f).Within(0.0001f), "灵敏度下限 0.1");
        }

        // ---- SettingsDraft：捕获 / 应用 / 回滚 / 默认 ----

        [Test]
        public void Draft_ApplyPersistsAndReloadKeepsValues()
        {
            var draft = SettingsDraft.CreateDefaults();
            draft.MasterVolume = 0.4f;
            draft.SfxVolume = 0.6f;
            draft.Sensitivity = 2.2f;
            draft.FrameCap = 144;
            draft.PreviewKey(SettingsKeyMap.Action.Reload, Key.F);
            draft.ApplyAndPersist();

            Assert.That(PlayerPrefs.GetFloat(SettingsModel.MasterVolumeKey), Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(PlayerPrefs.GetFloat(SettingsModel.SfxVolumeKey), Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(PlayerPrefs.GetFloat(SettingsModel.SensitivityKey), Is.EqualTo(2.2f).Within(0.0001f));
            Assert.That(PlayerPrefs.GetInt(SettingsModel.FrameCapKey), Is.EqualTo(144));
            Assert.That(SettingsKeyMap.IsCustomized(SettingsKeyMap.Action.Reload), Is.True, "应用后键位必须持久化");

            // 模拟重启：清缓存重读 → 持久值仍生效
            SettingsKeyMap.InvalidateCache();
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Reload), Is.EqualTo(Key.F));
            SettingsRuntime.ReloadFromPersistedAndApply();
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(2.2f).Within(0.0001f));
            Assert.That(SettingsRuntime.MasterVolume, Is.EqualTo(0.4f).Within(0.0001f));
        }

        [Test]
        public void Draft_PreviewThenCancelRollsBackEverything()
        {
            // 捕获点（用户当前持久值 → 实时层，模拟启动装载）
            SettingsModel.MasterVolume = 0.8f;
            SettingsModel.Sensitivity = 1.7f;
            SettingsRuntime.ReloadFromPersistedAndApply();
            SettingsKeyMap.Set(SettingsKeyMap.Action.Jump, Key.Space, persist: false); // 锚定基线（仅缓存，无持久键）
            var draft = SettingsDraft.CaptureFromCurrent();

            // 用户拖滑杆 + 重绑（全部即时预览）
            draft.MasterVolume = 0.1f;
            draft.Sensitivity = 3f;
            SettingsRuntime.SetLive(SensitivityTarget.Master, 0.1f);
            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, 3f);
            draft.PreviewKey(SettingsKeyMap.Action.Jump, Key.C);

            Assert.That(SettingsRuntime.MasterVolume, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Jump), Is.EqualTo(Key.C));

            // 取消 → 全部回滚到捕获点；PlayerPrefs 未被预览污染
            draft.RestoreLive();
            Assert.That(SettingsRuntime.MasterVolume, Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(1.7f).Within(0.0001f));
            Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.Jump), Is.EqualTo(Key.Space));
            Assert.That(PlayerPrefs.HasKey(SettingsKeyMap.Find(SettingsKeyMap.Action.Jump).prefsKey), Is.False,
                "取消后不得残留预览键位");
        }

        [Test]
        public void Draft_ResetToDefaults_PreviewsButDoesNotPersist()
        {
            SettingsModel.MasterVolume = 0.2f;
            SettingsRuntime.ReloadFromPersistedAndApply();
            var draft = SettingsDraft.CaptureFromCurrent();
            draft.ResetToDefaults();
            draft.PreviewAllLive();
            Assert.That(SettingsRuntime.MasterVolume, Is.EqualTo(SettingsModel.DefaultMasterVolume).Within(0.0001f));
            Assert.That(draft.Sensitivity, Is.EqualTo(SettingsModel.DefaultSensitivity).Within(0.0001f));
            Assert.That(draft.FrameCap, Is.EqualTo(60));
            Assert.That(draft.Resolution, Is.EqualTo((1920, 1080)));

            // 恢复默认后取消 → 回到捕获点（而非默认值）
            draft.RestoreLive();
            Assert.That(SettingsRuntime.MasterVolume, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(SettingsRuntime.Sensitivity, Is.EqualTo(SettingsModel.DefaultSensitivity).Within(0.0001f));
        }

        // ---- 灵敏度唯一应用点 ----

        [Test]
        public void LookDelta_UserSensitivityAppliedExactlyOnce()
        {
            var raw = new Vector2(10f, 6f);
            // 用户灵敏度 2 + 开镜倍率 0.5 = 恰好一次合成
            var expected = raw * 2f * 0.5f;
            var got = InputReader.ComputeLookDelta(raw, adsSensitivityScale: 0.5f, userSensitivity: 2f);
            Assert.That(got.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(got.y, Is.EqualTo(expected.y).Within(0.0001f));

            // 消费方契约：拿到的已是最终值，任何二次缩放都是重复应用（锁定纯函数语义防止回归）
            var neutral = InputReader.ComputeLookDelta(raw, 1f, 1f);
            Assert.That(neutral, Is.EqualTo(raw), "两个中性倍率必须完全透传原始像素增量");
        }

        // ---- 键位冲突规则 ----

        [Test]
        public void KeybindRules_ConflictDetection()
        {
            // 默认键位：W=前进，A=左移
            var outcome = KeybindRules.Evaluate(SettingsKeyMap.Action.MoveForward, Key.A, out var conflicted);
            Assert.That(outcome, Is.EqualTo(RebindOutcome.Conflict));
            Assert.That(conflicted, Is.EqualTo(SettingsKeyMap.Action.MoveLeft));
            Assert.That(KeybindRules.FindConflict(SettingsKeyMap.Action.MoveForward, Key.W), Is.Null, "自身当前键不算冲突");
        }

        [Test]
        public void KeybindRules_EscapeReserved()
        {
            var outcome = KeybindRules.Evaluate(SettingsKeyMap.Action.MoveForward, Key.Escape, out var conflicted);
            Assert.That(outcome, Is.EqualTo(RebindOutcome.Reserved));
            Assert.That(conflicted, Is.Null);
            Assert.That(KeybindRules.IsReserved(Key.Escape), Is.True, "Escape 保留为系统菜单键");
        }

        [Test]
        public void KeybindRules_ApplyWithSwap_ExchangesBothKeys()
        {
            try
            {
                var swapped = KeybindRules.ApplyWithSwap(SettingsKeyMap.Action.MoveForward, Key.A, persist: false);
                Assert.That(swapped, Is.EqualTo(SettingsKeyMap.Action.MoveLeft));
                Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveForward), Is.EqualTo(Key.A));
                Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveLeft), Is.EqualTo(Key.W), "原占用者拿到前进的旧键");
                Assert.That(PlayerPrefs.HasKey(SettingsKeyMap.Find(SettingsKeyMap.Action.MoveForward).prefsKey), Is.False,
                    "persist=false 只改运行时缓存，不落盘（应用才保存）");
            }
            finally
            {
                SettingsKeyMap.Reset(SettingsKeyMap.Action.MoveForward, persist: false);
                SettingsKeyMap.Reset(SettingsKeyMap.Action.MoveLeft, persist: false);
            }
        }

        [Test]
        public void KeybindRules_NoConflictKeepsOthersUntouched()
        {
            try
            {
                var swapped = KeybindRules.ApplyWithSwap(SettingsKeyMap.Action.MoveForward, Key.T, persist: false);
                Assert.That(swapped, Is.Null);
                Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveForward), Is.EqualTo(Key.T));
                Assert.That(SettingsKeyMap.Get(SettingsKeyMap.Action.MoveLeft), Is.EqualTo(Key.A), "无冲突时他人键位不动");
            }
            finally
            {
                SettingsKeyMap.Reset(SettingsKeyMap.Action.MoveForward, persist: false);
            }
        }

        // ---- SettingsModel 迁移后新键 ----

        [Test]
        public void SettingsModel_SfxVolumeClampsAndPersists()
        {
            SettingsModel.SfxVolume = 1.5f;
            Assert.That(SettingsModel.SfxVolume, Is.EqualTo(1f));
            SettingsModel.SfxVolume = -0.5f;
            Assert.That(SettingsModel.SfxVolume, Is.EqualTo(0f));
            SettingsModel.ApplyCategoryVolumes();
            Assert.That(AudioBus.Factor(AudioBus.Category.Sfx), Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void SettingsDraft_CaptureIncludesAllBindings()
        {
            var draft = SettingsDraft.CaptureFromCurrent();
            var expected = new HashSet<SettingsKeyMap.Action>();
            foreach (var b in SettingsKeyMap.Bindings) expected.Add(b.action);
            Assert.That(draft.Keys.Keys, Is.EquivalentTo(expected), "草稿必须覆盖全部绑定（含 QuickSwap）");
        }
    }
}
