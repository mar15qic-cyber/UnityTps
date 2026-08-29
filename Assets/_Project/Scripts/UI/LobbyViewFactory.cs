using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public static class LobbyViewFactory
    {
        public static readonly Color Background = new(0.025f, 0.065f, 0.14f, 1f);
        public static readonly Color PanelSurface = new(0.055f, 0.12f, 0.23f, 0.94f);
        public static readonly Color PanelAlt = new(0.08f, 0.18f, 0.30f, 0.96f);
        public static readonly Color Teal = new(0.10f, 0.83f, 0.72f, 1f);
        public static readonly Color Cyan = new(0.25f, 0.70f, 1f, 1f);
        public static readonly Color Gold = new(1f, 0.76f, 0.23f, 1f);
        public static readonly Color Coral = new(1f, 0.34f, 0.40f, 1f);
        public static readonly Color PrimaryText = new(0.93f, 0.97f, 1f, 1f);
        public static readonly Color Muted = new(0.56f, 0.68f, 0.82f, 1f);

        public static GameObject CreateCanvas(Transform parent)
        {
            var root = new GameObject("LobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(parent, false);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return root;
        }

        public static GameObject Panel(string name, Transform parent, Color color, Vector2 min = default, Vector2 max = default)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            Place(go.GetComponent<RectTransform>(), min == default ? Vector2.zero : min, max == default ? Vector2.one : max);
            return go;
        }

        public static UnityEngine.UI.Text Text(string name, Transform parent, string value, float size, Color color, Vector2 min, Vector2 max, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<UnityEngine.UI.Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = value;
            label.fontSize = Mathf.RoundToInt(size);
            label.color = color;
            label.alignment = alignment == TextAlignmentOptions.Center ? TextAnchor.MiddleCenter : alignment == TextAlignmentOptions.Right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.raycastTarget = false;
            Place(label.rectTransform, min, max);
            return label;
        }

        public static Button Button(string name, Transform parent, string label, Color color, Vector2 min, Vector2 max)
        {
            var go = Panel(name, parent, color, min, max);
            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.16f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.16f);
            colors.disabledColor = new Color(color.r, color.g, color.b, 0.35f);
            button.colors = colors;
            Text("Label", go.transform, label, 18f, TextColor(color), new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.97f), TextAlignmentOptions.Center);
            return button;
        }

        public static InputField Input(string name, Transform parent, string placeholder, Vector2 min, Vector2 max)
        {
            var go = Panel(name, parent, PanelAlt, min, max);
            var input = go.AddComponent<InputField>();
            var text = Text("Text", go.transform, string.Empty, 20f, PrimaryText, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f));
            var hint = Text("Placeholder", go.transform, placeholder, 18f, Muted, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f));
            input.textComponent = text;
            input.placeholder = hint;
            input.targetGraphic = go.GetComponent<Image>();
            return input;
        }

        public static Image Progress(Transform parent, Vector2 min, Vector2 max, Color fillColor)
        {
            var background = Panel("ProgressBackground", parent, new Color(0.01f, 0.03f, 0.08f, 0.9f), min, max);
            var fill = Panel("ProgressFill", background.transform, fillColor).GetComponent<Image>();
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            return fill;
        }

        public static void Place(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Color TextColor(Color background) => PrimaryText;

    }
}

