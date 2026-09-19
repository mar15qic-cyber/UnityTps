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

        /// <summary>导航可见时 body 的锚点（导航隐藏时由 SetNavigationVisible 回收为全屏；
        /// 2026-09-16 需求2：无导航页在旧侧栏锚点下整体右偏）。</summary>
        private static readonly Vector2 BodyAnchorMinVisible = new(0.01f, 0.02f);
        private static readonly Vector2 BodyAnchorMaxVisible = new(0.99f, 0.915f);

        /// <summary>2026-09-16 需求4：大厅壳由左侧 NavRail 改为顶部导航栏（三角洲行动风格）——
        /// 左 logo/品牌、中左横向页签、右 status；body 占据顶栏以下全部区域。</summary>
        private void BuildShell()
        {
            canvas = LobbyViewFactory.CreateCanvas(transform);
            var background = UIComponents.Background(canvas.transform, UIArt.KeyBackgroundLogin);
            backgroundImage = background.GetComponent<Image>();

            navigationRoot = UIComponents.Panel("ShellTopBar", canvas.transform, new Color(0.07f, 0.10f, 0.15f, 0.94f),
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
            CreateNavPill(bar, "任务", LobbyPage.Mission, ref tabX);
            CreateNavPill(bar, "仓库", LobbyPage.Armory, ref tabX);
            CreateNavPill(bar, "商城", LobbyPage.Shop, ref tabX);
            CreateNavPill(bar, "升级", LobbyPage.Upgrades, ref tabX);
            CreateNavPill(bar, "设置", LobbyPage.Settings, ref tabX);
            // 热更页页签（稳定 seam）：Lua 经 HotLuaFacade.RegisterPage 在 AppRoot 启动期注册（早于建壳）；
            // 页签数量大时与右侧 status（x≥0.72）可能紧贴——v1 热页 ≤3 个可接受，超出需压缩页签宽度
            foreach (var hot in HotPageRegistry.All)
                CreateHotNavPill(bar, hot, ref tabX);

            var bodyGo = UIComponents.Panel("PageBody", canvas.transform, new Color(0f, 0f, 0f, 0f),
                BodyAnchorMinVisible, BodyAnchorMaxVisible, 0f, false);
            bodyGo.GetComponent<Image>().raycastTarget = false;
            body = bodyGo.transform;
            bodyRect = bodyGo.GetComponent<RectTransform>();

            status = UITypography.Text("Status", bar, string.Empty, UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.72f, 0f), new Vector2(0.995f, 1f), TextAlignmentOptions.Right);
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

        private void RenderLobby()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            var profile = session.Profile;
            var root = PageRoot("LobbyPage");

            StyledText(root, "作战大厅", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.88f), new Vector2(0.6f, 0.97f), TextAlignmentOptions.Left, FontStyles.Bold);

            // ---- 左区：模式入口大卡（三角洲行动风格：大标题+副标题+强调色 CTA）----
            var onlineCard = StyledPanel("ModeCardOnline", root, UITheme.CardSurface, new Vector2(0.02f, 0.46f), new Vector2(0.66f, 0.82f));
            StyledText(onlineCard.transform, "联机对战", UITheme.FontHero, UITheme.TextPrimary,
                new Vector2(0.06f, 0.62f), new Vector2(0.70f, 0.92f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(onlineCard.transform, "官方 Dedicated 服务器 · 建房 / 加入房间 · TDM 与击杀竞赛", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.06f, 0.48f), new Vector2(0.94f, 0.62f), TextAlignmentOptions.Left);
            UIComponents.Badge("OnlineTag", onlineCard.transform, "MULTIPLAYER", UITheme.AccentPrimary,
                new Vector2(0.06f, 0.30f), new Vector2(0.26f, 0.44f));
            StyledButton(onlineCard.transform, "进入匹配大厅", UIComponents.ButtonKind.Primary,
                new Vector2(0.60f, 0.10f), new Vector2(0.95f, 0.30f), () => Navigate(LobbyPage.OnlineJoin));

            var offlineCard = StyledPanel("ModeCardOffline", root, UITheme.BackgroundPanel, new Vector2(0.02f, 0.12f), new Vector2(0.66f, 0.42f));
            StyledText(offlineCard.transform, "离线演练", UITheme.FontCardTitle + 4, UITheme.TextPrimary,
                new Vector2(0.06f, 0.58f), new Vector2(0.60f, 0.88f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(offlineCard.transform, "本地战斗 · 结算照常提交 XP 与金币", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.06f, 0.36f), new Vector2(0.94f, 0.56f), TextAlignmentOptions.Left);
            StyledButton(offlineCard.transform, "开始演练", UIComponents.ButtonKind.Info,
                new Vector2(0.60f, 0.16f), new Vector2(0.95f, 0.42f), StartGameplay);

            // ---- 右区：档案卡 + 当前主战武器卡 ----
            var card = StyledPanel("ProfileCard", root, UITheme.CardSurface, new Vector2(0.69f, 0.50f), new Vector2(0.97f, 0.82f));
            StyledText(card.transform, profile?.username ?? "-", UITheme.FontCardTitle + 4, UITheme.TextPrimary,
                new Vector2(0.07f, 0.78f), new Vector2(0.93f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(card.transform, $"等级 {profile?.level ?? 0}   ·   技能点 {profile?.skillPoints ?? 0}", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.07f, 0.64f), new Vector2(0.93f, 0.76f), TextAlignmentOptions.Left);
            var xpToNext = Mathf.Max(1, profile?.xpToNextLevel ?? 1);
            StyledText(card.transform, $"XP {profile?.xp ?? 0}/{profile?.xpToNextLevel ?? 0}", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.52f), new Vector2(0.93f, 0.62f), TextAlignmentOptions.Left);
            var xpFill = UIComponents.ProgressBar(card.transform, new Vector2(0.07f, 0.42f), new Vector2(0.93f, 0.51f), UITheme.AccentSecondary);
            xpFill.fillAmount = Mathf.Clamp01((profile?.xp ?? 0) / (float)xpToNext);
            UIComponents.Badge("CoinsBadge", card.transform, $"COINS {profile?.coins ?? 0:N0}", UITheme.AccentPrimary,
                new Vector2(0.07f, 0.24f), new Vector2(0.58f, 0.38f));
            StyledButton(card.transform, "退出会话", UIComponents.ButtonKind.Danger,
                new Vector2(0.62f, 0.24f), new Vector2(0.93f, 0.38f), Logout);

            var loadout = session.Loadout;
            var weaponCard = StyledPanel("LoadoutCard", root, UITheme.BackgroundPanel, new Vector2(0.69f, 0.12f), new Vector2(0.97f, 0.46f));
            StyledText(weaponCard.transform, "当前主战武器", UITheme.FontCaption + 2, UITheme.TextMuted,
                new Vector2(0.07f, 0.82f), new Vector2(0.93f, 0.94f), TextAlignmentOptions.Left);
            var primaryId = loadout?.primaryWeaponId;
            var primaryName = !string.IsNullOrEmpty(primaryId) && weaponAssets.TryGet(primaryId, out var primaryAsset) && primaryAsset != null
                ? primaryAsset.definition != null ? primaryAsset.definition.DisplayName : primaryId
                : "未配置";
            StyledText(weaponCard.transform, primaryName, UITheme.FontCardTitle + 2, UITheme.TextPrimary,
                new Vector2(0.07f, 0.56f), new Vector2(0.93f, 0.80f), TextAlignmentOptions.Left, FontStyles.Bold).name = "LoadoutPrimaryName";
            var secondaryId = loadout?.secondaryWeaponId;
            var secondaryName = !string.IsNullOrEmpty(secondaryId) && weaponAssets.TryGet(secondaryId, out var secondaryAsset) && secondaryAsset != null
                ? secondaryAsset.definition != null ? secondaryAsset.definition.DisplayName : secondaryId
                : "未配置";
            StyledText(weaponCard.transform, "副武器  " + secondaryName, UITheme.FontCaption + 2, UITheme.TextMuted,
                new Vector2(0.07f, 0.40f), new Vector2(0.93f, 0.54f), TextAlignmentOptions.Left).name = "LoadoutSecondaryName";
            StyledButton(weaponCard.transform, "前往仓库改装", UIComponents.ButtonKind.Secondary,
                new Vector2(0.07f, 0.10f), new Vector2(0.93f, 0.30f), () => Navigate(LobbyPage.Armory));

            var gameplayError = session.ConsumeGameplayError();
            if (!string.IsNullOrWhiteSpace(gameplayError))
                StyledText(root, gameplayError, UITheme.FontCaption + 2, UITheme.AccentDanger,
                    new Vector2(0.02f, 0.03f), new Vector2(0.66f, 0.10f), TextAlignmentOptions.Left);

            PlayEnter(root.gameObject);
            // 服务器配装未加载时补拉一次（装备卡显示真实主/副武器）；失败静默，不影响主页渲染
            if (session.Loadout == null && api != null && pageCts != null) _ = EnsureLoadoutCachedAsync(pageCts.Token);
        }

        /// <summary>装备数据补拉：仅当 Loadout 为空时调用一次；成功后按当前页重绘（大厅装备卡/仓库装备角标）。</summary>
        private async Task EnsureLoadoutCachedAsync(System.Threading.CancellationToken token)
        {
            var result = await api.GetLoadoutAsync(token);
            if (token.IsCancellationRequested) return;
            if (!result.Success || result.Data == null) return;
            if (session.Loadout != null) return; // 等待期间已被其他路径加载
            session.ApplyLoadout(result.Data);
            if (currentPage == LobbyPage.Lobby) RenderLobby();
            else if (currentPage == LobbyPage.Armory) RenderArmoryPage();
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

        /// <summary>联机入口页（Docs/27 v1.2 CF）：左侧创建房间（模式/人数），右侧房间码加入 + 房间列表。
        /// 创建/加入成功后一律进等待房间页（Waiting 不连 DS）；仅重连/补人响应带 connection 时直进战场。</summary>
        private void RenderOnlineJoin()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("OnlineJoinPage");

            StyledText(root, "联机对战", UITheme.FontHero, UITheme.TextPrimary,
                new Vector2(0.05f, 0.86f), new Vector2(0.6f, 0.96f), TextAlignmentOptions.Left, FontStyles.Bold);

            var createCard = StyledPanel("CreateCard", root, UITheme.CardSurface, new Vector2(0.06f, 0.18f), new Vector2(0.46f, 0.80f));
            StyledText(createCard.transform, "创建房间", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.86f), new Vector2(0.92f, 0.96f), TextAlignmentOptions.Left, FontStyles.Bold);

            StyledText(createCard.transform, "模式", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.76f), new Vector2(0.24f, 0.84f), TextAlignmentOptions.Left);
            var tdmButton = StyledButton(createCard.transform, "团队死斗", UIComponents.ButtonKind.Secondary,
                new Vector2(0.26f, 0.75f), new Vector2(0.56f, 0.85f), () => { });
            var killRaceButton = StyledButton(createCard.transform, "击杀竞赛", UIComponents.ButtonKind.Secondary,
                new Vector2(0.60f, 0.75f), new Vector2(0.92f, 0.85f), () => { });
            var selectedMode = GameModes.Tdm;
            UIComponents.SetNavPillSelected(tdmButton, true);
            tdmButton.onClick.AddListener(() => { selectedMode = GameModes.Tdm; UIComponents.SetNavPillSelected(tdmButton, true); UIComponents.SetNavPillSelected(killRaceButton, false); });
            killRaceButton.onClick.AddListener(() => { selectedMode = GameModes.KillRace; UIComponents.SetNavPillSelected(killRaceButton, true); UIComponents.SetNavPillSelected(tdmButton, false); });

            StyledText(createCard.transform, "人数上限", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.62f), new Vector2(0.24f, 0.70f), TextAlignmentOptions.Left);
            float capX = 0.26f;
            int[] capacities = { 2, 4, 8, 12, 16 };
            var capacityButtons = new List<(Button Button, int Capacity)>();
            foreach (var capacity in capacities)
            {
                var capButton = StyledButton(createCard.transform, capacity.ToString(), UIComponents.ButtonKind.Secondary,
                    new Vector2(capX, 0.61f), new Vector2(capX + 0.12f, 0.71f), () => { });
                capacityButtons.Add((capButton, capacity));
                var captured = capacity;
                capButton.onClick.AddListener(() =>
                {
                    foreach (var entry in capacityButtons) UIComponents.SetNavPillSelected(entry.Button, entry.Capacity == captured);
                });
                capX += 0.135f;
            }
            UIComponents.SetNavPillSelected(capacityButtons[2].Button, true); // 默认 8 人
            var selectedCapacity = 8;

            // Phase 8 地图选择 → P4 热更试点：数据驱动（/api/maps 缓存；空缓存用内置兜底 + 后台拉取刷新本页）
            StyledText(createCard.transform, "地图", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.48f), new Vector2(0.24f, 0.56f), TextAlignmentOptions.Left);
            var mapButtons = new List<(Button Button, string MapId)>();
            var mapOptions = new List<(string id, string label)>();
            foreach (var entry in HotMapCatalog.Cached)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.mapId)) continue;
                mapOptions.Add((entry.mapId,
                    string.IsNullOrWhiteSpace(entry.displayName) ? entry.mapId : entry.displayName));
            }
            if (mapOptions.Count == 0)
            {
                _ = RefreshMapCatalogAsync();
                mapOptions.Add(("arena", "Arena"));
                mapOptions.Add(("map_01", "Stackyard"));
                mapOptions.Add(("map_02", "Depot 55"));
                mapOptions.Add(("map_03", "Ridgeline"));
            }
            // 动态列宽：5+ 张图自动收窄，避免溢出右侧加入卡
            float mapX = 0.26f;
            float mapStep = System.Math.Min(0.165f, (0.94f - mapX) / mapOptions.Count);
            float mapWidth = mapStep - 0.01f;
            var mapSelectionButtons = new List<Button>();
            foreach (var (id, label) in mapOptions)
            {
                var mapButton = StyledButton(createCard.transform, label, UIComponents.ButtonKind.Secondary,
                    new Vector2(mapX, 0.47f), new Vector2(mapX + mapWidth, 0.57f), () => { });
                mapButtons.Add((mapButton, id));
                mapSelectionButtons.Add(mapButton);
                mapX += mapStep;
            }
            var selectedMapId = "arena";
            foreach (var (button, id) in mapButtons)
            {
                var capturedId = id;
                button.onClick.AddListener(() =>
                {
                    selectedMapId = capturedId;
                    foreach (var entry in mapButtons) UIComponents.SetNavPillSelected(entry.Button, entry.MapId == selectedMapId);
                });
            }
            UIComponents.SetNavPillSelected(mapButtons[0].Button, true); // 默认 arena

            StyledButton(createCard.transform, "创建并进入等待房间", UIComponents.ButtonKind.Primary,
                new Vector2(0.08f, 0.32f), new Vector2(0.92f, 0.44f), () =>
                {
                    Navigate(LobbyPage.Lobby);
                    _ = StartOnlineCreateAsync(new CreateRoomRequest
                    {
                        maxPlayers = selectedCapacity,
                        mode = selectedMode,
                        mapId = selectedMapId,
                    });
                });
            StyledText(createCard.transform, "TDM：100 杀 / 10 分钟（可调整）\nKillRace：20 杀 / 10 分钟（可调整）", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.14f), new Vector2(0.92f, 0.28f), TextAlignmentOptions.Left);

            var joinCard = StyledPanel("JoinCard", root, UITheme.CardSurface, new Vector2(0.52f, 0.18f), new Vector2(0.94f, 0.80f));
            StyledText(joinCard.transform, "输入六位房间码加入", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.86f), new Vector2(0.92f, 0.96f), TextAlignmentOptions.Left, FontStyles.Bold);
            var codeInput = StyledInput("RoomCodeInput", joinCard.transform, "房间码（如 482913）",
                new Vector2(0.08f, 0.70f), new Vector2(0.60f, 0.84f));
            StyledButton(joinCard.transform, "加入", UIComponents.ButtonKind.Primary,
                new Vector2(0.66f, 0.70f), new Vector2(0.92f, 0.84f), () =>
                {
                    var code = (codeInput != null ? codeInput.text : string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(code)) { status.text = "请输入房间码"; return; }
                    Navigate(LobbyPage.Lobby);
                    _ = StartOnlineRoomAsync(code);
                });

            StyledText(joinCard.transform, "或从房间列表选择", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.58f), new Vector2(0.92f, 0.66f), TextAlignmentOptions.Left);

            StyledButton(root, "返回", UIComponents.ButtonKind.Secondary,
                new Vector2(0.06f, 0.05f), new Vector2(0.22f, 0.14f), () => Navigate(LobbyPage.Lobby));

            PlayEnter(root.gameObject);
            _ = RenderRoomListAsync(joinCard.transform);
        }

        private async Task RenderRoomListAsync(Transform card)
        {
            var token = pageCts.Token;
            var result = await api.ListRoomsAsync(token);
            if (token.IsCancellationRequested || result == null || !card) return;
            if (!result.Success || result.Data == null)
            {
                status.text = ApiErrorMessages.ToUserMessage(result);
                return;
            }
            var rooms = result.Data;
            if (rooms.Length == 0)
            {
                StyledText(card, "当前没有可加入的房间，可从左侧直接建房", UITheme.FontCaption, UITheme.TextMuted,
                    new Vector2(0.08f, 0.20f), new Vector2(0.92f, 0.54f));
                return;
            }
            float y = 0.50f;
            int shown = Mathf.Min(rooms.Length, 4);
            for (int i = 0; i < shown; i++)
            {
                var room = rooms[i];
                string label = $"{room.roomCode}   {room.joinedPlayers}/{room.maxPlayers}   {room.status}";
                var row = room;
                StyledButton(card, label, UIComponents.ButtonKind.Secondary,
                    new Vector2(0.08f, y), new Vector2(0.92f, y + 0.07f), () =>
                    {
                        Navigate(LobbyPage.Lobby);
                        _ = StartOnlineRoomAsync(row.roomCode);
                    });
                y -= 0.085f;
                if (y < 0.20f) break;
            }
        }
    }
}
