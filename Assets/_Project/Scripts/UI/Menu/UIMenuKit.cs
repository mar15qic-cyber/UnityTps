using Game.Gameplay.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Menu
{
    /// <summary>
    /// 游戏菜单 uGUI 构建小工具（Game.UI 内部）：统一暗色/红色强调语言，
    /// 底层走 UITheme/UITypography/UIComponents 设计系统（延迟 Awake 安全模式已内建）。
    /// </summary>
    internal static class UIMenuKit
    {
        /// <summary>无边框纯色面板。</summary>
        public static GameObject Panel(string name, Transform parent, Color fill, Vector2 min, Vector2 max)
            => UIComponents.Panel(name, parent, fill, min, max, 4f, border: false);

        public static TMP_Text Title(Transform parent, string value, Vector2 min, Vector2 max)
            => UITypography.Text("Title", parent, value, UITheme.FontPageTitle, TextBright(), min, max,
                TextAlignmentOptions.Left, FontStyles.Bold);

        public static TMP_Text Body(Transform parent, string value, Vector2 min, Vector2 max, Color color)
            => UITypography.Text("Body", parent, value, UITheme.FontBody + 1, color, min, max, TextAlignmentOptions.Left);

        public static TMP_Text Caption(Transform parent, string value, Vector2 min, Vector2 max, Color color)
            => UITypography.Text("Caption", parent, value, UITheme.FontCaption + 1, color, min, max, TextAlignmentOptions.Left);

        public static void AccentBar(Transform parent, Vector2 min, Vector2 max, Color? color = null)
            => Panel("AccentBar", parent, color ?? new Color(1f, 0.27f, 0.33f), min, max);

        /// <summary>左侧导航大按钮：根透明可点 + 红色选中条 + 大号标签。返回（按钮, 选中条, 标签）。</summary>
        public static (Button button, Image accent, TMP_Text label) NavButton(Transform parent, string name, string label,
            Vector2 min, Vector2 max, Color? accent = null)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>();
            UIComponents.Place(rect, min, max);
            var image = root.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.02f);
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.06f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.02f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;

            var bar = Panel("SelectedBar", root.transform, accent ?? new Color(1f, 0.27f, 0.33f),
                new Vector2(0f, 0.08f), new Vector2(0.045f, 0.92f));
            bar.GetComponent<Image>().raycastTarget = false;

            var text = UITypography.Text("Label", root.transform, label, UITheme.FontCardTitle + 6, TextDim(),
                new Vector2(0.09f, 0f), new Vector2(0.98f, 1f), TextAlignmentOptions.MidlineLeft);
            return (button, bar.GetComponent<Image>(), text);
        }

        /// <summary>菜单内弹窗按钮（设计系统伪 3D 按钮壳，可覆盖面色）。</summary>
        public static Button MenuButton(Transform parent, string name, string label, Color faceColor, Vector2 min, Vector2 max)
        {
            var button = UIComponents.Button(name, parent, label, UIComponents.ButtonKind.Secondary, min, max);
            var image = button.GetComponent<Image>();
            if (image != null) image.color = faceColor;
            var text = button.GetComponentInChildren<TMP_Text>();
            if (text != null) text.color = TextBright();
            return button;
        }

        private static Color TextBright() => new Color(0.96f, 0.97f, 0.98f);
        private static Color TextDim() => new Color(0.60f, 0.66f, 0.73f);

        /// <summary>滑杆行 + 上方小标题（设置页统一行式布局：标题占上 40%，滑杆占下 50%）。</summary>
        public static Slider LabeledSlider(Transform parent, string name, string label,
            Vector2 min, Vector2 max, float value)
        {
            float mid = min.y + (max.y - min.y) * 0.55f;
            UITypography.Text("Label_" + name, parent, label, UITheme.FontCaption + 1, TextDim(),
                new Vector2(min.x, mid), new Vector2(max.x, max.y), TextAlignmentOptions.Left);
            return UIComponents.SliderRow(name, parent, new Vector2(min.x, min.y), new Vector2(max.x, mid), value);
        }
    }
}
