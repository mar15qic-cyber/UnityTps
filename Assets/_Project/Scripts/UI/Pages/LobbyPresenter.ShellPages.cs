using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Account;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Docs/20 Step 4: NavRail shell, lobby home page and the async scene-loading transition.</summary>
    public sealed partial class LobbyPresenter
    {
        private readonly Dictionary<LobbyPage, Button> navPills = new();
        private readonly Dictionary<string, Button> hotNavPills = new();
        private Image loadingFill;

        // ---- 联机入口页房间列表（2026-09-20 需求1）：停留页面自动轮询 + 手动刷新 ----
        // 旧实现只在进页时拉一次，A 建房后 B 必须退出重进才能看到；renderId 防地图目录
        // 回包重渲染本页后出现双循环。手动刷新与轮询并发由 ApiClient operationKey 去重兜底。
        private int onlineJoinRenderId;
        private TMP_Text roomListCaptionText;
        private const string RoomListCaptionPrefix = "或从房间列表选择";
        private const float RoomListPollIntervalSeconds = 3f;

        /// <summary>导航可见时 body 的锚点（导航隐藏时由 SetNavigationVisible 回收为全屏；
        /// 2026-09-16 需求2：无导航页在旧侧栏锚点下整体右偏）。</summary>
        private static readonly Vector2 BodyAnchorMinVisible = new(0.01f, 0.02f);
        private static readonly Vector2 BodyAnchorMaxVisible = new(0.95f, 0.915f);

        /// <summary>2026-09-16 需求4：大厅壳由左侧 NavRail 改为顶部导航栏（三角洲行动风格）——
        /// 左 logo/品牌、中左横向页签、右 status；body 占据顶栏以下全部区域。</summary>
        private void BuildShell()
        {
            canvas = LobbyViewFactory.CreateCanvas(transform);
            var background = UIComponents.Background(canvas.transform, UIArt.KeyBackgroundLogin);
            backgroundImage = background.GetComponent<Image>();

            navigationRoot = UIComponents.Panel("ShellTopBar", canvas.transform, UITheme.BackgroundDeep,
                new Vector2(0f, 0.93f), Vector2.one, 0f, false);
            var bar = navigationRoot.transform;

            var logoGo = new GameObject("BarLogo", typeof(RectTransform), typeof(Image));
            logoGo.transform.SetParent(bar, false);
            var logoImage = logoGo.GetComponent<Image>();
            logoImage.sprite = UIArt.Get(UIArt.KeyLogo);
            logoImage.preserveAspect = true;
            logoImage.raycastTarget = false;
            UIComponents.Place(logoGo.GetComponent<RectTransform>(), new Vector2(0.006f, 0.08f), new Vector2(0.038f, 0.92f));
            UITypography.Text("BarBrand", bar, "UNITY FPS", UITheme.FontBody + 2, UITheme.TextPrimary,
                new Vector2(0.044f, 0.50f), new Vector2(0.155f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold);
            UITypography.Text("BarBrandSub", bar, "LOWPOLY OPS", UITheme.FontCaption - 2, UITheme.TextMuted,
                new Vector2(0.044f, 0.06f), new Vector2(0.155f, 0.52f), TextAlignmentOptions.Left);

            var tabX = 0.170f;
            CreateNavPill(bar, "大厅", LobbyPage.Lobby, ref tabX);
            CreateNavPill(bar, "仓库", LobbyPage.Armory, ref tabX);
            CreateNavPill(bar, "商城", LobbyPage.Shop, ref tabX);
            // 热更页页签（稳定 seam）：Lua 经 HotLuaFacade.RegisterPage 在 AppRoot 启动期注册（早于建壳）；
            // 页签数量大时与右侧 status（x≥0.72）可能紧贴——v1 热页 ≤3 个可接受，超出需压缩页签宽度
            foreach (var hot in HotPageRegistry.All)
                if (hot.Id == "career") CreateHotNavPill(bar, hot, ref tabX);

            var bodyGo = UIComponents.Panel("PageBody", canvas.transform, new Color(0f, 0f, 0f, 0f),
                BodyAnchorMinVisible, BodyAnchorMaxVisible, 0f, false);
            bodyGo.GetComponent<Image>().raycastTarget = false;
            body = bodyGo.transform;
            bodyRect = bodyGo.GetComponent<RectTransform>();

            var account = StyledButton(bar, "账号", UIComponents.ButtonKind.Secondary,
                new Vector2(0.65f, 0.17f), new Vector2(0.875f, 0.83f), ToggleAccountPanel);
            accountLabel = account.GetComponentInChildren<TMP_Text>();
            accountLabel.overflowMode = TextOverflowModes.Ellipsis;
            accountLabel.fontSize = UITheme.FontCaption;
            UIComponents.Place(accountLabel.rectTransform, new Vector2(0.04f, 0.46f), new Vector2(0.66f, 0.98f));
            accountLabel.alignment = TextAlignmentOptions.Right;
            accountTagLabel = StyledText(account.transform, "", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.67f, 0.46f), new Vector2(0.97f, 0.98f), TextAlignmentOptions.Left);
            accountTagLabel.name = "AccountIdentityTag";
            accountStatsLabel = StyledText(account.transform, "", UITheme.FontCaption - 2, UITheme.TextMuted,
                new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.47f), TextAlignmentOptions.Center);
            StyledButton(bar, "设置", UIComponents.ButtonKind.Secondary,
                new Vector2(0.89f, 0.17f), new Vector2(0.95f, 0.83f), () => Navigate(LobbyPage.Settings));
            status = UITypography.Text("Status", canvas.transform, string.Empty, UITheme.FontCaption, UITheme.AccentWarning,
                new Vector2(0.20f, 0.005f), new Vector2(0.80f, 0.035f), TextAlignmentOptions.Center);
            BuildSocialShell();
            canvas.AddComponent<MenuPerformanceView>();
            RefreshLobbyLoadout();
        }

        /// <summary>横向页签（顶栏）：tabX 为游标（引用推进），每签宽 0.062、间距 0.008。</summary>
        private void CreateNavPill(Transform bar, string label, LobbyPage page, ref float tabX)
        {
            var pill = UIComponents.TopTab("Nav_" + label, bar, label,
                new Vector2(tabX, 0.06f), new Vector2(tabX + 0.062f, 0.94f));
            tabX += 0.070f;
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
            foreach (var entry in hotNavPills)
                UIComponents.SetNavPillSelected(entry.Value, currentPage == LobbyPage.Hot && entry.Key == currentHotPageId);
        }

        /// <summary>热页页签（与枚举页签同规格）：id 键控、点击走 NavigateHot(id)。</summary>
        private void CreateHotNavPill(Transform bar, HotPageRegistry.HotPage page, ref float tabX)
        {
            var pill = UIComponents.TopTab("NavHot_" + page.Id, bar, page.Label,
                new Vector2(tabX, 0.06f), new Vector2(tabX + 0.062f, 0.94f));
            tabX += 0.070f;
            var capturedId = page.Id;
            pill.onClick.AddListener(() => NavigateHot(capturedId));
            pill.interactable = apiAvailable;
            navigationButtons.Add(pill);
            hotNavPills[capturedId] = pill;
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

        private void RenderLobby() => RenderTacticalLobby();

        private async Task EnsureLoadoutCachedAsync(System.Threading.CancellationToken token)
        {
            var result = await api.GetLoadoutAsync(cancellationToken: token);
            if (token.IsCancellationRequested) return;
            if (!result.Success || result.Data == null) return;
            if (session.Loadout != null) return; // 等待期间已被其他路径加载
            session.ApplyLoadout(result.Data);
            if (currentPage == LobbyPage.Lobby) RefreshLobbyLoadout(); // 仅刷新配装，不重置角色待机和旋转
            else if (currentPage == LobbyPage.Armory) RenderArmoryPage();
        }

        /// <summary>Async scene-loading transition page; loadingFill is driven by the online scene load loop.</summary>
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

        /// <summary>联机入口页（Docs/27 v1.2 CF）：左侧创建房间（模式/人数），右侧房间码加入 + 房间列表。
        /// 创建/加入成功后一律进等待房间页（Waiting 不连 DS）；仅重连/补人响应带 connection 时直进战场。</summary>
        /// <summary>房间列表轮询：停留本页期间每 3s 重拉一次；页面离开/重渲染（renderId 变化）即退出。</summary>
        private async Task RunRoomListPollAsync(Transform content, int renderId)
        {
            var token = pageCts != null ? pageCts.Token : System.Threading.CancellationToken.None;
            while (!token.IsCancellationRequested && currentPage == LobbyPage.OnlineJoin && renderId == onlineJoinRenderId)
            {
                await DelaySafe(RoomListPollIntervalSeconds, token);
                if (token.IsCancellationRequested || currentPage != LobbyPage.OnlineJoin || renderId != onlineJoinRenderId) return;
                await RenderRoomListAsync(content, renderId);
            }
        }

        private async Task RenderRoomListAsync(Transform content, int renderId)
        {
            if (api == null || content == null) return;
            var token = pageCts != null ? pageCts.Token : System.Threading.CancellationToken.None;
            var result = await api.ListRoomsAsync(token);
            if (token.IsCancellationRequested || renderId != onlineJoinRenderId || currentPage != LobbyPage.OnlineJoin || content == null) return;
            if (result == null) return;
            if (!result.Success || result.Data == null)
            {
                // 手动刷新与轮询并发时 ApiClient 按操作键去重（在途请求的结果会照常渲染），此处静默
                if (result.Code != "CLIENT_DUPLICATE_REQUEST")
                    status.text = ApiErrorMessages.ToUserMessage(result);
                return;
            }
            cachedRoomRows = result.Data;
            RenderRoomRows(content);
        }

        private GameRoomDto[] cachedRoomRows = System.Array.Empty<GameRoomDto>();
        private string roomSearch = "";
    }
}
