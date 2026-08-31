using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>Locks the Docs/20 design-system contracts: theme tokens, component structure, art fallbacks.</summary>
    public sealed class UIDesignSystemTests
    {
        private readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        private Transform Root()
        {
            var go = new GameObject("TestRoot", typeof(RectTransform));
            created.Add(go);
            return go.transform;
        }

        // ---- Theme ----

        [Test]
        public void Theme_TextContrast_MeetsWcagOnAllSurfaces()
        {
            Assert.That(UITheme.ContrastRatio(UITheme.TextPrimary, UITheme.BackgroundDeep), Is.GreaterThanOrEqualTo(4.5f));
            Assert.That(UITheme.ContrastRatio(UITheme.TextPrimary, UITheme.BackgroundPanel), Is.GreaterThanOrEqualTo(4.5f));
            Assert.That(UITheme.ContrastRatio(UITheme.TextPrimary, UITheme.CardSurface), Is.GreaterThanOrEqualTo(4.5f));
            Assert.That(UITheme.ContrastRatio(UITheme.TextMuted, UITheme.BackgroundPanel), Is.GreaterThanOrEqualTo(4.5f));
            Assert.That(UITheme.ContrastRatio(UITheme.TextOnAccent, UITheme.AccentPrimary), Is.GreaterThanOrEqualTo(4.5f));
        }

        [Test]
        public void Theme_Tokens_AreSane()
        {
            Assert.That(UITheme.FontHero, Is.GreaterThan(UITheme.FontPageTitle));
            Assert.That(UITheme.FontPageTitle, Is.GreaterThan(UITheme.FontCardTitle));
            Assert.That(UITheme.FontCardTitle, Is.GreaterThan(UITheme.FontBody));
            Assert.That(UITheme.FontBody, Is.GreaterThan(UITheme.FontCaption));
            Assert.That(UITheme.RadiusPanel, Is.GreaterThan(UITheme.BorderWidth));
            Assert.That(UITheme.BorderWidth, Is.GreaterThan(0f));
            Assert.That(UITheme.ButtonDepth, Is.GreaterThan(0f));
            Assert.That(UITheme.PageFadeSeconds, Is.InRange(0.05f, 1f));
        }

        // ---- Sprites ----

        [Test]
        public void Sprites_RoundedRect_IsSlicedAndCached()
        {
            var a = UISprites.RoundedRect(UITheme.RadiusPanel);
            var b = UISprites.RoundedRect(UITheme.RadiusPanel);
            Assert.That(a, Is.Not.Null);
            Assert.That(a, Is.SameAs(b));
            Assert.That(a.border.x, Is.GreaterThan(0f));
        }

        [Test]
        public void Sprites_Gradient_And_Circle_Resolve()
        {
            Assert.That(UISprites.Circle(), Is.Not.Null);
            Assert.That(UISprites.GradientVertical(Color.black, Color.white), Is.Not.Null);
        }

        // ---- Components ----

        [Test]
        public void Panel_WithBorder_HasInsetFillChild()
        {
            var panel = UIComponents.Panel("P", Root(), UITheme.CardSurface, Vector2.zero, Vector2.one);
            var fill = panel.transform.Find("Fill");
            Assert.That(fill, Is.Not.Null);
            var fillRect = fill.GetComponent<RectTransform>();
            Assert.That(fillRect.offsetMin.x, Is.EqualTo(UITheme.BorderWidth));
            Assert.That(fill.GetComponent<Image>().color, Is.EqualTo(UITheme.CardSurface));
            Assert.That(panel.GetComponent<Image>().color, Is.EqualTo(UITheme.BorderDark));
        }

        [Test]
        public void Panel_WithoutBorder_HasNoFillChild()
        {
            var panel = UIComponents.Panel("P", Root(), UITheme.CardSurface, Vector2.zero, Vector2.one, UITheme.RadiusPanel, false);
            Assert.That(panel.transform.Find("Fill"), Is.Null);
            Assert.That(panel.GetComponent<Image>().color, Is.EqualTo(UITheme.CardSurface));
        }

        [Test]
        public void Button_Structure_HasDepthFaceLabel()
        {
            var button = UIComponents.Button("B", Root(), "开始战斗", UIComponents.ButtonKind.Primary, Vector2.zero, Vector2.one);
            // The returned Button lives on the Face; root holds Face + Depth.
            var face = button.transform;
            var root = face.parent;
            var depth = root.Find("Depth");
            Assert.That(root.name, Is.EqualTo("B"));
            Assert.That(depth, Is.Not.Null);
            Assert.That(button.GetComponent<Image>().color, Is.EqualTo(UITheme.AccentPrimary));
            var label = face.Find("Label")?.GetComponent<TextMeshProUGUI>();
            Assert.That(label, Is.Not.Null);
            Assert.That(label.text, Is.EqualTo("开始战斗"));
            Assert.That(label.color, Is.EqualTo(UITheme.TextOnAccent));
            Assert.That(depth.GetComponent<Image>().color, Is.EqualTo(UITheme.ButtonShadow));
            var faceRect = face.GetComponent<RectTransform>();
            Assert.That(faceRect.offsetMin.y, Is.EqualTo(UITheme.ButtonDepth));
        }

        [Test]
        public void Button_Kinds_MapToThemeColors()
        {
            Assert.That(UIComponents.ButtonColor(UIComponents.ButtonKind.Primary), Is.EqualTo(UITheme.AccentPrimary));
            Assert.That(UIComponents.ButtonColor(UIComponents.ButtonKind.Info), Is.EqualTo(UITheme.AccentInfo));
            Assert.That(UIComponents.ButtonColor(UIComponents.ButtonKind.Danger), Is.EqualTo(UITheme.AccentDanger));
            Assert.That(UIComponents.ButtonColor(UIComponents.ButtonKind.Secondary), Is.EqualTo(UITheme.CardSurfaceAlt));
        }

        [Test]
        public void Input_Structure_HasViewportPlaceholderAndFocusTriggers()
        {
            var input = UIComponents.Input("I", Root(), "用户名", Vector2.zero, Vector2.one);
            Assert.That(input.placeholder, Is.Not.Null);
            Assert.That(((TMP_Text)input.placeholder).text, Is.EqualTo("用户名"));
            Assert.That(input.textViewport, Is.Not.Null);
            Assert.That(input.textViewport.GetComponent<RectMask2D>(), Is.Not.Null);
            Assert.That(input.textComponent, Is.Not.Null);
            var trigger = input.GetComponent<UnityEngine.EventSystems.EventTrigger>();
            Assert.That(trigger, Is.Not.Null);
            Assert.That(trigger.triggers.Count, Is.EqualTo(2));
        }

        [Test]
        public void NavPill_SelectedState_TogglesBarAndColors()
        {
            var pill = UIComponents.NavPill("N", Root(), "大厅", Vector2.zero, Vector2.one);
            var bar = pill.transform.Find("ActiveBar")?.GetComponent<Image>();
            var label = pill.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            Assert.That(bar, Is.Not.Null);
            Assert.That(label, Is.Not.Null);
            Assert.That(bar.enabled, Is.False);

            UIComponents.SetNavPillSelected(pill, true);
            Assert.That(bar.enabled, Is.True);
            Assert.That(label.color, Is.EqualTo(UITheme.AccentPrimary));

            UIComponents.SetNavPillSelected(pill, false);
            Assert.That(bar.enabled, Is.False);
            Assert.That(label.color, Is.EqualTo(UITheme.TextMuted));
        }

        [Test]
        public void ProgressBar_Fill_IsHorizontalFilled()
        {
            var fill = UIComponents.ProgressBar(Root(), Vector2.zero, Vector2.one, UITheme.AccentPrimary);
            Assert.That(fill.type, Is.EqualTo(Image.Type.Filled));
            Assert.That(fill.fillMethod, Is.EqualTo(Image.FillMethod.Horizontal));
            Assert.That(fill.fillAmount, Is.EqualTo(0f));
            Assert.That(fill.color, Is.EqualTo(UITheme.AccentPrimary));
        }

        [Test]
        public void Card_HasTitleAndContentRegion()
        {
            var content = UIComponents.Card("C", Root(), "档案", Vector2.zero, Vector2.one);
            Assert.That(content, Is.Not.Null);
            Assert.That(content.name, Is.EqualTo("Content"));
            var title = content.parent.Find("Title")?.GetComponent<TextMeshProUGUI>();
            Assert.That(title, Is.Not.Null);
            Assert.That(title.text, Is.EqualTo("档案"));
        }

        [Test]
        public void Background_UsesArtSpriteAndOverlay()
        {
            var bg = UIComponents.Background(Root(), UIArt.KeyBackgroundLogin);
            Assert.That(bg.GetComponent<Image>().sprite, Is.Not.Null);
            var overlay = bg.transform.Find("ReadabilityOverlay");
            Assert.That(overlay, Is.Not.Null);
            Assert.That(overlay.GetComponent<Image>().raycastTarget, Is.False);
        }

        // ---- Art registry ----

        [Test]
        public void UIArt_Registry_ContainsThreeKeyVisuals()
        {
            Assert.That(UIArt.Entries.Count, Is.EqualTo(3));
            Assert.That(UIArt.Get(UIArt.KeyBackgroundLogin), Is.Not.Null);
            Assert.That(UIArt.Get(UIArt.KeyBackgroundLobby), Is.Not.Null);
            Assert.That(UIArt.Get(UIArt.KeyLogo), Is.Not.Null);
        }

        [Test]
        public void UIArt_UnknownKey_FallsBackToGradient()
        {
            var sprite = UIArt.Get("bg.does-not-exist");
            Assert.That(sprite, Is.Not.Null);
        }

        [Test]
        public void UIArt_GeneratedAssets_AreImportedAsSprites()
        {
            Assert.That(UIArt.HasRealAsset(UIArt.KeyBackgroundLogin), Is.True, "LoginBackground.png should exist");
            Assert.That(UIArt.HasRealAsset(UIArt.KeyBackgroundLobby), Is.True, "LobbyBackground.png should exist");
            Assert.That(UIArt.HasRealAsset(UIArt.KeyLogo), Is.True, "GameLogo.png should exist");
        }

        // ---- Motion ----

        [Test]
        public void UIMotion_Tween_CompletesSynchronouslyInEditMode()
        {
            float? lastT = null;
            var completed = false;
            UIMotion.Tween(0.2f, UIMotion.Ease.Linear, t => lastT = t, () => completed = true);
            Assert.That(lastT, Is.EqualTo(1f));
            Assert.That(completed, Is.True);
            Assert.That(UIMotion.Pending, Is.Empty);
        }

        [Test]
        public void UIMotion_Tick_AdvancesPendingHandles()
        {
            var tValues = new List<float>();
            var handle = new UIMotion.Handle
            {
                Duration = 0.5f,
                EaseFunc = UIMotion.Resolve(UIMotion.Ease.Linear),
                OnUpdate = t => tValues.Add(t),
            };
            UIMotion.Pending.Add(handle);
            try
            {
                UIMotion.Tick(0.25f);
                Assert.That(tValues, Has.Count.EqualTo(1));
                Assert.That(tValues[0], Is.EqualTo(0.5f).Within(0.001f));
                UIMotion.Tick(0.25f);
                Assert.That(handle.Done, Is.True);
                Assert.That(UIMotion.Pending, Is.Empty);
            }
            finally
            {
                UIMotion.Pending.Remove(handle);
            }
        }

        [Test]
        public void UIMotion_Eases_HaveExpectedEndpoints()
        {
            foreach (var ease in new[] { UIMotion.Ease.Linear, UIMotion.Ease.OutQuad, UIMotion.Ease.OutBack })
            {
                var f = UIMotion.Resolve(ease);
                Assert.That(f(0f), Is.EqualTo(0f).Within(0.0001f));
                Assert.That(f(1f), Is.EqualTo(1f).Within(0.0001f));
            }
        }

        // ---- Typography ----

        [Test]
        public void UITypography_ChineseFont_IsConfigured()
        {
            Assert.That(UITypography.DefaultFont, Is.Not.Null);
            Assert.That(UITypography.HasChineseFont, Is.True, "Noto Sans SC fallback should resolve 中");
        }

        [Test]
        public void UITypography_Text_CreatesConfiguredLabel()
        {
            var label = UITypography.Text("T", Root(), "作战大厅", UITheme.FontBody, UITheme.TextPrimary, Vector2.zero, Vector2.one);
            Assert.That(label.text, Is.EqualTo("作战大厅"));
            Assert.That(label.fontSize, Is.EqualTo(UITheme.FontBody));
            Assert.That(label.raycastTarget, Is.False);
            Assert.That(label.font, Is.SameAs(UITypography.DefaultFont));
        }
    }
}
