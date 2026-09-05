using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 运行时音频总线（共享设置 Phase B）：Master / Music / SFX 三层音量的分类因子与合成。
    /// 放在 Game.Core：Gameplay（设置服务）、Presentation（音频源）、Game.UI（设置页）都能引用，
    /// 且不引入任何反向依赖。
    /// 分层方案（明确记录）：Master 经 AudioListener.volume 全局衰减（SettingsModel.ApplyMasterVolume，
    /// 唯一 Master 衰减点）；Music/SFX 分类因子由各音频源在播放时经 ComputeVolume 消费——
    /// 0 值即真静音（线性 0，不写 dB）；所有因子同源本类，不存在重复衰减。
    /// 纯函数部分（ComputeVolume/Factor）EditMode 可测。
    /// </summary>
    public static class AudioBus
    {
        public enum Category { Music = 0, Sfx = 1 }

        /// <summary>Music 分类因子（0..1，线性）。</summary>
        public static float MusicVolume { get; set; } = 1f;

        /// <summary>SFX 分类因子（0..1，线性）。武器开火/命中/UI 等经此消费。</summary>
        public static float SfxVolume { get; set; } = 1f;

        /// <summary>分类音量变化事件（设置页滑杆即时预览时，注册源刷新音量）。</summary>
        public static event System.Action Changed;

        /// <summary>设置分类因子并广播（SettingsRuntime 调用；测试可直接写因子后手动触发）。</summary>
        public static void SetCategoryVolume(Category category, float volume)
        {
            volume = Mathf.Clamp01(volume);
            if (category == Category.Music) MusicVolume = volume;
            else SfxVolume = volume;
            Changed?.Invoke();
        }

        /// <summary>
        /// 分类因子（纯函数）：分类因子独立于 Master（Master 由 AudioListener 全局承担），
        /// 取 0 即该分类真静音。
        /// </summary>
        public static float Factor(Category category)
            => Mathf.Clamp01(category == Category.Music ? MusicVolume : SfxVolume);

        /// <summary>
        /// 音频源最终音量（纯函数）：基础音量（剪辑/配置随机范围）× 分类因子。
        /// 线性相乘即线性域正确合成；0（用户滑杆到底）× 任意 base = 0 → 真静音。
        /// </summary>
        public static float ComputeVolume(float baseVolume, Category category)
            => Mathf.Clamp01(baseVolume) * Factor(category);
    }
}
