using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Settings
{
    /// <summary>实时设置通道（SettingsRuntime.SetLive 的目标）。</summary>
    public enum SensitivityTarget { Master, Music, Sfx, Sensitivity }

    /// <summary>
    /// 运行时设置服务（共享设置 Phase B）：大厅与 Arena 共用的实时值层。
    /// SettingsModel = 持久层（PlayerPrefs）；SettingsRuntime = 实时层（本帧生效值）。
    /// 设置页滑杆即时预览写实时层；「应用」经 SettingsDraft.ApplyAndPersist 落持久层；
    /// 「取消」经 SettingsDraft.RestoreLive 从捕获点回滚实时层。
    /// 启动时 Initialize 一次性应用（AppRoot.Awake 显式调用 + RuntimeInitializeOnLoadMethod
    /// 兜底直连 Arena 场景），保证两场景共享同一持久值。
    /// InputReader 的灵敏度只读本类实时值——用户灵敏度在输入源头只乘一次，
    /// Locomotor/FPMouseLook/网络命令/远端俯仰/sway 消费的都是同一份缩放后的 LookDelta。
    /// </summary>
    public static class SettingsRuntime
    {
        private static bool _initialized;

        public static float MasterVolume { get; private set; } = 1f;
        public static float MusicVolume { get; private set; } = 1f;
        public static float SfxVolume { get; private set; } = 1f;
        public static float Sensitivity { get; private set; } = 1f;

        /// <summary>
        /// 启动入口（幂等）：从持久层加载实时值并应用全部音频/画质设置。
        /// AppRoot.Awake 显式调用；RuntimeInitializeOnLoadMethod 兜底（编辑器直连 Arena 场景）。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            ReloadFromPersistedAndApply();
        }

        /// <summary>从持久层重读实时值并应用（应用设置/恢复默认后调用）。</summary>
        public static void ReloadFromPersistedAndApply()
        {
            MasterVolume = SettingsModel.MasterVolume;
            MusicVolume = SettingsModel.MusicVolume;
            SfxVolume = SettingsModel.SfxVolume;
            Sensitivity = SettingsModel.Sensitivity;
            SettingsModel.ApplyAll(); // Master(AudioListener) + 分类(AudioBus) + 锁帧 + 分辨率
        }

        /// <summary>实时写单个通道并立即应用（设置页即时预览；不写 PlayerPrefs）。</summary>
        public static void SetLive(SensitivityTarget target, float value)
        {
            switch (target)
            {
                case SensitivityTarget.Master:
                    MasterVolume = Mathf.Clamp01(value);
                    AudioListener.volume = MasterVolume; // Master 唯一衰减点（不与分类因子重复）
                    break;
                case SensitivityTarget.Music:
                    MusicVolume = Mathf.Clamp01(value);
                    AudioBus.SetCategoryVolume(AudioBus.Category.Music, MusicVolume);
                    break;
                case SensitivityTarget.Sfx:
                    SfxVolume = Mathf.Clamp01(value);
                    AudioBus.SetCategoryVolume(AudioBus.Category.Sfx, SfxVolume);
                    break;
                case SensitivityTarget.Sensitivity:
                    Sensitivity = Mathf.Clamp(value, 0.1f, 5f);
                    break;
            }
        }

        /// <summary>仅测试用：复位初始化标记（EditMode 隔离场景间状态）。</summary>
        public static void ResetForTests()
        {
            _initialized = false;
            MasterVolume = SettingsModel.DefaultMasterVolume;
            MusicVolume = SettingsModel.DefaultMusicVolume;
            SfxVolume = SettingsModel.DefaultSfxVolume;
            Sensitivity = SettingsModel.DefaultSensitivity;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void BootstrapOnLoad() => Initialize();
    }
}
