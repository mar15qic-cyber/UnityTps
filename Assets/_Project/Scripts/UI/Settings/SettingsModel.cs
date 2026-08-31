using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 设置数据模型与本地持久化（PlayerPrefs）。逻辑与 UI 分离，EditMode 可直测。
    /// 覆盖：全局音量、鼠标灵敏度、分辨率、锁帧；键位映射见 SettingsKeyMap。
    /// </summary>
    public static class SettingsModel
    {
        public const string MusicVolumeKey = "unityfps.settings.music";
        public const string MasterVolumeKey = "unityfps.settings.volume.master";
        public const string SensitivityKey = "unityfps.settings.sensitivity";
        public const string ResolutionKey = "unityfps.settings.resolution";   // "WxH"
        public const string FullscreenKey = "unityfps.settings.fullscreen";   // 0/1
        public const string FrameCapKey = "unityfps.settings.framecap";       // -1/30/60/120/144/240

        public static readonly (int w, int h)[] SupportedResolutions =
        {
            (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160),
        };

        public static readonly int[] FrameCapOptions = { -1, 30, 60, 120, 144, 240 };

        public static float MasterVolume
        {
            get => PlayerPrefs.GetFloat(MasterVolumeKey, 1f);
            set => PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value));
        }

        public static float MusicVolume
        {
            get => PlayerPrefs.GetFloat(MusicVolumeKey, 1f);
            set => PlayerPrefs.SetFloat(MusicVolumeKey, Mathf.Clamp01(value));
        }

        public static float Sensitivity
        {
            get => PlayerPrefs.GetFloat(SensitivityKey, 1f);
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

        /// <summary>应用全局音量到 AudioListener。需在运行线程调用。</summary>
        public static void ApplyMasterVolume()
        {
            AudioListener.volume = MasterVolume;
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

        /// <summary>一次性应用所有画质/音频设置（启动时调用）。</summary>
        public static void ApplyAll()
        {
            ApplyMasterVolume();
            ApplyFrameCap();
            ApplyResolution();
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
