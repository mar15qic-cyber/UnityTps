using System;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Settings
{
    public enum OpticReticleStyle { Dot = 0, CircleDot = 1, Chevron = 2, Diamond = 3, ThreePost = 4 }
    // Preserve stored IDs; legacy tinted colors migrate to an available native texture.
    public enum OpticReticleColor { Red = 0, Green = 1, Cyan = 2, White = 3, Blue = 4, Orange = 5 }

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
        public const string OpticReticleStyleKey = "unityfps.settings.opticReticle.style";
        public const string OpticReticleColorKey = "unityfps.settings.opticReticle.color";

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
        public const OpticReticleStyle DefaultOpticReticleStyle = OpticReticleStyle.Dot;
        public const OpticReticleColor DefaultOpticReticleColor = OpticReticleColor.Red;

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

        public static OpticReticleStyle ReticleStyle
        {
            get => NormalizeReticleStyle((OpticReticleStyle)PlayerPrefs.GetInt(OpticReticleStyleKey,
                (int)DefaultOpticReticleStyle));
            set => PlayerPrefs.SetInt(OpticReticleStyleKey, (int)NormalizeReticleStyle(value));
        }

        public static OpticReticleColor ReticleColor
        {
            get => NormalizeReticleColor(ReticleStyle, (OpticReticleColor)PlayerPrefs.GetInt(OpticReticleColorKey,
                (int)DefaultOpticReticleColor));
            set => PlayerPrefs.SetInt(OpticReticleColorKey, (int)NormalizeReticleColor(ReticleStyle, value));
        }

        public const int ReticleStyleCount = 5;
        public static OpticReticleStyle NormalizeReticleStyle(OpticReticleStyle style)
            => (int)style >= 0 && (int)style < ReticleStyleCount ? style : DefaultOpticReticleStyle;

        private static readonly OpticReticleColor[] RedOnly = { OpticReticleColor.Red };
        private static readonly OpticReticleColor[] BlueOnly = { OpticReticleColor.Blue };
        private static readonly OpticReticleColor[] RedBlue = { OpticReticleColor.Red, OpticReticleColor.Blue };
        private static readonly OpticReticleColor[] RedBlueOrange = { OpticReticleColor.Red, OpticReticleColor.Blue, OpticReticleColor.Orange };
        public static System.Collections.Generic.IReadOnlyList<OpticReticleColor> ReticleColors(OpticReticleStyle style)
            => style switch { OpticReticleStyle.Chevron => RedBlue, OpticReticleStyle.Diamond => RedBlueOrange,
                OpticReticleStyle.ThreePost => BlueOnly, _ => RedOnly };

        public static OpticReticleColor NormalizeReticleColor(OpticReticleStyle style, OpticReticleColor color)
        {
            if (color == OpticReticleColor.Cyan) color = OpticReticleColor.Blue;
            var available = ReticleColors(NormalizeReticleStyle(style));
            foreach (var option in available) if (option == color) return color;
            return available[0];
        }

        public static OpticReticleColor NextReticleColor(OpticReticleStyle style, OpticReticleColor color)
        {
            var available = ReticleColors(style);
            color = NormalizeReticleColor(style, color);
            for (int i = 0; i < available.Count; i++)
                if (available[i] == color) return available[(i + 1) % available.Count];
            return available[0];
        }

        public static string FormatReticleStyle(OpticReticleStyle style) => style switch
        {
            OpticReticleStyle.CircleDot => "圆环点",
            OpticReticleStyle.Chevron => "箭头",
            OpticReticleStyle.Diamond => "菱形框",
            OpticReticleStyle.ThreePost => "三线点",
            _ => "单点",
        };

        public static string FormatReticleColor(OpticReticleColor color) => color switch
        {
            OpticReticleColor.Green => "绿色",
            OpticReticleColor.Cyan => "青色",
            OpticReticleColor.White => "白色",
            OpticReticleColor.Blue => "蓝色",
            OpticReticleColor.Orange => "橙色",
            _ => "红色",
        };

        public static Color ResolveReticleColor(OpticReticleColor color) => color switch
        {
            OpticReticleColor.Green => new Color(0.20f, 1f, 0.24f, 1f),
            OpticReticleColor.Cyan => new Color(0.12f, 0.92f, 1f, 1f),
            OpticReticleColor.White => new Color(1f, 1f, 1f, 1f),
            OpticReticleColor.Blue => new Color(0f, .5f, 1f, 1f),
            OpticReticleColor.Orange => new Color(1f, .5f, 0f, 1f),
            _ => new Color(1f, 0.12f, 0.045f, 1f),
        };

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
