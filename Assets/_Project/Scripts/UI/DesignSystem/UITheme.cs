using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Single source of truth for the LowPoly stylized UI look (Docs/20 §2).
    /// All colors/sizes are readonly tokens; page builders must not hardcode literals.
    /// </summary>
    public static class UITheme
    {
        // Surfaces
        public static readonly Color BackgroundDeep = Hex("#141B26");
        public static readonly Color BackgroundPanel = Hex("#232F3F");
        public static readonly Color CardSurface = Hex("#2E3D52");
        public static readonly Color CardSurfaceAlt = Hex("#37485F");
        public static readonly Color BorderDark = Hex("#10161F");

        // Accents
        public static readonly Color AccentPrimary = Hex("#FFC93C");
        public static readonly Color AccentSecondary = Hex("#6BCB77");
        public static readonly Color AccentInfo = Hex("#4D96FF");
        public static readonly Color AccentDanger = Hex("#FF6B6B");
        public static readonly Color AccentWarning = Hex("#FFA41B");

        // Text
        public static readonly Color TextPrimary = Hex("#F5F7FA");
        public static readonly Color TextMuted = Hex("#9AA7B8");
        public static readonly Color TextOnAccent = Hex("#1A222E");

        // Button pseudo-3D thickness layer
        public static readonly Color ButtonShadow = Hex("#0D1117");

        // Type ramp (px at 1920x1080 reference)
        public const int FontHero = 44;
        public const int FontPageTitle = 36;
        public const int FontCardTitle = 24;
        public const int FontBody = 18;
        public const int FontCaption = 14;

        // Geometry
        public const float RadiusPanel = 12f;
        public const float RadiusButton = 10f;
        public const float RadiusPill = 18f;
        public const float BorderWidth = 2f;
        public const float ButtonDepth = 4f;

        // Motion (seconds)
        public const float PageFadeSeconds = 0.25f;
        public const float PageSlidePixels = 12f;
        public const float PressScale = 0.96f;
        public const float PressSeconds = 0.08f;
        public const float HoverSeconds = 0.12f;
        public const float HoverLiftPixels = 2f;

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var color);
            return color;
        }

        /// <summary>WCAG relative-luminance contrast ratio, used by EditMode tests.</summary>
        public static float ContrastRatio(Color a, Color b)
        {
            var la = RelativeLuminance(a);
            var lb = RelativeLuminance(b);
            var lighter = Mathf.Max(la, lb);
            var darker = Mathf.Min(la, lb);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private static float RelativeLuminance(Color c)
        {
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        private static float Channel(float v)
        {
            return v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
    }
}
