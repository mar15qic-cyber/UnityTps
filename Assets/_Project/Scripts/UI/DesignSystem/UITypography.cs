using TMPro;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Binds the Noto Sans SC font asset (TMPChineseFont skill) and exposes typed text factories.
    /// Falls back to the TMP default font when the Chinese SDF is unavailable.
    /// </summary>
    public static class UITypography
    {
        private const string ChineseFontEditorPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";
        private const string ChineseFontResourcesPath = "Fonts/NotoSansSC-Regular SDF";

        private static TMP_FontAsset cachedFont;

        public static TMP_FontAsset DefaultFont
        {
            get
            {
                if (cachedFont != null) return cachedFont;
#if UNITY_EDITOR
                cachedFont = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ChineseFontEditorPath);
#endif
                if (cachedFont == null)
                    cachedFont = Resources.Load<TMP_FontAsset>(ChineseFontResourcesPath);
                if (cachedFont == null && TMP_Settings.defaultFontAsset != null)
                    cachedFont = TMP_Settings.defaultFontAsset;
                return cachedFont;
            }
        }

        /// <summary>Visible for tests: true when a Chinese-capable font is globally registered.</summary>
        public static bool HasChineseFont
        {
            get
            {
                var font = DefaultFont;
                if (font == null) return false;
                if (font.HasCharacter('中', false, false)) return true;
                // Dynamic atlases start empty; accept a dynamically populated font registered as a global fallback.
                return font.atlasPopulationMode == AtlasPopulationMode.Dynamic
                    && TMP_Settings.fallbackFontAssets != null
                    && TMP_Settings.fallbackFontAssets.Contains(font);
            }
        }

        public static TextMeshProUGUI Text(string name, Transform parent, string value, int size, Color color,
            Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions alignment = TextAlignmentOptions.Left,
            FontStyles style = FontStyles.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false); // defer TextMeshProUGUI.Awake/OnEnable until configured (EditMode-safe)
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (DefaultFont != null) label.font = DefaultFont;
            label.text = value;
            label.fontSize = size;
            label.color = color;
            label.alignment = alignment;
            label.fontStyle = style;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            Place(go.GetComponent<RectTransform>(), anchorMin, anchorMax);
            go.SetActive(true);
            return label;
        }

        public static TextMeshProUGUI Title(string name, Transform parent, string value, Vector2 anchorMin, Vector2 anchorMax,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var label = Text(name, parent, value, UITheme.FontPageTitle, UITheme.TextPrimary, anchorMin, anchorMax, alignment, FontStyles.Bold);
            // POLYGON-style dark offset outline for punchy titles.
            label.fontMaterial.EnableKeyword("UNDERLAY_ON");
            label.fontMaterial.SetColor("_UnderlayColor", UITheme.BorderDark);
            label.fontMaterial.SetFloat("_UnderlayOffsetX", 0.5f);
            label.fontMaterial.SetFloat("_UnderlayOffsetY", -0.5f);
            label.fontMaterial.SetFloat("_UnderlayDilate", 0.4f);
            label.fontMaterial.SetFloat("_UnderlaySoftness", 0.1f);
            return label;
        }

        internal static void Place(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
