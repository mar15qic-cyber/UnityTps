using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Optional menu-only performance readout, outside the navigation and notifications.</summary>
    public sealed class MenuPerformanceView : MonoBehaviour
    {
        private const string Key = "unityfps.ui.performance";
        private TMP_Text label;
        private float elapsed;
        private int frames;
        private GameObject panel;
        public static bool Visible { get => PlayerPrefs.GetInt(Key, 0) == 1; set { PlayerPrefs.SetInt(Key, value ? 1 : 0); PlayerPrefs.Save(); } }
        private void Awake()
        {
            panel = UIComponents.Panel("PerformanceReadout", transform, UITheme.BackgroundDeep,
                new Vector2(0.80f, 0.001f), new Vector2(0.955f, 0.03f), 0, false);
            panel.GetComponent<Image>().raycastTarget = false;
            label = UITypography.Text("Performance", panel.transform, "", UITheme.FontCaption, UITheme.TextMuted, Vector2.zero, Vector2.one);
            panel.SetActive(Visible);
        }
        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame) Visible = !Visible;
            panel.SetActive(Visible);
            elapsed += Time.unscaledDeltaTime; frames++;
            if (elapsed < 0.5f) return;
            if (Visible) label.text = $"{frames / elapsed:0} FPS   {elapsed * 1000 / frames:0.0} ms   F8";
            frames = 0; elapsed = 0;
        }
    }
}
