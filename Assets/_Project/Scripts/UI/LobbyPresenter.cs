using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Network;
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
    public sealed partial class LobbyPresenter : MonoBehaviour
    {
        private readonly List<GameObject> bodyObjects = new();
        private readonly List<Button> navigationButtons = new();
        private IApiClient api;
        private AccountSession session;
        private WeaponAssetCatalog weaponAssets;
        private GameObject canvas;
        private GameObject navigationRoot;
        private Transform body;
        private RectTransform bodyRect;
        private TMP_Text status;
        private CancellationTokenSource pageCts;
        private Action retryAction;
        private string gameplaySceneName = "Gameplay";
        private LobbyPage currentPage;
        private string currentHotPageId;
        private bool apiAvailable;
        // ---- 好友/在场（2026-09-20 需求2）：壳页心跳拉好友列表（服务器侧 LastSeenUtc 锚点）+ 主页好友卡缓存 ----
        private CancellationTokenSource presenceCts;
        private FriendListDto cachedFriends;
        private const int PresenceIntervalSeconds = 30;
        private ShopCatalogDto cachedCatalog;
        private InventoryDto cachedInventory;
        private AttachmentCompatibilityDto[] cachedCompatibility = Array.Empty<AttachmentCompatibilityDto>();
        private CatalogItemDto selectedWeapon;
        private bool detailsFromShop;
        private string catalogFilter = "Rifle";

        public void Initialize(string gameplayScene, WeaponAssetCatalog catalog)
        {
            gameplaySceneName = string.IsNullOrWhiteSpace(gameplayScene) ? "Arena" : gameplayScene;
            weaponAssets = catalog != null ? catalog : WeaponAssetCatalog.LoadOrDefault();
            api = AppRoot.Instance.ApiClient;
            session = AppRoot.Instance.Session;
            EnsureInputSystemEventSystem();
            session.Changed += RefreshLobbyLoadout;
            if (SocialSession.Instance != null) SocialSession.Instance.Changed += OnSocialChanged;
            BuildShell();
            SetNavigationVisible(false);
            Navigate(AppRoot.Instance.SessionExpiredPending ? LobbyPage.SessionExpired : LobbyPage.Boot);
        }

        public void ShowSessionExpired()
        {
            if (session != null && body != null) Navigate(LobbyPage.SessionExpired);
        }

        private void OnDestroy()
        {
            mapUpdateCancellation?.Cancel();
            if (session != null) session.Changed -= RefreshLobbyLoadout;
            if (SocialSession.Instance != null) SocialSession.Instance.Changed -= OnSocialChanged;
            CloseSocialModal();
            pageCts?.Cancel();
            pageCts?.Dispose();
            StopPresenceLoop();
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

        public void Navigate(LobbyPage page)
        {
            SocialWindow.CloseActive();
            CloseSocialModal();
            pageCts?.Cancel();
            pageCts?.Dispose();
            pageCts = new CancellationTokenSource();
            retryAction = null;
            if (IsProtectedPage(page) && (session == null || !session.IsAuthenticated)) page = LobbyPage.Login;
            currentPage = page;
            ClearBody();
            status.text = string.Empty;
            // 审计 2026-09-16 §5.3-3：页面上下文兜底——离开等待房间页时卸载挂在本画布上的聊天 UI
            //（保留房间会话历史；留房返房不丢）。旧实现 Navigate 只清 bodyObjects，Canvas 直挂的
            // ChatHud 不在列表内 → 残留到大厅/登录页且 Enter 可重新打开。
            Chat.ChatController.SetPageContext(
                canvas != null ? canvas.GetComponent<Canvas>() : null,
                chatAllowed: page == LobbyPage.WaitingRoom);
            // 2026-09-10 审计 §5：导航可见性=页面状态不变量，由本唯一入口集中执行——不再依赖各跳转
            // 分支自行恢复（ROOM_CLOSED/ROOM_NOT_FOUND 返厅、手动退房、战斗返回、登录成功全覆盖）。
            // 渲染分支内的既有 SetNavigationVisible 调用保持幂等断言，不承担恢复职责。
            SetNavigationVisible(WantsNavigationVisible(page));
            switch (page)
            {
                case LobbyPage.Boot: RenderBoot(pageCts.Token); break;
                case LobbyPage.Login: RenderLoginPage(); break;
                case LobbyPage.Register: RenderRegisterPage(); break;
                case LobbyPage.Identity: RenderIdentity(); break;
                case LobbyPage.Lobby: RenderLobby(); break;

                case LobbyPage.Armory: LoadCatalogAndRender(false, pageCts.Token); break;
                case LobbyPage.WeaponDetails: RenderWeaponDetails(); break;
                case LobbyPage.Shop: LoadCatalogAndRender(true, pageCts.Token); break;

                case LobbyPage.Settings: RenderSettings(); break;
                case LobbyPage.Hud: RenderHud(); break;
                case LobbyPage.Pause: RenderPause(); break;
                case LobbyPage.Results: RenderResults(); break;
                case LobbyPage.Error: RenderError("发生未知错误", retryAction); break;
                case LobbyPage.SessionExpired: RenderSessionExpired(); break;
                case LobbyPage.Loading: RenderLoading(); break;
                case LobbyPage.OnlineJoin: RenderOnlineJoin(); break;
                case LobbyPage.WaitingRoom: RenderWaitingRoom(); break;
                case LobbyPage.Friends: RenderFriends(); break;
                // 热更页（稳定 seam）：枚举路由只是防御性转发——真实入口是 NavigateHot(id)
                case LobbyPage.Hot: NavigateHot(currentHotPageId); break;
            }
            UpdateNavSelection();
        }

        /// <summary>热页导航（热更 seam 的 C# 消费端）：与 Navigate 相同的前置语义
        /// （认证门 / pageCts 重建 / ClearBody / 聊天上下文 / 导航可见性单点），
        /// 页面容器建好后把渲染交给注册的 Lua 委托；渲染异常不炸壳（状态行提示）。</summary>
        public void NavigateHot(string id)
        {
            if (id == "hello" && !Debug.isDebugBuild) return;
            if (!HotPageRegistry.TryGet(id, out var page))
            {
                // 页面已不存在（热更脚本回退等场景）：回作战大厅，避免空白 body
                if (currentPage == LobbyPage.Hot) { Navigate(LobbyPage.Lobby); return; }
                return;
            }
            if (session == null || !session.IsAuthenticated)
            {
                Navigate(LobbyPage.Login);
                return;
            }
            pageCts?.Cancel();
            pageCts?.Dispose();
            pageCts = new CancellationTokenSource();
            retryAction = null;
            currentPage = LobbyPage.Hot;
            currentHotPageId = page.Id;
            ClearBody();
            status.text = string.Empty;
            Chat.ChatController.SetPageContext(
                canvas != null ? canvas.GetComponent<Canvas>() : null,
                chatAllowed: false);
            SetNavigationVisible(true);
            var root = PageRoot("HotPage_" + page.Id);
            try
            {
                page.Render(root);
            }
            catch (Exception e)
            {
                status.text = "热页渲染异常：" + e.Message;
                UnityEngine.Debug.LogError("[HotUpdate] hot page render failed (" + page.Id + "): " + e.Message);
            }
            UpdateNavSelection();
        }

        private void ClearBody()
        {
            CloseSocialModal();
            CloseAccountPanel();
            foreach (var go in bodyObjects)
            {
                if (go == null) continue;
                // EditMode 测试（结构断言驱动私有渲染方法）必须用 DestroyImmediate；运行时保持 Destroy 语义
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            bodyObjects.Clear();
        }

        /// <summary>清空容器直接子节点（房间列表/好友列表等每轮重建的动态行）；EditMode 用 DestroyImmediate。</summary>
        private static void ClearChildren(Transform parent)
        {
            if (parent == null) return;
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                child.gameObject.SetActive(false);
                child.SetParent(null, false);
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
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

        // ---- Docs/20 design-system helpers (new pages use these) ----

        private Image backgroundImage;

        private void SetBackground(string artKey)
        {
            if (backgroundImage != null) backgroundImage.sprite = UIArt.Get(artKey);
        }

        private GameObject StyledPanel(string name, Transform parent, Color fill, Vector2 min, Vector2 max)
        {
            var go = UIComponents.Panel(name, parent, fill, min, max);
            // Page roots own their descendants. Persistent shell/social panels must survive navigation.
            if (parent == body) bodyObjects.Add(go);
            return go;
        }

        private TextMeshProUGUI StyledText(Transform parent, string value, int size, Color color, Vector2 min, Vector2 max,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center, FontStyles style = FontStyles.Normal) =>
            UITypography.Text("T_" + bodyObjects.Count, parent, value, size, color, min, max, alignment, style);

        private Button StyledButton(Transform parent, string label, UIComponents.ButtonKind kind, Vector2 min, Vector2 max, UnityEngine.Events.UnityAction action)
        {
            var button = UIComponents.Button("Btn_" + bodyObjects.Count, parent, label, kind, min, max);
            button.onClick.AddListener(action);
            return button;
        }

        private TMP_InputField StyledInput(string name, Transform parent, string placeholder, Vector2 min, Vector2 max) =>
            UIComponents.Input(name, parent, placeholder, min, max);

        /// <summary>Standard page-enter motion (fade + slide up) applied to a page root.</summary>
        private static void PlayEnter(GameObject pageRoot)
        {
            if (pageRoot == null) return;
            var group = pageRoot.GetComponent<CanvasGroup>();
            if (group == null) group = pageRoot.AddComponent<CanvasGroup>();
            UIMotion.FadeSlideIn(group, pageRoot.GetComponent<RectTransform>());
        }

        private void SetNavigationInteractable(bool value)
        {
            foreach (var button in navigationButtons)
                if (button != null) button.interactable = value && apiAvailable && session != null && session.IsAuthenticated;
        }

        private void SetNavigationVisible(bool value)
        {
            if (navigationRoot != null) navigationRoot.SetActive(value);
            // 2026-09-16 需求2：导航隐藏时 body 回收为全屏。旧实现 body 恒锚在导航右侧
            // （0.155-0.99），登录/启动等无导航页的卡片在 body 内居中≠屏幕居中，客户端实机右偏。
            if (bodyRect != null)
            {
                bodyRect.anchorMin = value ? BodyAnchorMinVisible : Vector2.zero;
                bodyRect.anchorMax = value ? (socialExpanded ? new Vector2(0.71f, BodyAnchorMaxVisible.y) : BodyAnchorMaxVisible) : Vector2.one;
                bodyRect.offsetMin = Vector2.zero;
                bodyRect.offsetMax = Vector2.zero;
            }
            if (session == null || !session.IsAuthenticated) { cachedFriends = null; socialExpanded = false; CloseSocialModal(); }
            if (socialRail != null) socialRail.SetActive(value);
            if (socialDrawer != null) socialDrawer.SetActive(value && socialExpanded);
            SetNavigationInteractable(value);
            // 在场心跳随导航可见性启停：壳页（大厅/仓库/商城等）运行；等待房间由房间心跳承担；
            // 登录/等待房/战斗等全屏页停止（战斗中在场状态由房间成员资格承载）
            if (value) StartPresenceLoop(); else StopPresenceLoop();
        }

        private void StartPresenceLoop() { OnSocialChanged(); SocialSession.Instance?.RefreshSoon(); }
        private void StopPresenceLoop() { }
        private void OnSocialChanged()
        {
            // A service reconnect must not blank a snapshot already displayed by this page.
            // Explicit sign-out clears the cache in the account lifecycle handler.
            if (SocialSession.Instance?.Friends != null) cachedFriends = SocialSession.Instance.Friends;
            RefreshSocialViews();
        }

        private static bool IsProtectedPage(LobbyPage page)
        {
            return page == LobbyPage.Lobby || page == LobbyPage.Armory ||
                   page == LobbyPage.WeaponDetails || page == LobbyPage.Shop ||
                   page == LobbyPage.Settings || page == LobbyPage.Hud || page == LobbyPage.Pause ||
                   page == LobbyPage.Results || page == LobbyPage.Loading || page == LobbyPage.WaitingRoom ||
                   page == LobbyPage.Friends || page == LobbyPage.Hot;
        }

        /// <summary>页面 → 导航栏可见性不变量（2026-09-10 审计 §5）：已认证的作战大厅壳页
        /// （大厅/任务/仓库/武器详情/商城/升级/设置/联机入口）显示完整侧栏；登录/注册/身份确认、
        /// 等待房间、加载过渡、错误、会话过期、结算/暂停等全屏流程页一律隐藏。</summary>
        private static bool WantsNavigationVisible(LobbyPage page)
        {
            switch (page)
            {
                case LobbyPage.Lobby:
                case LobbyPage.Armory:
                case LobbyPage.WeaponDetails:
                case LobbyPage.Shop:
                case LobbyPage.Settings:
                case LobbyPage.OnlineJoin:
                case LobbyPage.Friends:
                case LobbyPage.Hot:
                    return true;
                default:
                    return false;
            }
        }

        private async Task SubmitAuthAsync(bool register, TMP_InputField usernameInput, TMP_InputField passwordInput, Button submitButton, RectTransform card)
        {
            var username = usernameInput != null ? usernameInput.text : string.Empty;
            var password = passwordInput != null ? passwordInput.text : string.Empty;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                status.text = "请输入用户名和密码";
                UIMotion.Shake(card);
                return;
            }
            if (!apiAvailable)
            {
                status.text = "后端服务未就绪，请先完成连接检查";
                UIMotion.Shake(card);
                return;
            }
            if (submitButton != null) submitButton.interactable = false;
            status.text = "请求处理中…";
            var token = pageCts.Token;
            var result = register ? await api.RegisterAsync(username.Trim(), password, token) : await api.LoginAsync(username.Trim(), password, token);
            if (token.IsCancellationRequested) return;
            if (!result.Success)
            {
                if (result.Code == "AUTH_UNAUTHORIZED") { status.text = "用户名或密码不正确"; UIMotion.Shake(card); }
                else if (ApiClientErrorCodes.IsTransportFailure(result.Code))
                {
                    apiAvailable = false;
                    SetNavigationInteractable(false);
                    RenderError(ApiErrorMessages.ToUserMessage(result), () => Navigate(LobbyPage.Boot));
                    return;
                }
                else { status.text = ApiErrorMessages.ToUserMessage(result); UIMotion.Shake(card); }
                if (submitButton != null) submitButton.interactable = true;
                return;
            }
            ApplySession(result.Data);
            if (rememberLogin) RememberedSession.Save(result.Data); else RememberedSession.Clear();
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
            // 每玩家设置偏好（键位/音量/灵敏度）跟随账号：登录成功即拉取服务器值覆盖本地
            AppRoot.Instance?.PullUserSettingsFromServer();
        }

        private void EnterAuthenticatedLobby()
        {
            SetNavigationVisible(true);
            Navigate(LobbyPage.Lobby);
        }

        /// <summary>联机入口（Docs/27 v1.2 CF）：建房/加入都先进等待房间（Waiting 不连 DS）；
        /// 只有 start ack / InMatch 重连的合法 connection 才写 NetworkLaunchContext 进入 Arena。</summary>
        private void StartOnlineHost(CreateRoomRequest request) => _ = StartOnlineCreateAsync(request);

        private void StartOnlineJoin() => Navigate(LobbyPage.OnlineJoin);

        /// <summary>建房：成功后进入等待房间页（快照无 connection）。失败清上下文留在大厅。</summary>
        private async Task StartOnlineCreateAsync(CreateRoomRequest request)
        {
            if (!await BeginRoomRequestGuardAsync("create")) return;
            var token = pageCts.Token;
            try
            {
                if (!await RefreshMapCatalogAsync() || token.IsCancellationRequested)
                { if (!token.IsCancellationRequested) status.text = "地图目录获取失败，请检查连接后重试"; return; }
                if (request != null && HotMapCatalog.TryGetSceneName(request.mapId, out var requestedScene)
                    && HotSceneLoader.IsBundleScene(requestedScene)
                    && (!HotMapCatalog.TryGet(request.mapId, out var publishedMap)
                        || string.IsNullOrWhiteSpace(publishedMap.contentHash)))
                { status.text = "这张热更地图尚未发布与服务器匹配的版本，请等待地图更新"; return; }
                if (request != null && HotMapCatalog.TryGet(request.mapId, out var selectedMap)
                    && (selectedMap.availability == "preparing" || !string.IsNullOrEmpty(selectedMap.contentHash) && !HotSceneLoader.IsBundleReady(selectedMap.sceneName)))
                { status.text = "地图尚未准备好，请等待服务器就绪或完成地图下载"; return; }
                // P0-A：申报本端应用协议代际——后端冻结在房间上，实例租用按协议筛选（旧 DS 不可见）
                if (request != null) request.clientProtocolId = Game.Gameplay.Network.GameProtocolIdentity.ProtocolId;
                status.text = "正在向服务器申请建房…";
                var result = await api.CreateRoomAsync(request, token);
                await HandleRoomEntryResponseAsync(result, token);
            }
            finally { roomEntryPending = false; }
        }

        /// <summary>按房间码加入：Waiting → 等待房间页；Starting/InMatch（重连/补人）→ 直接进战场。
        /// 失败一律清上下文并留在大厅。</summary>
        private async Task StartOnlineRoomAsync(string roomCode, bool byCode = false)
        {
            if (!await BeginRoomRequestGuardAsync("join")) return;
            status.text = "正在申请加入房间…";
            var token = pageCts.Token;
            var result = byCode
                ? await api.JoinRoomByCodeAsync(roomCode, Game.Gameplay.Network.GameProtocolIdentity.ProtocolId, token)
                : await api.JoinRoomAsync(roomCode, null, Game.Gameplay.Network.GameProtocolIdentity.ProtocolId, token);
            await HandleRoomEntryResponseAsync(result, token);
        }

        /// <summary>入房请求的前置校验；失败返回 false（状态栏已提示）。</summary>
        private async Task<bool> BeginRoomRequestGuardAsync(string action)
        {
            if (MapContentUpdater.Busy || roomEntryPending) { status.text = "正在更新地图或进入房间，请稍候"; return false; }
            if (!apiAvailable) { status.text = "后端服务未就绪，请先完成连接检查"; return false; }
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return false; }
            await Task.CompletedTask;
            roomEntryPending = true;
            return true;
        }

        /// <summary>创建/加入响应统一处置（Docs/27 §5.2/§5.3）：快照入会话 →
        /// 有 connection（Starting/InMatch 重连或补人）走战斗链路；否则进入等待房间页。</summary>
        private async Task HandleRoomEntryResponseAsync(ApiResult<RoomSnapshotDto> result, CancellationToken token)
        {
            roomEntryPending = false;
            if (token.IsCancellationRequested) return;
            if (!result.Success || result.Data?.room == null)
            {
                NetworkLaunchContext.Clear();
                session.ClearRoom(); // 对称清理（§6 三.1）：失败路径不得残留上一局的房间快照
                // P0 开发诊断（2026-09-08 审计 §4）：NO_SERVER_AVAILABLE 五类归因见后端结构化日志
                Debug.LogWarning($"[Lobby] 房间申请失败 code={result.Code ?? "UNKNOWN"}——归因明细查后端日志或 /api/server-instances/pool");
                if (result.Code == "AUTH_UNAUTHORIZED") Navigate(LobbyPage.SessionExpired);
                else if (ApiClientErrorCodes.IsTransportFailure(result.Code))
                {
                    apiAvailable = false;
                    SetNavigationInteractable(false);
                    RenderError(ApiErrorMessages.ToUserMessage(result), () => Navigate(LobbyPage.Boot));
                }
                else status.text = ApiErrorMessages.ToUserMessage(result);
                return;
            }

            var snapshot = result.Data;
            session.ApplyRoomSnapshot(snapshot);
            if (snapshot.connection != null)
            {
                // 重连/补人路径：response 自带可连接票据 → 战斗链路（Waiting 房间绝无 connection）
                if (!await EnterBattleAsync(snapshot.connection, snapshot.room.roomId.ToString()))
                    await CleanupRoomJoinFailureAsync(snapshot);
                return;
            }
            status.text = "已进入房间 " + snapshot.room.roomId.ToString();
            Navigate(LobbyPage.WaitingRoom);
        }

        /// <summary>战斗链路（Docs/27 §11）：配装校验 → RoomConnectionGate 严格校验 →
        /// 写 NetworkLaunchContext（附目标场景名/matchId；代际在 ConfigureClient 内递增）→
        /// 推进 ConnectionGeneration（唯一递增点）→ 加载战斗场景。
        /// Phase 8：目标场景 = 房间 mapId 经 GameMapCatalog 镜像解析（与 DS 同表）；未知地图拒绝入场
        /// （一致性门：绝不加载与房间不符的场景）。场景就绪后的连接/认证/Owner 入场由
        /// ClientMatchSessionCoordinator 按代际编排（P0-B）；失败（含分段超时）由协调器经认证失败
        /// 覆盖层提示并自动返回等待房间页，本方法不重复轮询。
        /// 任何失败路径都不写上下文、不推进代际、不加载场景。</summary>
        private async Task<bool> EnterBattleAsync(RoomConnectionInfoDto connection, string roomCode)
        {
            if (!await ValidateLoadoutForArenaAsync())
            {
                RoomConnectionGate.Sanitize(connection);
                return false;
            }
            // Phase 8 地图一致性门：房间 mapId → 场景名单向解析；未知地图 fail closed 回大厅
            // P4 热更试点：解析改数据驱动（/api/maps 缓存优先，静态镜像兜底）；热更地图须 bundle 就绪
            var roomMapId = session.Room?.MapId;
            if (string.IsNullOrWhiteSpace(roomMapId)) roomMapId = "arena";
            if (!HotMapCatalog.TryGetSceneName(roomMapId, out string sceneName))
            {
                RoomConnectionGate.Sanitize(connection);
                status.text = $"无法进入战场：房间地图未知（{roomMapId}），请更新客户端";
                return false;
            }
            if (HotSceneLoader.IsBundleScene(sceneName)
                && (!HotMapCatalog.TryGet(roomMapId, out var publishedMap)
                    || string.IsNullOrWhiteSpace(publishedMap.contentHash)))
            {
                RoomConnectionGate.Sanitize(connection);
                status.text = "无法进入战场：服务器尚未发布这张热更地图的内容校验值";
                return false;
            }
            if (HotSceneLoader.IsBundleScene(sceneName) && !HotSceneLoader.IsBundleReady(sceneName))
            {
                RoomConnectionGate.Sanitize(connection);
                status.text = $"无法进入战场：地图（{roomMapId}）需要热更资源，请重启客户端下载后重试";
                return false;
            }
            if (!RoomConnectionGate.TryValidate(connection, DateTime.UtcNow, out var validationError))
            {
                RoomConnectionGate.Sanitize(connection);
                status.text = "无法进入战场：" + validationError;
                return false;
            }
            MapContentIdentity.ClientHash = "";
            if (HotMapCatalog.TryGet(roomMapId, out var mapContent) && !string.IsNullOrEmpty(mapContent.contentHash))
            {
                var file = MapContentUpdater.FindBundle(sceneName);
                if (file == null || !System.IO.File.Exists(file)) { status.text = "地图待下载，请返回大厅更新"; return false; }
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = System.IO.File.OpenRead(file);
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!MapContentIdentity.Matches(mapContent.contentHash, hash)) { status.text = "地图内容不一致，请返回大厅更新"; return false; }
                MapContentIdentity.ClientHash = hash;
            }
            RoomConnectionGate.WriteLaunchContext(connection, sceneName);
            session.AdvanceConnectionGeneration();
            status.text = "已取得比赛票据，正在进入战场…";
            // F17（2026-09-19 审计）：加载失败 = 未入场，必须返回 false——旧实现 LoadArenaAsync
            // 清了本地房间后正常 return，本方法仍返回 true，等待房轮询当作入场成功收口，
            // 后端成员/票据不走对称清理，用户丢失返房上下文只能等控制面超时。
            // 失败路径的清理归调用方：等待房轮询 false 分支清上下文并保房间重新取票；
            // 入房/重连路径走 CleanupRoomJoinFailureAsync（含后端 Leave）。
            var loaded = await LoadArenaAsync(sceneName);
            if (!loaded)
            {
                RoomConnectionGate.Sanitize(connection);
                return false;
            }
            return true;
        }

        /// <summary>入房失败但可能已创建后端成员资格时释放。仅对响应带有效 roomCode 的快照调用 Leave。
        /// 对称清理（§6 三.1）：任何失败路径同时清 Session.Room 与 NetworkLaunchContext。</summary>
        private async Task CleanupRoomJoinFailureAsync(RoomSnapshotDto snapshot)
        {
            NetworkLaunchContext.Clear();
            session.ClearRoom();
            try
            {
                if (snapshot?.room != null && !string.IsNullOrWhiteSpace(snapshot.room.roomId.ToString()))
                    await api.LeaveRoomAsync(CancellationToken.None);
            }
            catch
            {
                // 离房是失败清理的 best-effort；主流程仍留在 Lobby，且不泄露 ticket。
            }
            finally
            {
                RoomConnectionGate.Sanitize(snapshot?.connection);
            }
        }

        /// <summary>进入 Arena 前的服务器配装校验（建房/加入/本地开始共用）。</summary>
        private async Task<bool> ValidateLoadoutForArenaAsync()
        {
            status.text = "正在验证服务器配装…";
            var result = await api.GetLoadoutAsync(pageCts.Token);
            if (!result.Success)
            {
                if (result.Code == "AUTH_UNAUTHORIZED") Navigate(LobbyPage.SessionExpired);
                else status.text = "无法进入 Arena：" + ApiErrorMessages.ToUserMessage(result);
                return false;
            }
            if (result.Data == null ||
                !IsLpfpLoadoutItem(result.Data.primaryWeaponId) ||
                !IsLpfpLoadoutItem(result.Data.secondaryWeaponId))
            {
                status.text = "无法进入 Arena：主线仅支持已映射的 LPFP 武器";
                return false;
            }
            session.ApplyLoadout(result.Data);
            return true;
        }

        private async Task<bool> LoadArenaAsync(string sceneName = null)
        {
            var target = string.IsNullOrWhiteSpace(sceneName) ? gameplaySceneName : sceneName;
            Navigate(LobbyPage.Loading);
            MatchLoadingScreen.Begin(true);
            // P4 热更试点：非内置场景走 bundle 通道（文件由热更下载器预先落盘）
            if (HotSceneLoader.IsBundleScene(target))
            {
                if (!await HotSceneLoader.TryLoadBundleSceneAsync(target,
                        p => { MatchLoadingScreen.ReportSceneProgress(p); if (loadingFill != null) loadingFill.fillAmount = p; }))
                {
                    // 罕见失败路径（文件损坏等）：返回显式失败——旧实现在此清房回大厅并把
                    // 成功返回给 EnterBattleAsync（F17 反例）
                    status.text = "热更地图加载失败，将在等待房间自动重试";
                    MatchLoadingScreen.Dismiss();
                    return false;
                }
                MatchLoadingScreen.SceneReady();
                return true;
            }
            await MatchLoadingScreen.LoadAsync(target, true);
            return true;
        }

        /// <summary>Refresh independently of content downloads; never rebuild the browser under active input.</summary>
        private Task<bool> mapCatalogRefreshTask;
        private Task<bool> RefreshMapCatalogAsync()
        {
            if (mapCatalogRefreshTask == null || mapCatalogRefreshTask.IsCompleted)
                mapCatalogRefreshTask = FetchMapCatalogAsync();
            return mapCatalogRefreshTask;
        }

        private async Task<bool> FetchMapCatalogAsync()
        {
            if (api == null || session?.IsAuthenticated != true) return false;
            var accountToken = session.Token;
            try
            {
                var result = await api.ListMapsAsync(CancellationToken.None);
                if (this != null && session.IsAuthenticated && session.Token == accountToken
                    && result.Success && result.Data != null && result.Data.Length > 0)
                {
                    HotMapCatalog.Store(result.Data);
                    return true;
                }
            }
            catch
            {
                // Retain the last catalog; creation reports failure instead of claiming a version mismatch.
            }
            return false;
        }

        private bool IsLpfpLoadoutItem(string itemId)
        {
            return weaponAssets != null
                && weaponAssets.TryGet(itemId, out var entry)
                && entry != null
                && entry.IsLpfp
                && weaponAssets.TryResolveDefinition(itemId, out var definition)
                && definition != null;
        }

        private async Task PurchaseAsync(CatalogItemDto item)
        {
            if (item == null || !IsLpfpWeaponItem(item))
            {
                status.text = "主线仅支持 LPFP 武器，无法购买该条目";
                return;
            }
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

        private bool equipBusy;
        private async Task EquipWeaponAsync(CatalogItemDto item)
        {
            if (equipBusy || api == null || pageCts == null) return;
            if (item == null || !IsLpfpWeaponItem(item))
            {
                status.text = "主线仅支持 LPFP 武器，无法装备该条目";
                return;
            }
            if (!item.isOwned || session.Loadout == null) { status.text = "未拥有或配装尚未加载"; return; }
            var request = new LoadoutRequest
            {
                primaryWeaponId = item.slotType == "Primary" ? item.itemId : session.Loadout.primaryWeaponId,
                secondaryWeaponId = item.slotType == "Secondary" ? item.itemId : session.Loadout.secondaryWeaponId,
                throwableId = null,
                expectedVersion = session.Loadout.version
            };
            status.text = "正在保存服务器配装…";
            var token = pageCts.Token;
            var owner = session;
            equipBusy = true;
            try
            {
                var result = await api.UpdateLoadoutAsync(request, token);
                if (token.IsCancellationRequested || session != owner) return;
                if (!result.Success) { status.text = ApiErrorMessages.ToUserMessage(result); return; }
                session.ApplyLoadout(result.Data);
                equipBusy = false;
                if (currentPage == LobbyPage.Armory || currentPage == LobbyPage.Shop)
                    RenderTacticalCatalog(currentPage == LobbyPage.Shop);
                status.text = item.slotType == "Secondary" ? "已装备为副武器" : "已装备为主武器";
            }
            catch (OperationCanceledException) { }
            finally { equipBusy = false; }
        }

        private async Task<bool> SaveAttachmentsAsync(long version, List<AttachmentSelectionRequest> selections)
        {
            var result = await api.UpdateLoadoutAttachmentsAsync(new LoadoutAttachmentsRequest
            {
                expectedVersion = version, weaponSlot = selectedWeapon.slotType == "Secondary" ? "Secondary" : "Primary",
                weaponItemId = selectedWeapon.itemId, attachments = selections.ToArray()
            }, pageCts.Token);
            if (!result.Success)
            {
                status.text = ApiErrorMessages.ToUserMessage(result);
                return false;
            }

            var slotMap = new Dictionary<string, string>();
            foreach (var slot in WeaponAttachmentStore.AllSlots)
                slotMap[slot] = selections.FirstOrDefault(x => x.attachmentSlot == slot)?.attachmentItemId ?? string.Empty;
            WeaponAttachmentStore.Save(selectedWeapon.itemId, slotMap);
            // 配件更新与整套配装共用同一个乐观锁版本；响应必须立即合并回会话，
            // 否则随后 EquipWeaponAsync 会继续提交旧 expectedVersion 并稳定触发冲突。
            session.ApplyLoadoutAttachments(result.Data);
            gunsmithLoadoutVersion = result.Data.version;
            status.text = "配件已保存并装备当前武器，版本 " + result.Data.version;
            return true;
        }

        private void Logout()
        {
            RememberedSession.Clear();
            cachedFriends = null;
            socialExpanded = false;
            RefreshSocialViews();
            api.ClearToken();
            session.Clear();
            Chat.ChatController.StopAndClear(); // 审计 §5.3-3：登出即房间会话退出（旧实现漏清聊天）
            SetNavigationVisible(false);
            Navigate(LobbyPage.Login);
        }
    }
}
