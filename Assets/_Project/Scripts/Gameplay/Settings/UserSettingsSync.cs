using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Settings
{
    /// <summary>
    /// 每玩家设置偏好的服务器同步数据映射（2026-09-07）：后端 UserSetting 表（键值对，按用户隔离）
    /// = 跨设备权威；本地 PlayerPrefs 仍是运行时持久层——拉取=服务器值覆盖本地并重放应用；
    /// 推送=本地偏好全量快照上行；后端 PUT 为合并语义（只 upsert 快照里出现的键）。
    /// 同步范围=玩家偏好（音量/灵敏度/开镜模式/键位）；分辨率/全屏/锁帧属机器本地偏好，不入库。
    /// 键名即后端 UserSetting.SettingKey（稳定契约，改名等于丢玩家旧设置）。
    /// 本类不依赖网络栈（Gameplay 程序集无 ApiClient）——传输由 UI 层（AppRoot）驱动。
    /// </summary>
    public static class UserSettingsSync
    {
        public const string KeyMasterVolume = "volume.master";
        public const string KeyMusicVolume = "volume.music";
        public const string KeySfxVolume = "volume.sfx";
        public const string KeySensitivity = "input.sensitivity";
        public const string KeyAdsToggle = "input.ads.toggle";
        public const string KeyLeanToggle = "input.lean.toggle";

        private static string KeyOf(SettingsKeyMap.Action action) => "key." + action.ToString().ToLowerInvariant();

        /// <summary>本地偏好快照（只含实际同步键；float 用 InvariantCulture 序列化）。</summary>
        public static Dictionary<string, string> CapturePayload()
        {
            var payload = new Dictionary<string, string>
            {
                [KeyMasterVolume] = SettingsModel.MasterVolume.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                [KeyMusicVolume] = SettingsModel.MusicVolume.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                [KeySfxVolume] = SettingsModel.SfxVolume.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                [KeySensitivity] = SettingsModel.Sensitivity.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                [KeyAdsToggle] = AdsInputMode.Toggle ? "1" : "0",
                [KeyLeanToggle] = LeanInputMode.Toggle ? "1" : "0",
            };
            foreach (var binding in SettingsKeyMap.Bindings)
                payload[KeyOf(binding.action)] = SettingsKeyMap.Get(binding.action).ToString();
            return payload;
        }

        /// <summary>
        /// 服务器值覆盖本地（合并语义：只应用快照中出现的键；未知键跳过=向前兼容新客户端），
        /// 然后统一落盘并重放应用（音量/灵敏度立即生效）。
        /// </summary>
        public static void ApplyRemote(IReadOnlyDictionary<string, string> values)
        {
            if (values == null || values.Count == 0) return;
            if (TryFloat(values, KeyMasterVolume, out var v)) SettingsModel.MasterVolume = v;
            if (TryFloat(values, KeyMusicVolume, out v)) SettingsModel.MusicVolume = v;
            if (TryFloat(values, KeySfxVolume, out v)) SettingsModel.SfxVolume = v;
            if (TryFloat(values, KeySensitivity, out v)) SettingsModel.Sensitivity = v;
            if (values.TryGetValue(KeyAdsToggle, out var adsRaw) && (adsRaw == "0" || adsRaw == "1"))
                AdsInputMode.Toggle = adsRaw == "1";
            if (values.TryGetValue(KeyLeanToggle, out var leanRaw) && (leanRaw == "0" || leanRaw == "1"))
                LeanInputMode.Toggle = leanRaw == "1";
            foreach (var binding in SettingsKeyMap.Bindings)
            {
                if (!values.TryGetValue(KeyOf(binding.action), out var keyRaw)) continue;
                if (System.Enum.TryParse(keyRaw, ignoreCase: true, out Key key) && key != Key.None)
                    SettingsKeyMap.Set(binding.action, key, persist: true);
            }
            SettingsModel.Save();
            SettingsRuntime.ReloadFromPersistedAndApply();
        }

        private static bool TryFloat(IReadOnlyDictionary<string, string> values, string key, out float value)
        {
            value = 0f;
            return values.TryGetValue(key, out var raw)
                && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
