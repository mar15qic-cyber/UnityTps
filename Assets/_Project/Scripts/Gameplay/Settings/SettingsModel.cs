using System;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Settings
{
    /// <summary>
    /// 设置数据模型与本地持久化（PlayerPrefs）。逻辑与 UI 分离，EditMode 可直测。
    /// 覆盖：Master/Music/SFX 三层音量、鼠标灵敏度、分辨率、锁帧；键位映射见 SettingsKeyMap。
    /// 2026-09-05 自 Game.UI 迁入 Gameplay 程序集（共享设置单真相重构）：
    /// InputReader（Gameplay）直接消费 Sensitivity，大厅与 Arena 共用同一持久值，
    /// Gameplay 层不反向依赖 Game.UI。
    /// 音量分层：Master = AudioListener.volume（唯一总衰减，明确的 Master 方案，
    /// 不与 Mixer Master 重复叠加）；Music/SFX = AudioBus 分类因子，在音频源侧消费。
    /// </summary>
    public static class SettingsModel
    {
        public const string MusicVolumeKey = "unityfps.settings.music";
        public const string MasterVolumeKey = "unityfps.settings.volume.master";
        public const string SfxVolumeKey = "unityfps.settings.sfx";
        public const string SensitivityKey = "unityfps.settings.sensitivity";
        public const string ResolutionKey = "unityfps.settings.resolution";   // "WxH"
        public const string FullscreenKey = "unityfps.settings.fullscreen";   // 0/1
        public const string FrameCapKey = "unityfps.settings.framecap";       // -1/30/60/120/144/240

        public static readonly (int w, int h)[] SupportedResolutions =
        {
            (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160),
        };

        public static readonly int[] FrameCapOptions = { -1, 30, 60, 120, 144, 240 };

        // ---- 出厂默认（SettingsDraft「恢复默认」与首启共用同一常量） ----

        public const float DefaultMasterVolume = 1f;
        public const float DefaultMusicVolume = 1f;
        public const float DefaultSfxVolume = 1f;
        public const float DefaultSensitivity = 1f;

        public static float MasterVolume
        {
            get => PlayerPrefs.GetFloat(MasterVolumeKey, DefaultMasterVolume);
            set => PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value));
        }

        public static float MusicVolume
        {
            get => PlayerPrefs.GetFloat(MusicVolumeKey, DefaultMusicVolume);
            set => PlayerPrefs.SetFloat(MusicVolumeKey, Mathf.Clamp01(value));
        }

        public static float SfxVolume
        {
            get => PlayerPrefs.GetFloat(SfxVolumeKey, DefaultSfxVolume);
            set => PlayerPrefs.SetFloat(SfxVolumeKey, Mathf.Clamp01(value));
        }

        public static float Sensitivity
        {
            get => PlayerPrefs.GetFloat(SensitivityKey, DefaultSensitivity);
            set => PlayerPrefs.SetFloat(SensitivityKey, Mathf.Clamp(value, 0.1f, 5f));
        }

        public static (int w, int h) Resolution
        {
            get
            {
                var raw = PlayerPrefs.GetString(ResolutionKey, "1920x1080");
                return ParseResolution(raw, (1920, 1080));
            }
            set => PlayerPrefs.SetString(ResolutionKey, $"{value.w}x{value.h}");
        }

        public static bool Fullscreen
        {
            get => PlayerPrefs.GetInt(FullscreenKey, 1) == 1;
            set => PlayerPrefs.SetInt(FullscreenKey, value ? 1 : 0);
        }

        public static int FrameCap
        {
            get => PlayerPrefs.GetInt(FrameCapKey, 60);
            set => PlayerPrefs.SetInt(FrameCapKey, value);
        }

        public static (int w, int h) ParseResolution(string raw, (int w, int h) fallback)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var sep = raw.IndexOf('x');
                if (sep > 0 &&
                    int.TryParse(raw.Substring(0, sep), out var w) &&
                    int.TryParse(raw.Substring(sep + 1), out var h) &&
                    w >= 640 && h >= 360)
                    return (w, h);
            }
            return fallback;
        }

        public static string FormatFrameCap(int cap) => cap <= 0 ? "无限制" : cap + " FPS";

        public static string FormatResolution((int w, int h) r) => $"{r.w} × {r.h}";

        /// <summary>应用 Master 音量到 AudioListener（唯一 Master 衰减点）。需在运行线程调用。</summary>
        public static void ApplyMasterVolume()
        {
            AudioListener.volume = MasterVolume;
        }

        /// <summary>应用 Music/SFX 分类因子到 AudioBus（音频源侧消费；0 = 真静音）。</summary>
        public static void ApplyCategoryVolumes()
        {
            AudioBus.MusicVolume = MusicVolume;
            AudioBus.SfxVolume = SfxVolume;
        }

        /// <summary>应用锁帧。vSync 关闭时 targetFrameRate 才生效。</summary>
        public static void ApplyFrameCap()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = FrameCap <= 0 ? -1 : FrameCap;
        }

        /// <summary>应用分辨率与全屏。编辑器下不实际切换（仅记录）。</summary>
        public static void ApplyResolution()
        {
            var r = Resolution;
            if (Application.isPlaying && !Application.isEditor)
                Screen.SetResolution(r.w, r.h, Fullscreen);
        }

        /// <summary>一次性应用所有音频/画质设置（启动时调用；SettingsRuntime.Initialize 亦走此入口）。</summary>
        public static void ApplyAll()
        {
            ApplyMasterVolume();
            ApplyCategoryVolumes();
            ApplyFrameCap();
            ApplyResolution();
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
