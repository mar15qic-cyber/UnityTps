using UnityEngine;

namespace Game.UI
{
    /// <summary>Hidden driver that advances UIMotion tweens. Only class in this file on purpose.</summary>
    public sealed class UIMotionDriver : MonoBehaviour
    {
        public static UIMotionDriver Instance { get; private set; }

        public static void EnsureExists()
        {
            if (Instance != null) return;
            if (!Application.isPlaying) return; // EditMode: tweens complete synchronously instead.
            var go = new GameObject("UIMotionDriver", typeof(UIMotionDriver));
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            Instance = go.GetComponent<UIMotionDriver>();
        }

        private void Update()
        {
            UIMotion.Tick(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
