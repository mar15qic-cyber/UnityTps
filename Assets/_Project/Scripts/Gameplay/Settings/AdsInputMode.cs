using UnityEngine;

namespace Game.Gameplay.Settings
{
    /// <summary>
    /// 开镜输入模式偏好（PlayerPrefs 持久化，InputReader 读取）。
    /// 长按 = 右键按住开镜（默认，与原实现一致）；切换 = 右键点按开/收镜。
    /// 放在 Gameplay 程序集：InputReader（Gameplay）不能引用 Game.UI 的 SettingsModel，
    /// 与 SettingsKeyMap 同理；设置页（UI 引用 Gameplay）直接读写本类。
    /// </summary>
    public static class AdsInputMode
    {
        public const string PrefsKey = "unityfps.settings.ads.toggle";

        /// <summary>当前是否为切换模式；false = 长按（默认）。</summary>
        public static bool Toggle
        {
            get => PlayerPrefs.GetInt(PrefsKey, 0) == 1;
            set => PlayerPrefs.SetInt(PrefsKey, value ? 1 : 0);
        }

        public static void Reset() => PlayerPrefs.DeleteKey(PrefsKey);

        public static string DisplayName() => Toggle ? "切换" : "长按";
    }
}
