using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Settings
{
    /// <summary>
    /// 设置页草稿（共享设置 Phase B）：进入设置页时从当前生效值捕获，
    /// 滑杆/键位修改先写草稿并即时预览（音量/灵敏度经 SettingsRuntime），
    /// 「应用」才持久化；「取消/返回」回滚到捕获点（不是被修改后的草稿值）；
    /// 「恢复默认」重置为出厂值并即时预览，取消仍可回到捕获点。
    /// 大厅设置页与 Arena 游戏菜单共用同一草稿类型，保证两套 UI 同一套应用/回滚语义。
    /// </summary>
    public sealed class SettingsDraft
    {
        public float MasterVolume;
        public float MusicVolume;
        public float SfxVolume;
        public float Sensitivity;
        public bool AdsToggleMode;
        public (int w, int h) Resolution;
        public bool Fullscreen;
        public int FrameCap;

        /// <summary>键位草稿：动作 → 当前生效键（含未持久化的临时重绑）。</summary>
        public readonly Dictionary<SettingsKeyMap.Action, Key> Keys = new();

        // ---- 捕获点快照（RestoreLive 的回滚基准；与可变草稿字段分离） ----

        private bool _hasCapture;
        private readonly Dictionary<SettingsKeyMap.Action, Key> _capturedKeys = new();
        private float _capMaster, _capMusic, _capSfx, _capSensitivity;
        private bool _capAds;

        /// <summary>从当前生效值捕获（SettingsRuntime 实时值 + SettingsKeyMap 当前映射）。</summary>
        public static SettingsDraft CaptureFromCurrent()
        {
            var draft = new SettingsDraft
            {
                MasterVolume = SettingsRuntime.MasterVolume,
                MusicVolume = SettingsRuntime.MusicVolume,
                SfxVolume = SettingsRuntime.SfxVolume,
                Sensitivity = SettingsRuntime.Sensitivity,
                AdsToggleMode = AdsInputMode.Toggle,
                Resolution = SettingsModel.Resolution,
                Fullscreen = SettingsModel.Fullscreen,
                FrameCap = SettingsModel.FrameCap,
            };
            foreach (var b in SettingsKeyMap.Bindings)
                draft.Keys[b.action] = SettingsKeyMap.Get(b.action);
            draft.TakeCaptureSnapshot();
            return draft;
        }

        private void TakeCaptureSnapshot()
        {
            _capturedKeys.Clear();
            foreach (var kv in Keys) _capturedKeys[kv.Key] = kv.Value;
            _capMaster = MasterVolume;
            _capMusic = MusicVolume;
            _capSfx = SfxVolume;
            _capSensitivity = Sensitivity;
            _capAds = AdsToggleMode;
            _hasCapture = true;
        }

        /// <summary>出厂默认草稿（「恢复默认」的数值源；不触任何持久化，无捕获点语义）。</summary>
        public static SettingsDraft CreateDefaults()
        {
            var draft = new SettingsDraft
            {
                MasterVolume = SettingsModel.DefaultMasterVolume,
                MusicVolume = SettingsModel.DefaultMusicVolume,
                SfxVolume = SettingsModel.DefaultSfxVolume,
                Sensitivity = SettingsModel.DefaultSensitivity,
                AdsToggleMode = false,
                Resolution = (1920, 1080),
                Fullscreen = true,
                FrameCap = 60,
            };
            foreach (var b in SettingsKeyMap.Bindings)
                draft.Keys[b.action] = b.defaultKey;
            return draft;
        }

        /// <summary>把草稿覆盖为出厂默认（保留同一实例与捕获点；UI 直接重绘）。</summary>
        public void ResetToDefaults()
        {
            var defaults = CreateDefaults();
            MasterVolume = defaults.MasterVolume;
            MusicVolume = defaults.MusicVolume;
            SfxVolume = defaults.SfxVolume;
            Sensitivity = defaults.Sensitivity;
            AdsToggleMode = defaults.AdsToggleMode;
            Resolution = defaults.Resolution;
            Fullscreen = defaults.Fullscreen;
            FrameCap = defaults.FrameCap;
            Keys.Clear();
            foreach (var kv in defaults.Keys) Keys[kv.Key] = kv.Value;
        }

        /// <summary>
        /// 应用并持久化：草稿 → SettingsModel/AdsInputMode/SettingsKeyMap → PlayerPrefs.Save
        /// → ApplyAll（Master/分类音量/锁帧/分辨率）。保存后重启仍生效。
        /// </summary>
        public void ApplyAndPersist()
        {
            SettingsModel.MasterVolume = MasterVolume;
            SettingsModel.MusicVolume = MusicVolume;
            SettingsModel.SfxVolume = SfxVolume;
            SettingsModel.Sensitivity = Sensitivity;
            AdsInputMode.Toggle = AdsToggleMode;
            SettingsModel.Resolution = Resolution;
            SettingsModel.Fullscreen = Fullscreen;
            SettingsModel.FrameCap = FrameCap;
            foreach (var b in SettingsKeyMap.Bindings)
            {
                // 无条件持久写：预览阶段已非持久改缓存，这里统一落盘（值相同重复写无害）
                if (Keys.TryGetValue(b.action, out var key) && key != Key.None)
                    SettingsKeyMap.Set(b.action, key, persist: true);
                else
                    SettingsKeyMap.Reset(b.action, persist: true);
            }
            SettingsModel.Save();
            SettingsRuntime.ReloadFromPersistedAndApply();
        }

        /// <summary>
        /// 回滚（取消/返回）：把实时值与键位缓存恢复到【捕获点】（进入设置页前的状态，
        /// 而非被修改后的草稿值），含音量/灵敏度即时预览回滚；不写 PlayerPrefs（持久值从未被草稿改动）。
        /// 无捕获点的草稿（CreateDefaults 裸实例）回滚为无操作。
        /// </summary>
        public void RestoreLive()
        {
            if (!_hasCapture) return;
            SettingsRuntime.SetLive(SensitivityTarget.Master, _capMaster);
            SettingsRuntime.SetLive(SensitivityTarget.Music, _capMusic);
            SettingsRuntime.SetLive(SensitivityTarget.Sfx, _capSfx);
            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, _capSensitivity);
            AdsInputMode.Toggle = _capAds;
            foreach (var b in SettingsKeyMap.Bindings)
            {
                var key = _capturedKeys.TryGetValue(b.action, out var k) ? k : b.defaultKey;
                SettingsKeyMap.Set(b.action, key, persist: false);
            }
        }

        /// <summary>把草稿内键位直接写入运行时键位缓存（重绑即时预览用；取消经 RestoreLive 回滚）。</summary>
        public void PreviewKey(SettingsKeyMap.Action action, Key key)
        {
            Keys[action] = key;
            SettingsKeyMap.Set(action, key, persist: false); // 立即生效（InputReader 直接读映射）；持久化跟随 ApplyAndPersist
        }

        /// <summary>把整份草稿推到实时层（不落 PlayerPrefs）——恢复默认后的即时预览。</summary>
        public void PreviewAllLive()
        {
            SettingsRuntime.SetLive(SensitivityTarget.Master, MasterVolume);
            SettingsRuntime.SetLive(SensitivityTarget.Music, MusicVolume);
            SettingsRuntime.SetLive(SensitivityTarget.Sfx, SfxVolume);
            SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, Sensitivity);
            AdsInputMode.Toggle = AdsToggleMode;
            foreach (var b in SettingsKeyMap.Bindings)
            {
                var key = Keys.TryGetValue(b.action, out var k) ? k : b.defaultKey;
                SettingsKeyMap.Set(b.action, key, persist: false);
            }
        }
    }
}
