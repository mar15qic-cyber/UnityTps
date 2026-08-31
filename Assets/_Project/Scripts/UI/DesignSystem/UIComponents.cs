using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Stylized component factory for the LowPoly look (Docs/20 §2.3): rounded corners via
    /// procedural 9-slice sprites, 2px dark borders, pseudo-3D button thickness, hover/press
    /// motion. Every factory is pure construction (no scene state) so EditMode tests can assert
    /// the produced hierarchy.
    /// </summary>
    public static class UIComponents
    {
        public enum ButtonKind { Primary, Secondary, Info, Danger }

        public static GameObject CreateCanvas(Transform parent, string name = "UICanvas")
        {
            var root = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(parent, false);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return root;
        }

        /// <summary>Fullscreen AI background (or gradient fallback) + bottom darkening overlay for readability.</summary>
        public static GameObject Background(Transform parent, string artKey)
        {
            var root = Panel("Background_" + artKey, parent, Color.white, Vector2.zero, Vector2.one, 0f, false);
            var image = root.GetComponent<Image>();
            image.type = Image.Type.Simple;
            image.sprite = UIArt.Get(artKey);
            image.preserveAspect = false;
            var overlay = Panel("ReadabilityOverlay", root.transform, Color.white, Vector2.zero, Vector2.one, 0f, false);
            var overlayImage = overlay.GetComponent<Image>();
            overlayImage.type = Image.Type.Simple;
            overlayImage.sprite = UISprites.GradientVertical(
                new Color(0.05f, 0.08f, 0.12f, 0.10f),
                new Color(0.05f, 0.08f, 0.12f, 0.82f));
            overlayImage.raycastTarget = false;
            return root;
        }

        /// <summary>Rounded panel with optional dark border wrapper (border = outer rounded rect + inset fill).</summary>
        public static GameObject Panel(string name, Transform parent, Color fill, Vector2 anchorMin, Vector2 anchorMax,
            float radius = UITheme.RadiusPanel, bool border = true)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            if (radius > 0f)
            {
                image.sprite = UISprites.RoundedRect(radius);
                image.type = Image.Type.Sliced;
            }
            image.color = border ? UITheme.BorderDark : fill;
            Place(go.GetComponent<RectTransform>(), anchorMin, anchorMax);
            if (!border) return go;

            var inner = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            inner.transform.SetParent(go.transform, false);
            var innerImage = inner.GetComponent<Image>();
            var innerRadius = Mathf.Max(1f, radius - UITheme.BorderWidth);
            innerImage.sprite = UISprites.RoundedRect(innerRadius);
            innerImage.type = Image.Type.Sliced;
            innerImage.color = fill;
            innerImage.raycastTarget = false;
            var rect = inner.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(UITheme.BorderWidth, UITheme.BorderWidth);
            rect.offsetMax = new Vector2(-UITheme.BorderWidth, -UITheme.BorderWidth);
            image.raycastTarget = false;
            return go;
        }

        /// <summary>Pseudo-3D button: thickness layer under a rounded face; hover lift + press punch.</summary>
        public static Button Button(string name, Transform parent, string label, ButtonKind kind,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var faceColor = ButtonColor(kind);
            var textColor = kind == ButtonKind.Primary ? UITheme.TextOnAccent : UITheme.TextPrimary;

            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            Place(root.GetComponent<RectTransform>(), anchorMin, anchorMax);

            var depth = new GameObject("Depth", typeof(RectTransform), typeof(Image));
            depth.transform.SetParent(root.transform, false);
            var depthImage = depth.GetComponent<Image>();
            depthImage.sprite = UISprites.RoundedRect(UITheme.RadiusButton);
            depthImage.type = Image.Type.Sliced;
            depthImage.color = UITheme.ButtonShadow;
            depthImage.raycastTarget = false;
            var depthRect = depth.GetComponent<RectTransform>();
            depthRect.anchorMin = Vector2.zero;
            depthRect.anchorMax = Vector2.one;
            depthRect.offsetMin = new Vector2(0f, 0f);
            depthRect.offsetMax = new Vector2(0f, -UITheme.ButtonDepth + UITheme.BorderWidth);

            var face = new GameObject("Face", typeof(RectTransform), typeof(Image), typeof(Button));
            face.transform.SetParent(root.transform, false);
            var faceImage = face.GetComponent<Image>();
            faceImage.sprite = UISprites.RoundedRect(UITheme.RadiusButton);
            faceImage.type = Image.Type.Sliced;
            faceImage.color = faceColor;
            var faceRect = face.GetComponent<RectTransform>();
            faceRect.anchorMin = Vector2.zero;
            faceRect.anchorMax = Vector2.one;
            faceRect.offsetMin = new Vector2(0f, UITheme.ButtonDepth);
            faceRect.offsetMax = Vector2.zero;

            var button = face.GetComponent<Button>();
            button.targetGraphic = faceImage;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.45f);
            colors.fadeDuration = 0.06f;
            button.colors = colors;

            var trigger = face.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, _ => UIMotion.PressPunch(faceRect));
            AddTrigger(trigger, EventTriggerType.PointerEnter, _ =>
            {
                if (button.interactable) UIMotion.HoverLift(faceRect, true);
            });
            AddTrigger(trigger, EventTriggerType.PointerExit, _ =>
            {
                if (button.interactable) UIMotion.HoverLift(faceRect, false);
            });

            UITypography.Text("Label", face.transform, label, UITheme.FontBody, textColor,
                new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.97f), TextAlignmentOptions.Center, FontStyles.Bold);
            return button;
        }

        /// <summary>Stylized TMP input with border that highlights on focus.</summary>
        public static TMP_InputField Input(string name, Transform parent, string placeholder, Vector2 anchorMin, Vector2 anchorMax)
        {
            var root = Panel(name, parent, UITheme.BackgroundPanel, anchorMin, anchorMax, UITheme.RadiusButton, true);
            var borderImage = root.GetComponent<Image>();

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(root.transform, false);
            var viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(14f, 6f);
            viewportRect.offsetMax = new Vector2(-14f, -6f);

            // TMP_InputField.Awake() dereferences textComponent; disable the GO so Awake is deferred
            // until after all references are wired (also avoids a null-crash under EditMode tests).
            root.SetActive(false);

            var input = root.AddComponent<TMP_InputField>();
            var text = UITypography.Text("Text", viewport.transform, string.Empty, UITheme.FontBody, UITheme.TextPrimary, Vector2.zero, Vector2.one);
            text.raycastTarget = false;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;
            var hint = UITypography.Text("Placeholder", viewport.transform, placeholder, UITheme.FontBody, UITheme.TextMuted, Vector2.zero, Vector2.one);
            hint.raycastTarget = false;
            hint.verticalAlignment = VerticalAlignmentOptions.Middle;
            hint.fontStyle = FontStyles.Italic;

            input.textViewport = viewportRect;
            input.textComponent = text;
            input.placeholder = hint;
            input.targetGraphic = root.GetComponentInChildren<Image>(true);
            input.pointSize = UITheme.FontBody;
            if (UITypography.DefaultFont != null) input.fontAsset = UITypography.DefaultFont;

            var trigger = root.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.Select, _ => borderImage.color = UITheme.AccentPrimary);
            AddTrigger(trigger, EventTriggerType.Deselect, _ => borderImage.color = UITheme.BorderDark);

            root.SetActive(true);
            return input;
        }

        /// <summary>Vertical NavRail item: left accent bar when selected + label.</summary>
        public static Button NavPill(string name, Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            root.transform.SetParent(parent, false);
            Place(root.GetComponent<RectTransform>(), anchorMin, anchorMax);
            var image = root.GetComponent<Image>();
            image.sprite = UISprites.RoundedRect(UITheme.RadiusButton);
            image.type = Image.Type.Sliced;
            image.color = Color.clear;

            var bar = new GameObject("ActiveBar", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(root.transform, false);
            var barImage = bar.GetComponent<Image>();
            barImage.sprite = UISprites.RoundedRect(4f);
            barImage.type = Image.Type.Sliced;
            barImage.color = UITheme.AccentPrimary;
            barImage.raycastTarget = false;
            barImage.enabled = false;
            var barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 0.18f);
            barRect.anchorMax = new Vector2(0f, 0.82f);
            barRect.offsetMin = new Vector2(0f, 0f);
            barRect.offsetMax = new Vector2(4f, 0f);

            UITypography.Text("Label", root.transform, label, UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.14f, 0f), new Vector2(0.98f, 1f), TextAlignmentOptions.Left, FontStyles.Bold);

            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.9f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            colors.fadeDuration = 0.06f;
            button.colors = colors;
            return button;
        }

        /// <summary>Sets NavPill visual selected state (accent bar + label/background tint).</summary>
        public static void SetNavPillSelected(Button pill, bool selected)
        {
            if (pill == null) return;
            var image = pill.GetComponent<Image>();
            if (image != null)
                image.color = selected ? new Color(1f, 1f, 1f, 0.08f) : Color.clear;
            var bar = pill.transform.Find("ActiveBar")?.GetComponent<Image>();
            if (bar != null) bar.enabled = selected;
            var label = pill.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            if (label != null) label.color = selected ? UITheme.AccentPrimary : UITheme.TextMuted;
        }

        /// <summary>Small rounded badge (price tag, owned marker).</summary>
        public static GameObject Badge(string name, Transform parent, string text, Color color, Vector2 anchorMin, Vector2 anchorMax)
        {
            var badge = Panel(name, parent, color, anchorMin, anchorMax, UITheme.RadiusPill, false);
            UITypography.Text("Text", badge.transform, text, UITheme.FontCaption, UITheme.TextOnAccent,
                new Vector2(0.08f, 0f), new Vector2(0.92f, 1f), TextAlignmentOptions.Center, FontStyles.Bold);
            return badge;
        }

        /// <summary>Rounded progress bar with thickness; returns the fill image (Filled, horizontal).</summary>
        public static Image ProgressBar(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Color fillColor, string name = "ProgressBar")
        {
            var track = Panel(name, parent, UITheme.BackgroundDeep, anchorMin, anchorMax, UITheme.RadiusButton, true);
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(track.transform, false);
            var fill = fillGo.GetComponent<Image>();
            fill.sprite = UISprites.RoundedRect(Mathf.Max(1f, UITheme.RadiusButton - UITheme.BorderWidth));
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = 0f;
            fill.color = fillColor;
            fill.raycastTarget = false;
            var rect = fillGo.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(UITheme.BorderWidth, UITheme.BorderWidth);
            rect.offsetMax = new Vector2(-UITheme.BorderWidth, -UITheme.BorderWidth);
            return fill;
        }

        /// <summary>Card with bold title and a content region; returns the content RectTransform.</summary>
        public static RectTransform Card(string name, Transform parent, string title, Vector2 anchorMin, Vector2 anchorMax)
        {
            var card = Panel(name, parent, UITheme.CardSurface, anchorMin, anchorMax, UITheme.RadiusPanel, true);
            if (!string.IsNullOrEmpty(title))
            {
                UITypography.Text("Title", card.transform, title, UITheme.FontCardTitle, UITheme.TextPrimary,
                    new Vector2(0.06f, 0.86f), new Vector2(0.94f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            }
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(card.transform, false);
            var rect = content.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(16f, 14f);
            rect.offsetMax = new Vector2(-16f, -40f);
            return rect;
        }

        /// <summary>Horizontal slider row for 0-1 values (音量). Returns the Slider.</summary>
        public static Slider SliderRow(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, float value)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            Place(root.GetComponent<RectTransform>(), anchorMin, anchorMax);

            var track = Panel("Track", root.transform, UITheme.BackgroundDeep, Vector2.zero, Vector2.one, UITheme.RadiusButton, true);
            var trackRect = track.GetComponent<RectTransform>();
            trackRect.anchorMin = new Vector2(0f, 0.35f);
            trackRect.anchorMax = new Vector2(1f, 0.65f);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(track.transform, false);
            var fillImg = fill.GetComponent<Image>();
            fillImg.sprite = UISprites.RoundedRect(Mathf.Max(1f, UITheme.RadiusButton - UITheme.BorderWidth));
            fillImg.type = Image.Type.Sliced;
            fillImg.color = UITheme.AccentPrimary;
            fillImg.raycastTarget = false;
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.offsetMin = new Vector2(UITheme.BorderWidth, UITheme.BorderWidth);
            fillRect.offsetMax = new Vector2(-UITheme.BorderWidth, -UITheme.BorderWidth);

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(root.transform, false);
            var handleImg = handle.GetComponent<Image>();
            handleImg.sprite = UISprites.Circle();
            handleImg.color = UITheme.TextPrimary;
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(22f, 22f);

            var slider = root.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImg;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            slider.SetValueWithoutNotify(Mathf.Clamp01(value));
            return slider;
        }

        /// <summary>Left/right arrow stepper for discrete options (分辨率/锁帧). valueLabel shows current option.</summary>
        public static void Stepper(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            out Button prev, out Button next, out TextMeshProUGUI valueLabel)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            Place(root.GetComponent<RectTransform>(), anchorMin, anchorMax);

            prev = Button("Prev", root.transform, "‹", ButtonKind.Secondary, new Vector2(0f, 0f), new Vector2(0.14f, 1f));
            next = Button("Next", root.transform, "›", ButtonKind.Secondary, new Vector2(0.86f, 0f), new Vector2(1f, 1f));
            var center = Panel("Center", root.transform, UITheme.BackgroundPanel, new Vector2(0.16f, 0.08f), new Vector2(0.84f, 0.92f), UITheme.RadiusButton, true);
            valueLabel = UITypography.Text("Value", center.transform, string.Empty, UITheme.FontBody, UITheme.TextPrimary,
                new Vector2(0.04f, 0f), new Vector2(0.96f, 1f), TextAlignmentOptions.Center, FontStyles.Bold);
        }

        public static Color ButtonColor(ButtonKind kind)
        {
            switch (kind)
            {
                case ButtonKind.Primary: return UITheme.AccentPrimary;
                case ButtonKind.Info: return UITheme.AccentInfo;
                case ButtonKind.Danger: return UITheme.AccentDanger;
                default: return UITheme.CardSurfaceAlt;
            }
        }

        public static void Place(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> callback)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(callback);
            trigger.triggers.Add(entry);
        }
    }
}
