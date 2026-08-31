using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Docs/20 Step 4: NavRail shell, lobby home page and the async scene-loading transition.</summary>
    public sealed partial class LobbyPresenter
    {
        private readonly Dictionary<LobbyPage, Button> navPills = new();
        private Image loadingFill;

        private void BuildShell()
        {
            canvas = LobbyViewFactory.CreateCanvas(transform);
            var background = UIComponents.Background(canvas.transform, UIArt.KeyBackgroundLogin);
            backgroundImage = background.GetComponent<Image>();

            navigationRoot = UIComponents.Panel("NavRail", canvas.transform, new Color(0.07f, 0.10f, 0.15f, 0.94f),
                Vector2.zero, new Vector2(0.13f, 1f), 0f, false);
            var rail = navigationRoot.transform;

            var logoGo = new GameObject("RailLogo", typeof(RectTransform), typeof(Image));
            logoGo.transform.SetParent(rail, false);
            var logoImage = logoGo.GetComponent<Image>();
            logoImage.sprite = UIArt.Get(UIArt.KeyLogo);
            logoImage.preserveAspect = true;
            logoImage.raycastTarget = false;
            UIComponents.Place(logoGo.GetComponent<RectTransform>(), new Vector2(0.30f, 0.90f), new Vector2(0.70f, 0.985f));
            UITypography.Text("RailBrand", rail, "UNITY FPS", 20, UITheme.TextPrimary,
                new Vector2(0.04f, 0.852f), new Vector2(0.96f, 0.897f), TextAlignmentOptions.Center, FontStyles.Bold);
            UITypography.Text("RailBrandSub", rail, "LOWPOLY OPS", 12, UITheme.TextMuted,
                new Vector2(0.04f, 0.822f), new Vector2(0.96f, 0.854f), TextAlignmentOptions.Center);

            CreateNavPill(rail, "大厅", LobbyPage.Lobby, 0.700f);
            CreateNavPill(rail, "任务", LobbyPage.Mission, 0.638f);
            CreateNavPill(rail, "仓库", LobbyPage.Armory, 0.576f);
            CreateNavPill(rail, "商城", LobbyPage.Shop, 0.514f);
            CreateNavPill(rail, "升级", LobbyPage.Upgrades, 0.452f);
            CreateNavPill(rail, "设置", LobbyPage.Settings, 0.390f);

            var bodyGo = UIComponents.Panel("PageBody", canvas.transform, new Color(0f, 0f, 0f, 0f),
                new Vector2(0.155f, 0.02f), new Vector2(0.99f, 0.98f), 0f, false);
            bodyGo.GetComponent<Image>().raycastTarget = false;
            body = bodyGo.transform;

            status = UITypography.Text("Status", rail, string.Empty, UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.06f, 0.015f), new Vector2(0.94f, 0.055f), TextAlignmentOptions.Left);
        }

        private void CreateNavPill(Transform rail, string label, LobbyPage page, float y0)
        {
            var pill = UIComponents.NavPill("Nav_" + label, rail, label, new Vector2(0.08f, y0), new Vector2(0.94f, y0 + 0.054f));
            pill.onClick.AddListener(() => Navigate(page));
            pill.interactable = apiAvailable;
            navigationButtons.Add(pill);
            navPills[page] = pill;
        }

        /// <summary>Highlights the rail pill matching the current page (WeaponDetails follows its source list).</summary>
        private void UpdateNavSelection()
        {
            var selected = currentPage;
            if (selected == LobbyPage.WeaponDetails) selected = detailsFromShop ? LobbyPage.Shop : LobbyPage.Armory;
            foreach (var entry in navPills)
                UIComponents.SetNavPillSelected(entry.Value, entry.Key == selected);
        }

        /// <summary>Transparent full-body page container registered for cleanup; new pages parent everything here.</summary>
        private RectTransform PageRoot(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(body, false);
            UIComponents.Place(go.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);
            bodyObjects.Add(go);
            return go.GetComponent<RectTransform>();
        }

        private void RenderLobby()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            var profile = session.Profile;
            var root = PageRoot("LobbyPage");

            StyledText(root, "作战大厅", UITheme.FontHero, UITheme.TextPrimary,
                new Vector2(0.02f, 0.88f), new Vector2(0.6f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(root, "LOCAL GAMEPLAY 模式 · 开始按钮进入现有本地关卡，不伪装为服务器匹配", UITheme.FontCaption + 2, UITheme.AccentPrimary,
                new Vector2(0.02f, 0.83f), new Vector2(0.85f, 0.885f), TextAlignmentOptions.Left);

            var card = StyledPanel("ProfileCard", root, UITheme.CardSurface, new Vector2(0.02f, 0.30f), new Vector2(0.44f, 0.74f));
            StyledText(card.transform, profile?.username ?? "-", UITheme.FontCardTitle + 4, UITheme.TextPrimary,
                new Vector2(0.07f, 0.80f), new Vector2(0.93f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(card.transform, $"等级 {profile?.level ?? 0}   ·   技能点 {profile?.skillPoints ?? 0}", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.07f, 0.68f), new Vector2(0.93f, 0.78f), TextAlignmentOptions.Left);
            var xpToNext = Mathf.Max(1, profile?.xpToNextLevel ?? 1);
            StyledText(card.transform, $"XP {profile?.xp ?? 0}/{profile?.xpToNextLevel ?? 0}", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.56f), new Vector2(0.93f, 0.64f), TextAlignmentOptions.Left);
            var xpFill = UIComponents.ProgressBar(card.transform, new Vector2(0.07f, 0.46f), new Vector2(0.93f, 0.55f), UITheme.AccentSecondary);
            xpFill.fillAmount = Mathf.Clamp01((profile?.xp ?? 0) / (float)xpToNext);
            UIComponents.Badge("CoinsBadge", card.transform, $"COINS {profile?.coins ?? 0:N0}", UITheme.AccentPrimary,
                new Vector2(0.07f, 0.28f), new Vector2(0.60f, 0.41f));

            var gameplayError = session.ConsumeGameplayError();
            if (!string.IsNullOrWhiteSpace(gameplayError))
                StyledText(root, gameplayError, UITheme.FontCaption + 2, UITheme.AccentDanger,
                    new Vector2(0.02f, 0.22f), new Vector2(0.5f, 0.28f), TextAlignmentOptions.Left);

            StyledButton(root, "进入战斗", UIComponents.ButtonKind.Primary,
                new Vector2(0.52f, 0.52f), new Vector2(0.97f, 0.72f), StartGameplay);
            StyledButton(root, "联机对战（房主）", UIComponents.ButtonKind.Info,
                new Vector2(0.52f, 0.36f), new Vector2(0.73f, 0.49f), StartOnlineHost);
            StyledButton(root, "联机对战（加入）", UIComponents.ButtonKind.Info,
                new Vector2(0.76f, 0.36f), new Vector2(0.97f, 0.49f), StartOnlineClient);
            StyledButton(root, "任务 / 仓库", UIComponents.ButtonKind.Secondary,
                new Vector2(0.52f, 0.22f), new Vector2(0.73f, 0.33f), () => Navigate(LobbyPage.Mission));
            StyledButton(root, "退出会话", UIComponents.ButtonKind.Danger,
                new Vector2(0.76f, 0.22f), new Vector2(0.97f, 0.33f), Logout);

            PlayEnter(root.gameObject);
        }

        /// <summary>Async scene-loading transition page; loadingFill is driven by StartGameplayAsync's load loop.</summary>
        private void RenderLoading()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("LoadingPage");
            var card = StyledPanel("LoadingPanel", root, UITheme.CardSurface, new Vector2(0.30f, 0.28f), new Vector2(0.70f, 0.72f));
            CreateLogo(card.transform, new Vector2(0.40f, 0.58f), new Vector2(0.60f, 0.90f));
            StyledText(card.transform, "正在进入战场…", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.42f), new Vector2(0.9f, 0.56f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, "小提示：配件装配会随服务器配装一同下发到战局。", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.1f, 0.32f), new Vector2(0.9f, 0.42f));
            loadingFill = UIComponents.ProgressBar(card.transform, new Vector2(0.16f, 0.20f), new Vector2(0.84f, 0.28f), UITheme.AccentPrimary);
            PlayEnter(card);
        }
    }
}
