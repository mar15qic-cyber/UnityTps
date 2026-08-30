using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Weapon;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Owns page state and binds every persistent value to the API client. The views are intentionally
    /// generated in code so the scene remains a small composition root and can be tested without prefabs.
    /// </summary>
    public sealed class LobbyPresenter : MonoBehaviour
    {
        private const string SettingsMusicKey = "unityfps.settings.music";
        private const string SettingsSensitivityKey = "unityfps.settings.sensitivity";
        private readonly List<GameObject> bodyObjects = new();
        private readonly List<Button> navigationButtons = new();
        private IApiClient api;
        private AccountSession session;
        private WeaponAssetCatalog weaponAssets;
        private GameObject canvas;
        private GameObject navigationRoot;
        private Transform body;
        private UnityEngine.UI.Text status;
        private CancellationTokenSource pageCts;
        private Action retryAction;
        private string gameplaySceneName = "Gameplay";
        private LobbyPage currentPage;
        private bool apiAvailable;
        private ShopCatalogDto cachedCatalog;
        private InventoryDto cachedInventory;
        private AttachmentCompatibilityDto[] cachedCompatibility = Array.Empty<AttachmentCompatibilityDto>();
        private CatalogItemDto selectedWeapon;
        private bool detailsFromShop;
        private string catalogFilter = "Rifle";

        public void Initialize(string gameplayScene, WeaponAssetCatalog catalog)
        {
            gameplaySceneName = string.IsNullOrWhiteSpace(gameplayScene) ? "Arena" : gameplayScene;
            weaponAssets = catalog != null ? catalog : WeaponAssetCatalog.CreateRuntime();
            api = AppRoot.Instance.ApiClient;
            session = AppRoot.Instance.Session;
            EnsureInputSystemEventSystem();
            BuildShell();
            SetNavigationVisible(false);
            Navigate(LobbyPage.Boot);
        }

        private void OnDestroy()
        {
            pageCts?.Cancel();
            pageCts?.Dispose();
        }

        private void EnsureInputSystemEventSystem()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                var go = new GameObject("EventSystem", typeof(EventSystem));
                eventSystem = go.GetComponent<EventSystem>();
            }
            else
            {
                var legacy = eventSystem.GetComponent<StandaloneInputModule>();
                if (legacy != null) Destroy(legacy);
            }
            var inputSystemModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModuleType != null && eventSystem.GetComponent(inputSystemModuleType) == null)
                eventSystem.gameObject.AddComponent(inputSystemModuleType);
        }

        private void BuildShell()
        {
            canvas = LobbyViewFactory.CreateCanvas(transform);
            var background = LobbyViewFactory.Panel("GradientLikeBackground", canvas.transform, LobbyViewFactory.Background);
            var topGlow = LobbyViewFactory.Panel("TopCyanGlow", background.transform, new Color(0.02f, 0.18f, 0.28f, 0.45f), new Vector2(0f, 0.72f), Vector2.one);
            LobbyViewFactory.Text("Brand", topGlow.transform, "UNITY FPS  //  COMMAND CENTER", 20f, LobbyViewFactory.Muted,
                new Vector2(0.035f, 0.25f), new Vector2(0.5f, 0.78f));
            navigationRoot = LobbyViewFactory.Panel("Navigation", background.transform, new Color(0.02f, 0.055f, 0.12f, 0.92f), new Vector2(0f, 0.84f), Vector2.one);
            var nav = navigationRoot;
            CreateNavButton(nav.transform, "大厅", LobbyPage.Lobby, 0.23f);
            CreateNavButton(nav.transform, "任务", LobbyPage.Mission, 0.34f);
            CreateNavButton(nav.transform, "仓库", LobbyPage.Armory, 0.45f);
            CreateNavButton(nav.transform, "商城", LobbyPage.Shop, 0.56f);
            CreateNavButton(nav.transform, "升级", LobbyPage.Upgrades, 0.67f);
            CreateNavButton(nav.transform, "设置", LobbyPage.Settings, 0.78f);
            body = LobbyViewFactory.Panel("PageBody", background.transform, new Color(0f, 0f, 0f, 0f), new Vector2(0.035f, 0.08f), new Vector2(0.965f, 0.82f)).transform;
            status = LobbyViewFactory.Text("Status", background.transform, string.Empty, 15f, LobbyViewFactory.Muted,
                new Vector2(0.04f, 0.025f), new Vector2(0.96f, 0.065f));
        }

        private void CreateNavButton(Transform parent, string label, LobbyPage page, float centerX)
        {
            var button = LobbyViewFactory.Button("Nav_" + label, parent, label, new Color(0.04f, 0.14f, 0.23f, 0.92f),
                new Vector2(centerX - 0.05f, 0.12f), new Vector2(centerX + 0.05f, 0.88f));
            button.onClick.AddListener(() => Navigate(page));
            button.interactable = apiAvailable;
            navigationButtons.Add(button);
        }

        public void Navigate(LobbyPage page)
        {
            pageCts?.Cancel();
            pageCts?.Dispose();
            pageCts = new CancellationTokenSource();
            retryAction = null;
            if (IsProtectedPage(page) && (session == null || !session.IsAuthenticated)) page = LobbyPage.Login;
            currentPage = page;
            ClearBody();
            status.text = string.Empty;
            switch (page)
            {
                case LobbyPage.Boot: RenderBoot(pageCts.Token); break;
                case LobbyPage.Login: RenderLoginPage(); break;
                case LobbyPage.Register: RenderRegisterPage(); break;
                case LobbyPage.Identity: RenderIdentity(); break;
                case LobbyPage.Lobby: RenderLobby(); break;
                case LobbyPage.Mission: RenderMission(); break;
                case LobbyPage.Armory: LoadCatalogAndRender(false, pageCts.Token); break;
                case LobbyPage.WeaponDetails: RenderWeaponDetails(); break;
                case LobbyPage.Shop: LoadCatalogAndRender(true, pageCts.Token); break;
                case LobbyPage.Upgrades: RenderUpgrades(); break;
                case LobbyPage.Settings: RenderSettings(); break;
                case LobbyPage.Hud: RenderHud(); break;
                case LobbyPage.Pause: RenderPause(); break;
                case LobbyPage.Results: RenderResults(); break;
                case LobbyPage.Error: RenderError("发生未知错误", retryAction); break;
                case LobbyPage.SessionExpired: RenderSessionExpired(); break;
            }
        }

        private void ClearBody()
        {
            foreach (var go in bodyObjects) if (go != null) Destroy(go);
            bodyObjects.Clear();
        }

        private GameObject Panel(string name, Color color, Vector2 min, Vector2 max)
        {
            var go = LobbyViewFactory.Panel(name, body, color, min, max);
            bodyObjects.Add(go);
            return go;
        }

        private UnityEngine.UI.Text Text(Transform parent, string value, float size, Color color, Vector2 min, Vector2 max,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft) =>
            LobbyViewFactory.Text("Text_" + bodyObjects.Count + "_" + Guid.NewGuid().ToString("N"), parent, value, size, color, min, max, alignment);

        private Button Button(Transform parent, string label, Color color, Vector2 min, Vector2 max, UnityEngine.Events.UnityAction action)
        {
            var button = LobbyViewFactory.Button("Button_" + bodyObjects.Count + "_" + Guid.NewGuid().ToString("N"), parent, label, color, min, max);
            button.onClick.AddListener(action);
            return button;
        }

        private void RenderBoot(CancellationToken token) => _ = BootAsync(token);

        private async Task BootAsync(CancellationToken token)
        {
            apiAvailable = false;
            SetNavigationInteractable(false);
            var panel = Panel("BootPanel", LobbyViewFactory.PanelAlt, new Vector2(0.25f, 0.28f), new Vector2(0.75f, 0.72f));
            Text(panel.transform, "正在连接作战服务", 34f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.58f), new Vector2(0.9f, 0.82f), TextAlignmentOptions.Center);
            Text(panel.transform, "验证 API、数据库和目录状态…", 18f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.4f), new Vector2(0.9f, 0.56f), TextAlignmentOptions.Center);
            LobbyViewFactory.Progress(panel.transform, new Vector2(0.2f, 0.22f), new Vector2(0.8f, 0.28f), LobbyViewFactory.Teal);
            var result = await api.GetHealthAsync(token);
            if (token.IsCancellationRequested || currentPage != LobbyPage.Boot) return;
            if (result.Success)
            {
                apiAvailable = true;
                SetNavigationVisible(session.IsAuthenticated);
                status.text = "服务在线 · " + (result.Data?.database ?? "database");
                Navigate(session.IsAuthenticated ? LobbyPage.Lobby : LobbyPage.Login);
            }
            else
            {
                apiAvailable = false;
                SetNavigationInteractable(false);
                RenderError(ApiErrorMessages.ToUserMessage(result), () => Navigate(LobbyPage.Boot));
            }
        }

        private void SetNavigationInteractable(bool value)
        {
            foreach (var button in navigationButtons)
                if (button != null) button.interactable = value && apiAvailable && session != null && session.IsAuthenticated;
        }

        private void SetNavigationVisible(bool value)
        {
            if (navigationRoot != null) navigationRoot.SetActive(value);
            SetNavigationInteractable(value);
        }

        private static bool IsProtectedPage(LobbyPage page)
        {
            return page == LobbyPage.Lobby || page == LobbyPage.Mission || page == LobbyPage.Armory ||
                   page == LobbyPage.WeaponDetails || page == LobbyPage.Shop || page == LobbyPage.Upgrades ||
                   page == LobbyPage.Settings || page == LobbyPage.Hud || page == LobbyPage.Pause ||
                   page == LobbyPage.Results;
        }

        private void RenderLoginPage() => RenderAuthPage(false);

        private void RenderRegisterPage() => RenderAuthPage(true);

        private void RenderAuthPage(bool register)
        {
            var panel = Panel(register ? "RegisterPanel" : "LoginPanel", LobbyViewFactory.PanelAlt, new Vector2(0.27f, 0.15f), new Vector2(0.73f, 0.86f));
            Text(panel.transform, register ? "建立身份" : "登录作战网络", 38f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.78f), new Vector2(0.9f, 0.91f), TextAlignmentOptions.Center);
            Text(panel.transform, register ? "注册后将获得 M4、AK 与 Service Pistol" : "使用服务器账号继续", 17f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.69f), new Vector2(0.9f, 0.77f), TextAlignmentOptions.Center);
            var username = LobbyViewFactory.Input("Username", panel.transform, "用户名", new Vector2(0.13f, 0.54f), new Vector2(0.87f, 0.64f));
            var password = LobbyViewFactory.Input("Password", panel.transform, "密码（至少 8 位）", new Vector2(0.13f, 0.39f), new Vector2(0.87f, 0.49f));
            password.contentType = InputField.ContentType.Password;
            var action = Button(panel.transform, register ? "注册并继续" : "登录", LobbyViewFactory.Teal, new Vector2(0.13f, 0.23f), new Vector2(0.87f, 0.34f),
                () => _ = SubmitAuthAsync(register, username.text, password.text, panel.transform));
            Button(panel.transform, register ? "返回登录" : "创建账号", new Color(0.08f, 0.18f, 0.29f, 1f), new Vector2(0.13f, 0.08f), new Vector2(0.48f, 0.17f),
                () => Navigate(register ? LobbyPage.Login : LobbyPage.Register));
            Text(panel.transform, "取消" + (action.interactable ? " · 请求期间可安全取消" : string.Empty), 13f, LobbyViewFactory.Muted, new Vector2(0.52f, 0.08f), new Vector2(0.87f, 0.17f), TextAlignmentOptions.Right);
        }

        private async Task SubmitAuthAsync(bool register, string username, string password, Transform panel)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) { status.text = "请输入用户名和密码"; return; }
            if (!apiAvailable)
            {
                status.text = "后端服务未就绪，请先完成连接检查";
                return;
            }
            var button = panel.GetComponentsInChildren<Button>().FirstOrDefault(x => x.GetComponentInChildren<UnityEngine.UI.Text>()?.text == (register ? "注册并继续" : "登录"));
            if (button != null) button.interactable = false;
            status.text = "请求处理中…";
            var token = pageCts.Token;
            var result = register ? await api.RegisterAsync(username.Trim(), password, token) : await api.LoginAsync(username.Trim(), password, token);
            if (token.IsCancellationRequested) return;
            if (!result.Success)
            {
                if (result.Code == "AUTH_UNAUTHORIZED") { status.text = "用户名或密码不正确"; }
                else if (ApiClientErrorCodes.IsTransportFailure(result.Code))
                {
                    apiAvailable = false;
                    SetNavigationInteractable(false);
                    RenderError(ApiErrorMessages.ToUserMessage(result), () => Navigate(LobbyPage.Boot));
                    return;
                }
                else status.text = ApiErrorMessages.ToUserMessage(result);
                if (button != null) button.interactable = true;
                return;
            }
            ApplySession(result.Data);
            if (register)
            {
                SetNavigationVisible(false);
                Navigate(LobbyPage.Identity);
            }
            else
            {
                SetNavigationVisible(true);
                Navigate(LobbyPage.Lobby);
            }
        }

        private void ApplySession(AuthSessionDto value)
        {
            api.SetToken(value.token);
            session.Apply(value);
        }

        private void EnterAuthenticatedLobby()
        {
            SetNavigationVisible(true);
            Navigate(LobbyPage.Lobby);
        }

        private void RenderIdentity()
        {
            var panel = Panel("IdentityPanel", LobbyViewFactory.PanelAlt, new Vector2(0.22f, 0.24f), new Vector2(0.78f, 0.76f));
            var name = session.Profile?.username ?? "Operator";
            Text(panel.transform, "身份确认", 38f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.70f), new Vector2(0.9f, 0.88f), TextAlignmentOptions.Center);
            Text(panel.transform, "欢迎，" + name + "。服务器已创建你的账户、钱包和初始库存。", 19f, LobbyViewFactory.Muted, new Vector2(0.12f, 0.46f), new Vector2(0.88f, 0.66f), TextAlignmentOptions.Center);
            Text(panel.transform, "初始解锁：M4  ·  AK  ·  Service Pistol", 23f, LobbyViewFactory.Teal, new Vector2(0.12f, 0.31f), new Vector2(0.88f, 0.43f), TextAlignmentOptions.Center);
            Button(panel.transform, "进入大厅", LobbyViewFactory.Teal, new Vector2(0.25f, 0.12f), new Vector2(0.75f, 0.24f), EnterAuthenticatedLobby);
        }

        private void RenderLobby()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            var profile = session.Profile;
            var panel = Panel("LobbyOverview", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            Text(panel.transform, "作战大厅", 42f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.78f), new Vector2(0.65f, 0.95f));
            Text(panel.transform, "LOCAL GAMEPLAY 模式 · 开始按钮进入现有本地关卡，不伪装为服务器匹配", 16f, LobbyViewFactory.Gold, new Vector2(0.04f, 0.70f), new Vector2(0.72f, 0.78f));
            var card = LobbyViewFactory.Panel("ProfileCard", panel.transform, LobbyViewFactory.PanelAlt, new Vector2(0.04f, 0.25f), new Vector2(0.52f, 0.65f));
            Text(card.transform, profile?.username ?? "-", 30f, LobbyViewFactory.PrimaryText, new Vector2(0.07f, 0.64f), new Vector2(0.93f, 0.90f));
            Text(card.transform, $"等级 {profile?.level ?? 0}    XP {profile?.xp ?? 0}/{profile?.xpToNextLevel ?? 0}\n技能点 {profile?.skillPoints ?? 0}", 20f, LobbyViewFactory.Muted, new Vector2(0.07f, 0.20f), new Vector2(0.93f, 0.58f));
            Text(card.transform, $"COINS  {profile?.coins ?? 0:N0}", 24f, LobbyViewFactory.Gold, new Vector2(0.07f, 0.04f), new Vector2(0.93f, 0.18f));
            var gameplayError = session.ConsumeGameplayError();
            if (!string.IsNullOrWhiteSpace(gameplayError))
                Text(panel.transform, gameplayError, 16f, LobbyViewFactory.Coral, new Vector2(0.04f, 0.12f), new Vector2(0.52f, 0.23f));
            Button(panel.transform, "进入本地 Gameplay", LobbyViewFactory.Teal, new Vector2(0.58f, 0.49f), new Vector2(0.95f, 0.64f), StartGameplay);
            Button(panel.transform, "联机对战（房主）", LobbyViewFactory.Gold, new Vector2(0.58f, 0.30f), new Vector2(0.95f, 0.45f), StartOnlineHost);
            Button(panel.transform, "联机对战（加入）", LobbyViewFactory.Cyan, new Vector2(0.58f, 0.11f), new Vector2(0.95f, 0.25f), StartOnlineClient);
            Button(panel.transform, "任务 / 仓库", new Color(0.16f, 0.24f, 0.32f, 1f), new Vector2(0.04f, 0.02f), new Vector2(0.52f, 0.13f), () => Navigate(LobbyPage.Mission));
            Button(panel.transform, "退出会话", new Color(0.35f, 0.1f, 0.16f, 1f), new Vector2(0.60f, 0.02f), new Vector2(0.95f, 0.09f), Logout);
        }

        /// <summary>联机入口（Docs/19 N4）：进入 Arena 后按 F1=房主 / F2=加入（127.0.0.1，改 NetworkHud.clientAddress 连局域网）。
        /// 房间码注册表 API 已就绪（/api/rooms），完整大厅房间列表 UI 属后续打磨——当前 LAN 直连已是 M7 可验收链路。</summary>
        private void StartOnlineHost()
        {
            status.text = "进入联机关卡：加载后按 F1 开房（client-hosted）";
            _ = StartGameplayAsync();
        }

        private void StartOnlineClient()
        {
            status.text = "进入联机关卡：加载后按 F2 连接房主（默认 127.0.0.1，局域网改 NetworkHud.clientAddress）";
            _ = StartGameplayAsync();
        }

        private void RenderMission()
        {
            var panel = Panel("MissionPanel", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            Text(panel.transform, "任务 / 地图", 40f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.78f), new Vector2(0.65f, 0.95f));
            var card = LobbyViewFactory.Panel("MapCard", panel.transform, LobbyViewFactory.PanelAlt, new Vector2(0.04f, 0.18f), new Vector2(0.64f, 0.68f));
            Text(card.transform, "村庄 · 训练行动", 29f, LobbyViewFactory.PrimaryText, new Vector2(0.07f, 0.68f), new Vector2(0.93f, 0.9f));
            Text(card.transform, "当前可用：本地 Gameplay\n服务器结算：通过 ClientMatchId 幂等提交 XP 与金币\n在线匹配：尚未接入", 19f, LobbyViewFactory.Muted, new Vector2(0.07f, 0.25f), new Vector2(0.93f, 0.61f));
            Button(panel.transform, "开始本地任务", LobbyViewFactory.Teal, new Vector2(0.70f, 0.42f), new Vector2(0.96f, 0.58f), StartGameplay);
            Button(panel.transform, "返回大厅", LobbyViewFactory.Cyan, new Vector2(0.70f, 0.22f), new Vector2(0.96f, 0.37f), () => Navigate(LobbyPage.Lobby));
        }

        private void StartGameplay() => _ = StartGameplayAsync();

        private async Task StartGameplayAsync()
        {
            if (string.IsNullOrWhiteSpace(gameplaySceneName)) { status.text = "未配置 Gameplay 场景"; return; }
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            status.text = "正在验证服务器配装…";
            var result = await api.GetLoadoutAsync(pageCts.Token);
            if (!result.Success)
            {
                if (result.Code == "AUTH_UNAUTHORIZED") Navigate(LobbyPage.SessionExpired);
                else status.text = "无法进入 Arena：" + ApiErrorMessages.ToUserMessage(result);
                return;
            }
            if (result.Data == null ||
                !weaponAssets.TryResolveDefinition(result.Data.primaryWeaponId, out _) ||
                !weaponAssets.TryResolveDefinition(result.Data.secondaryWeaponId, out _))
            {
                status.text = "无法进入 Arena：服务器配装对应的本地武器资源缺失";
                return;
            }
            session.ApplyLoadout(result.Data);
            SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
        }

        private void LoadCatalogAndRender(bool shop, CancellationToken token) => _ = LoadCatalogAsync(shop, token);

        private async Task LoadCatalogAsync(bool shop, CancellationToken token)
        {
            var loading = Panel("Loading", LobbyViewFactory.PanelAlt, new Vector2(0.2f, 0.4f), new Vector2(0.8f, 0.6f));
            Text(loading.transform, shop ? "加载商城目录…" : "加载仓库目录…", 26f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.55f), new Vector2(0.9f, 0.8f), TextAlignmentOptions.Center);
            try
            {
                var catalogTask = api.GetShopCatalogAsync(token);
                var inventoryTask = api.GetInventoryAsync(token);
                await Task.WhenAll(catalogTask, inventoryTask);
                var catalogResult = await catalogTask;
                var inventoryResult = await inventoryTask;
                if (token.IsCancellationRequested || currentPage != (shop ? LobbyPage.Shop : LobbyPage.Armory)) return;
                if (!catalogResult.Success) { HandleCatalogFailure(shop, catalogResult); return; }
                if (!inventoryResult.Success) { HandleCatalogFailure(shop, inventoryResult); return; }
                cachedCatalog = catalogResult.Data;
                cachedInventory = inventoryResult.Data;
                RenderCatalog(shop);
            }
            catch (OperationCanceledException)
            {
                // Page navigation intentionally cancels the previous request.
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || currentPage != (shop ? LobbyPage.Shop : LobbyPage.Armory)) return;
                apiAvailable = false;
                SetNavigationVisible(false);
                Debug.LogException(ex);
                RenderError("商城/仓库请求未完成，请检查 API 是否正在运行后重试", () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
            }
        }

        private void HandleCatalogFailure<T>(bool shop, ApiResult<T> failed)
        {
            if (failed.Code == "AUTH_UNAUTHORIZED")
            {
                Navigate(LobbyPage.SessionExpired);
                return;
            }

            if (ApiClientErrorCodes.IsTransportFailure(failed.Code))
            {
                apiAvailable = false;
                SetNavigationVisible(false);
            }

            RenderError(ApiErrorMessages.ToUserMessage(failed), () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
        }

        private void RenderCatalog(bool shop)
        {
            ClearBody();
            var panel = Panel(shop ? "ShopCatalog" : "ArmoryCatalog", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            Text(panel.transform, shop ? "商城" : "仓库", 38f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.86f), new Vector2(0.65f, 0.98f));
            Text(panel.transform, $"COINS  {cachedCatalog?.coins ?? cachedInventory?.coins ?? 0:N0}    ·    服务器目录 / 所有权", 16f, LobbyViewFactory.Gold, new Vector2(0.04f, 0.78f), new Vector2(0.75f, 0.86f));
            var filters = new[] { "Rifle", "Pistol", "Shotgun", "Smg", "Sniper" };
            for (var i = 0; i < filters.Length; i++)
            {
                var key = filters[i];
                var label = key == "Rifle" ? "步枪" : key == "Pistol" ? "手枪" : key == "Shotgun" ? "霰弹枪" : key == "Smg" ? "冲锋枪" : "狙击枪";
                var x0 = 0.04f + i * 0.135f;
                Button(panel.transform, label, catalogFilter == key ? LobbyViewFactory.Teal : new Color(0.06f, 0.14f, 0.22f, 1f),
                    new Vector2(x0, 0.72f), new Vector2(x0 + 0.12f, 0.78f), () => { catalogFilter = key; RenderCatalog(shop); });
            }
            var items = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>()).Where(x => x != null && x.itemType == "Weapon" && GetCategory(x.itemId) == catalogFilter).ToArray();
            if (items.Length == 0)
            {
                Text(panel.transform, "暂无目录数据", 24f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.42f), new Vector2(0.9f, 0.58f), TextAlignmentOptions.Center);
                Button(panel.transform, "重试", LobbyViewFactory.Cyan, new Vector2(0.4f, 0.25f), new Vector2(0.6f, 0.36f), () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
                return;
            }
            for (var i = 0; i < items.Length; i++) CreateWeaponCard(panel.transform, items[i], shop, i, items.Length);
        }

        private void CreateWeaponCard(Transform parent, CatalogItemDto item, bool shop, int index, int count)
        {
            var columns = 3;
            var rows = Mathf.CeilToInt(count / (float)columns);
            var col = index % columns;
            var row = index / columns;
            var x0 = 0.035f + col * 0.32f;
            var x1 = x0 + 0.29f;
            var y1 = 0.68f - row * (0.60f / Mathf.Max(1, rows));
            var y0 = y1 - 0.145f;
            var card = LobbyViewFactory.Panel("WeaponCard_" + item.itemId, parent, item.isOwned ? LobbyViewFactory.PanelAlt : new Color(0.07f, 0.09f, 0.15f, 0.95f), new Vector2(x0, y0), new Vector2(x1, y1));
            var stats = weaponAssets.FindStats(item.itemId);
            var level = cachedCatalog?.level ?? session.Profile?.level ?? 0;
            var coins = cachedCatalog?.coins ?? cachedInventory?.coins ?? 0;
            var state = item.isOwned ? "已拥有" : item.unlockLevel > level
                ? $"等级 {item.unlockLevel} 解锁 · 价格 {item.priceCoins:N0} COINS"
                : item.priceCoins > coins ? $"金币不足 · 价格 {item.priceCoins:N0} COINS"
                : $"价格 {item.priceCoins:N0} COINS";
            Text(card.transform, item.displayName, 18f, item.isOwned ? LobbyViewFactory.PrimaryText : LobbyViewFactory.Muted, new Vector2(0.05f, 0.64f), new Vector2(0.95f, 0.92f));
            Text(card.transform, state + $"\n伤害 {stats.damage:0}  射速 {stats.roundsPerMinute:0}\n弹容 {stats.magazineSize:0}  后坐 {stats.recoil:0.##}", 11f, item.isOwned ? LobbyViewFactory.Teal : LobbyViewFactory.Muted, new Vector2(0.05f, 0.24f), new Vector2(0.95f, 0.61f));
            var previewButton = LobbyViewFactory.Button("Preview_" + item.itemId, card.transform, "预览", LobbyViewFactory.Cyan, new Vector2(0.05f, 0.06f), new Vector2(shop && !item.isOwned ? 0.46f : 0.95f, 0.20f));
            previewButton.onClick.AddListener(() =>
            {
                selectedWeapon = item;
                detailsFromShop = shop;
                Navigate(LobbyPage.WeaponDetails);
            });
            if (shop && !item.isOwned)
            {
                var buyButton = LobbyViewFactory.Button("Buy_" + item.itemId, card.transform, "购买", LobbyViewFactory.Gold, new Vector2(0.52f, 0.06f), new Vector2(0.95f, 0.20f));
                buyButton.onClick.AddListener(() => _ = PurchaseAsync(item));
                buyButton.interactable = item.isActive && item.isImplemented && item.unlockLevel <= level && item.priceCoins <= coins;
            }
        }

        private async Task PurchaseAsync(CatalogItemDto item)
        {
            if (item == null) return;
            var key = Guid.NewGuid().ToString("N");
            status.text = "购买处理中…";
            var result = await api.PurchaseAsync(new PurchaseRequest { itemId = item.itemId, quantity = 1, idempotencyKey = key }, pageCts.Token);
            if (result.Success)
            {
                status.text = result.Data.replayed ? "购买请求已幂等重放" : "购买成功，库存已同步";
                if (session.Profile != null)
                {
                    session.Profile.coins = result.Data.coins;
                    session.ApplyProfile(session.Profile);
                }
                cachedCatalog = (await api.GetShopCatalogAsync(pageCts.Token)).Data;
                cachedInventory = (await api.GetInventoryAsync(pageCts.Token)).Data;
                if (currentPage == LobbyPage.Shop) RenderCatalog(true);
                else if (currentPage == LobbyPage.WeaponDetails)
                {
                    selectedWeapon = cachedCatalog?.items?.FirstOrDefault(x => x.itemId == item.itemId) ?? item;
                    RenderWeaponDetails();
                }
            }
            else if (result.Code == "AUTH_UNAUTHORIZED") Navigate(LobbyPage.SessionExpired);
            else status.text = ApiErrorMessages.ToUserMessage(result);
        }

        private void RenderWeaponDetails()
        {
            if (selectedWeapon == null) { Navigate(LobbyPage.Armory); return; }
            var panel = Panel("WeaponDetails", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            weaponAssets.TryGet(selectedWeapon.itemId, out var asset);
            var stats = weaponAssets.FindStats(selectedWeapon.itemId);
            Text(panel.transform, selectedWeapon.displayName, 38f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.87f), new Vector2(0.58f, 0.98f));
            Text(panel.transform, selectedWeapon.isOwned ? "OWNED" : "LOCKED", 17f, selectedWeapon.isOwned ? LobbyViewFactory.Teal : LobbyViewFactory.Coral, new Vector2(0.04f, 0.79f), new Vector2(0.28f, 0.87f));
            Text(panel.transform, selectedWeapon.isOwned ? "已拥有 · 可装备" : $"价格  {selectedWeapon.priceCoins:N0} COINS", 21f, selectedWeapon.isOwned ? LobbyViewFactory.Teal : LobbyViewFactory.Gold, new Vector2(0.62f, 0.82f), new Vector2(0.95f, 0.94f), TextAlignmentOptions.Right);

            var previewFrame = LobbyViewFactory.Panel("WeaponPreviewFrame", panel.transform, new Color(0.02f, 0.09f, 0.16f, 0.98f), new Vector2(0.04f, 0.16f), new Vector2(0.54f, 0.76f));
            previewFrame.GetComponent<Image>().raycastTarget = false;
            var previewObject = new GameObject("WeaponPreview3D", typeof(RectTransform), typeof(RawImage), typeof(WeaponPreviewController));
            previewObject.transform.SetParent(previewFrame.transform, false);
            LobbyViewFactory.Place(previewObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);
            previewObject.GetComponent<WeaponPreviewController>().Initialize(weaponAssets.FindPreviewPrefab(selectedWeapon.itemId));
            Text(previewFrame.transform, "拖拽旋转  ·  滚轮缩放", 14f, LobbyViewFactory.Muted, new Vector2(0.06f, 0.04f), new Vector2(0.94f, 0.13f), TextAlignmentOptions.Center);

            var chartPanel = LobbyViewFactory.Panel("WeaponStatsChart", panel.transform, new Color(0.025f, 0.075f, 0.14f, 0.98f), new Vector2(0.59f, 0.28f), new Vector2(0.95f, 0.78f));
            Text(chartPanel.transform, "战斗属性", 23f, LobbyViewFactory.PrimaryText, new Vector2(0.08f, 0.84f), new Vector2(0.92f, 0.97f), TextAlignmentOptions.Center);
            var chartObject = new GameObject("WeaponRadarChart", typeof(RectTransform), typeof(CanvasRenderer), typeof(WeaponRadarChart));
            chartObject.transform.SetParent(chartPanel.transform, false);
            LobbyViewFactory.Place(chartObject.GetComponent<RectTransform>(), new Vector2(0.16f, 0.13f), new Vector2(0.84f, 0.80f));
            chartObject.GetComponent<WeaponRadarChart>().SetStats(stats);
            Text(chartPanel.transform, $"伤害  {stats.damage:0}", 14f, LobbyViewFactory.PrimaryText, new Vector2(0.35f, 0.70f), new Vector2(0.65f, 0.82f), TextAlignmentOptions.Center);
            Text(chartPanel.transform, $"射速  {stats.roundsPerMinute:0} RPM", 14f, LobbyViewFactory.PrimaryText, new Vector2(0.60f, 0.43f), new Vector2(0.98f, 0.55f), TextAlignmentOptions.Right);
            Text(chartPanel.transform, $"弹容量  {stats.magazineSize:0}", 14f, LobbyViewFactory.PrimaryText, new Vector2(0.32f, 0.06f), new Vector2(0.68f, 0.18f), TextAlignmentOptions.Center);
            Text(chartPanel.transform, $"后坐力  {stats.recoil:0.##}", 14f, LobbyViewFactory.PrimaryText, new Vector2(0.02f, 0.43f), new Vector2(0.40f, 0.55f));

            Text(panel.transform, "业务 ID  " + selectedWeapon.itemId + "\n资源键  " + (asset?.assetKey ?? selectedWeapon.assetKey), 14f, LobbyViewFactory.Muted, new Vector2(0.04f, 0.05f), new Vector2(0.40f, 0.14f));
            Button(panel.transform, "重置视角", LobbyViewFactory.Cyan, new Vector2(0.59f, 0.16f), new Vector2(0.76f, 0.25f), () =>
            {
                var controller = previewObject.GetComponent<WeaponPreviewController>();
                controller.Initialize(weaponAssets.FindPreviewPrefab(selectedWeapon.itemId));
            });
            if (!selectedWeapon.isOwned)
                Button(panel.transform, "购买", LobbyViewFactory.Gold, new Vector2(0.78f, 0.16f), new Vector2(0.95f, 0.25f), () => _ = PurchaseAsync(selectedWeapon));
            else
            {
                Button(panel.transform, selectedWeapon.slotType == "Secondary" ? "装备为副武器" : "装备为主武器", LobbyViewFactory.Gold,
                    new Vector2(0.59f, 0.05f), new Vector2(0.76f, 0.14f), () => _ = EquipWeaponAsync(selectedWeapon));
                if (asset != null && asset.supportsVerifiedAttachments)
                    Button(panel.transform, "打开配件装配", LobbyViewFactory.Teal, new Vector2(0.78f, 0.16f), new Vector2(0.95f, 0.25f), () => _ = RenderAttachmentsAsync());
                else
                    Text(panel.transform, "配件：尚未适配", 15f, LobbyViewFactory.Muted, new Vector2(0.78f, 0.16f), new Vector2(0.95f, 0.25f), TextAlignmentOptions.Center);
            }
            Button(panel.transform, detailsFromShop ? "返回商城" : "返回仓库", new Color(0.08f, 0.18f, 0.29f, 1f), new Vector2(0.78f, 0.05f), new Vector2(0.95f, 0.14f), () => Navigate(detailsFromShop ? LobbyPage.Shop : LobbyPage.Armory));
        }

        private async Task EquipWeaponAsync(CatalogItemDto item)
        {
            if (item == null || !item.isOwned || session.Loadout == null) { status.text = "未拥有或配装尚未加载"; return; }
            var request = new LoadoutRequest
            {
                primaryWeaponId = item.slotType == "Primary" ? item.itemId : session.Loadout.primaryWeaponId,
                secondaryWeaponId = item.slotType == "Secondary" ? item.itemId : session.Loadout.secondaryWeaponId,
                throwableId = null,
                expectedVersion = session.Loadout.version
            };
            status.text = "正在保存服务器配装…";
            var result = await api.UpdateLoadoutAsync(request, pageCts.Token);
            if (!result.Success) { status.text = ApiErrorMessages.ToUserMessage(result); return; }
            session.ApplyLoadout(result.Data);
            status.text = item.slotType == "Secondary" ? "已装备为副武器" : "已装备为主武器";
        }

        private static string GetCategory(string itemId)
        {
            if (itemId.Contains("pistol") || itemId.Contains("handgun")) return "Pistol";
            if (itemId.Contains("shotgun")) return "Shotgun";
            if (itemId.Contains("smg")) return "Smg";
            if (itemId.Contains("sniper")) return "Sniper";
            return "Rifle";
        }

        private async Task RenderAttachmentsAsync()
        {
            var token = pageCts.Token;
            var compatibility = await api.GetAttachmentCompatibilityAsync(token);
            var inventory = await api.GetInventoryAsync(token);
            var loadout = await api.GetLoadoutAttachmentsAsync(token);
            if (!compatibility.Success || !inventory.Success || !loadout.Success) { status.text = "配件数据加载失败，请重试"; return; }
            cachedCompatibility = compatibility.Data ?? Array.Empty<AttachmentCompatibilityDto>();
            cachedInventory = inventory.Data;
            ClearBody();
            var panel = Panel("AttachmentPanel", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            Text(panel.transform, "配件装配 · " + selectedWeapon.displayName, 34f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.84f), new Vector2(0.8f, 0.97f));
            var owned = new HashSet<string>((cachedInventory.items ?? Array.Empty<InventoryItemDto>()).Where(x => x.quantity > 0).Select(x => x.itemId), StringComparer.Ordinal);
            var selections = new List<AttachmentSelectionRequest>();
            var slots = new[] { "Optic", "Muzzle", "Magazine" };
            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                var compat = cachedCompatibility.Where(x => x.weaponId == selectedWeapon.itemId && x.slotType == slot).ToArray();
                var row = LobbyViewFactory.Panel("AttachmentRow_" + slot, panel.transform, LobbyViewFactory.PanelAlt, new Vector2(0.05f, 0.62f - i * 0.17f), new Vector2(0.95f, 0.75f - i * 0.17f));
                Text(row.transform, slot, 20f, LobbyViewFactory.PrimaryText, new Vector2(0.03f, 0.2f), new Vector2(0.22f, 0.8f));
                var implemented = compat.Where(x => x.isImplemented && owned.Contains(x.attachmentId)).ToArray();
                if (implemented.Length == 0)
                {
                    Text(row.transform, compat.Any(x => x.isImplemented) ? "未拥有可用配件" : "尚未适配", 16f, LobbyViewFactory.Muted, new Vector2(0.26f, 0.2f), new Vector2(0.78f, 0.8f));
                    continue;
                }
                var chosen = implemented[0];
                selections.Add(new AttachmentSelectionRequest { attachmentSlot = slot, attachmentItemId = chosen.attachmentId });
                Text(row.transform, chosen.attachmentId + " · " + chosen.calibrationKey, 15f, LobbyViewFactory.Teal, new Vector2(0.26f, 0.2f), new Vector2(0.92f, 0.8f));
            }
            Text(panel.transform, "仅显示服务端兼容且玩家已拥有的真实组合；未拥有或未校准组合不会被伪造。", 15f, LobbyViewFactory.Muted, new Vector2(0.05f, 0.08f), new Vector2(0.65f, 0.17f));
            Button(panel.transform, "保存真实装配", LobbyViewFactory.Teal, new Vector2(0.70f, 0.11f), new Vector2(0.95f, 0.24f), () => _ = SaveAttachmentsAsync(loadout.Data.version, selections));
        }

        private async Task SaveAttachmentsAsync(long version, List<AttachmentSelectionRequest> selections)
        {
            var result = await api.UpdateLoadoutAttachmentsAsync(new LoadoutAttachmentsRequest
            {
                expectedVersion = version, weaponSlot = selectedWeapon.slotType == "Secondary" ? "Secondary" : "Primary", attachments = selections.ToArray()
            }, pageCts.Token);
            if (!result.Success)
            {
                status.text = ApiErrorMessages.ToUserMessage(result);
                return;
            }

            var optic = selections.FirstOrDefault(x => x.attachmentSlot == "Optic")?.attachmentItemId ?? string.Empty;
            var muzzle = selections.FirstOrDefault(x => x.attachmentSlot == "Muzzle")?.attachmentItemId ?? string.Empty;
            var magazine = selections.FirstOrDefault(x => x.attachmentSlot == "Magazine")?.attachmentItemId ?? string.Empty;
            FPWeaponAttachmentView.SavePersisted(selectedWeapon.itemId, optic, muzzle, magazine);
            status.text = "配件已保存，版本 " + result.Data.version;
        }

        private void RenderUpgrades()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            var panel = Panel("UpgradesPanel", LobbyViewFactory.PanelAlt, new Vector2(0f, 0f), new Vector2(1f, 1f));
            var profile = session.Profile;
            Text(panel.transform, "能力升级", 40f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.84f), new Vector2(0.65f, 0.97f));
            Text(panel.transform, "升级数据由服务器保存；本地只负责编辑待提交值。", 16f, LobbyViewFactory.Muted, new Vector2(0.04f, 0.76f), new Vector2(0.8f, 0.84f));
            var up = profile?.upgrades ?? new UpgradeLevelsDto();
            var damage = AddUpgradeRow(panel.transform, "伤害", up.upDamage, 0.57f);
            var ammo = AddUpgradeRow(panel.transform, "弹容量", up.upAmmoCap, 0.41f);
            var health = AddUpgradeRow(panel.transform, "最大生命", up.upMaxHealth, 0.25f);
            Button(panel.transform, "提交升级", LobbyViewFactory.Teal, new Vector2(0.70f, 0.25f), new Vector2(0.95f, 0.40f), async () =>
            {
                var result = await api.UpdateUpgradesAsync(new UpgradeRequest { upDamage = damage(), upAmmoCap = ammo(), upMaxHealth = health() }, pageCts.Token);
                if (result.Success) { session.ApplyProfile(result.Data); status.text = "升级已同步"; RenderUpgrades(); }
                else status.text = ApiErrorMessages.ToUserMessage(result);
            });
        }

        private Func<int> AddUpgradeRow(Transform parent, string label, int value, float y)
        {
            var row = LobbyViewFactory.Panel("Upgrade_" + label, parent, LobbyViewFactory.PanelAlt, new Vector2(0.05f, y), new Vector2(0.60f, y + 0.12f));
            Text(row.transform, label + "  " + value + "/5", 21f, LobbyViewFactory.PrimaryText, new Vector2(0.05f, 0.2f), new Vector2(0.75f, 0.8f));
            var current = value;
            Button(row.transform, "−", LobbyViewFactory.Coral, new Vector2(0.80f, 0.15f), new Vector2(0.88f, 0.85f), () => current = Mathf.Max(0, current - 1));
            Button(row.transform, "+", LobbyViewFactory.Teal, new Vector2(0.90f, 0.15f), new Vector2(0.98f, 0.85f), () => current = Mathf.Min(5, current + 1));
            return () => current;
        }

        private void RenderSettings()
        {
            var panel = Panel("SettingsPanel", LobbyViewFactory.PanelAlt, new Vector2(0.16f, 0.12f), new Vector2(0.84f, 0.88f));
            Text(panel.transform, "设置", 40f, LobbyViewFactory.PrimaryText, new Vector2(0.08f, 0.78f), new Vector2(0.92f, 0.94f), TextAlignmentOptions.Center);
            var music = LobbyViewFactory.Input("Music", panel.transform, "音乐音量 0-1", new Vector2(0.15f, 0.56f), new Vector2(0.85f, 0.66f));
            music.text = PlayerPrefs.GetFloat(SettingsMusicKey, 1f).ToString("0.00");
            var sensitivity = LobbyViewFactory.Input("Sensitivity", panel.transform, "鼠标灵敏度", new Vector2(0.15f, 0.40f), new Vector2(0.85f, 0.50f));
            sensitivity.text = PlayerPrefs.GetFloat(SettingsSensitivityKey, 1f).ToString("0.00");
            Button(panel.transform, "保存本地设置", LobbyViewFactory.Teal, new Vector2(0.15f, 0.22f), new Vector2(0.85f, 0.33f), () =>
            {
                if (float.TryParse(music.text, out var musicValue)) PlayerPrefs.SetFloat(SettingsMusicKey, Mathf.Clamp01(musicValue));
                if (float.TryParse(sensitivity.text, out var sensitivityValue)) PlayerPrefs.SetFloat(SettingsSensitivityKey, Mathf.Clamp(sensitivityValue, 0.1f, 5f));
                PlayerPrefs.Save(); status.text = "设置已保存在本地";
            });
        }

        private void RenderHud()
        {
            var panel = Panel("HudPanel", new Color(0f, 0f, 0f, 0.24f), new Vector2(0f, 0f), new Vector2(1f, 1f));
            Text(panel.transform, "HP  100   |   AMMO  30 / 90", 22f, LobbyViewFactory.PrimaryText, new Vector2(0.04f, 0.05f), new Vector2(0.45f, 0.12f));
            Text(panel.transform, "CROSSHAIR", 14f, LobbyViewFactory.Teal, new Vector2(0.47f, 0.47f), new Vector2(0.53f, 0.53f), TextAlignmentOptions.Center);
            Button(panel.transform, "暂停", new Color(0.05f, 0.15f, 0.23f, 0.9f), new Vector2(0.86f, 0.88f), new Vector2(0.96f, 0.96f), () => Navigate(LobbyPage.Pause));
        }

        private void RenderPause()
        {
            var panel = Panel("PausePanel", new Color(0.015f, 0.04f, 0.10f, 0.96f), new Vector2(0.25f, 0.24f), new Vector2(0.75f, 0.78f));
            Text(panel.transform, "暂停", 38f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.68f), new Vector2(0.9f, 0.88f), TextAlignmentOptions.Center);
            Button(panel.transform, "继续", LobbyViewFactory.Teal, new Vector2(0.18f, 0.47f), new Vector2(0.82f, 0.60f), () => Navigate(LobbyPage.Hud));
            Button(panel.transform, "设置", LobbyViewFactory.Cyan, new Vector2(0.18f, 0.29f), new Vector2(0.82f, 0.42f), () => Navigate(LobbyPage.Settings));
            Button(panel.transform, "返回大厅", LobbyViewFactory.Coral, new Vector2(0.18f, 0.11f), new Vector2(0.82f, 0.24f), () => SceneManager.LoadScene("Lobby"));
        }

        private void RenderResults()
        {
            var panel = Panel("ResultsPanel", LobbyViewFactory.PanelAlt, new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.8f));
            Text(panel.transform, "任务结算", 40f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.7f), new Vector2(0.9f, 0.9f), TextAlignmentOptions.Center);
            Text(panel.transform, "结算必须携带稳定 ClientMatchId，重复提交只结算一次。", 18f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.48f), new Vector2(0.9f, 0.64f), TextAlignmentOptions.Center);
            Button(panel.transform, "返回大厅", LobbyViewFactory.Teal, new Vector2(0.22f, 0.18f), new Vector2(0.78f, 0.31f), () => Navigate(LobbyPage.Lobby));
        }

        private void RenderError(string message, Action retry)
        {
            ClearBody();
            retryAction = retry;
            var panel = Panel("ErrorPanel", new Color(0.15f, 0.04f, 0.10f, 0.96f), new Vector2(0.2f, 0.23f), new Vector2(0.8f, 0.77f));
            Text(panel.transform, "连接或服务错误", 34f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.68f), new Vector2(0.9f, 0.88f), TextAlignmentOptions.Center);
            Text(panel.transform, message, 19f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.43f), new Vector2(0.9f, 0.64f), TextAlignmentOptions.Center);
            if (retry != null) Button(panel.transform, "重试", LobbyViewFactory.Teal, new Vector2(0.18f, 0.18f), new Vector2(0.48f, 0.31f), () => retry());
            Button(panel.transform, apiAvailable ? "取消 / 登录页" : "返回连接检查", LobbyViewFactory.Cyan, new Vector2(0.52f, 0.18f), new Vector2(0.82f, 0.31f), () => Navigate(apiAvailable ? LobbyPage.Login : LobbyPage.Boot));
        }

        private void RenderSessionExpired()
        {
            api.ClearToken(); session.Clear(); SetNavigationVisible(false);
            var panel = Panel("SessionExpired", new Color(0.15f, 0.09f, 0.03f, 0.96f), new Vector2(0.2f, 0.3f), new Vector2(0.8f, 0.7f));
            Text(panel.transform, "会话已过期", 34f, LobbyViewFactory.PrimaryText, new Vector2(0.1f, 0.62f), new Vector2(0.9f, 0.84f), TextAlignmentOptions.Center);
            Text(panel.transform, "请重新登录。持久化数据仍由服务器保存。", 18f, LobbyViewFactory.Muted, new Vector2(0.1f, 0.40f), new Vector2(0.9f, 0.58f), TextAlignmentOptions.Center);
            Button(panel.transform, "返回登录", LobbyViewFactory.Gold, new Vector2(0.28f, 0.17f), new Vector2(0.72f, 0.30f), () => Navigate(LobbyPage.Login));
        }

        private void Logout()
        {
            api.ClearToken();
            session.Clear();
            SetNavigationVisible(false);
            Navigate(LobbyPage.Login);
        }
    }
}
