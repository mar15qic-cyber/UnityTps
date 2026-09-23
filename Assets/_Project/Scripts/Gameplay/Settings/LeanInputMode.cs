using UnityEngine;

namespace Game.Gameplay.Settings
{
    public static class LeanInputMode
    {
        public const string PrefsKey = "unityfps.settings.lean.toggle";
        private static bool? _livePreview;
        public static bool Toggle
        {
            get => _livePreview ?? PlayerPrefs.GetInt(PrefsKey, 0) == 1;
            set
            {
                _livePreview = null;
                PlayerPrefs.SetInt(PrefsKey, value ? 1 : 0);
            }
        }

        public static void SetLive(bool value) => _livePreview = value;
        public static void CancelPreview() => _livePreview = null;
    }
}
