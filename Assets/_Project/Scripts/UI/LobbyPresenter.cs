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
        private TMP_Text status;
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
                case LobbyPage.Loading: RenderLoading(); break;
            }
            UpdateNavSelection();
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

        // ---- Docs/20 design-system helpers (new pages use these) ----

        private Image backgroundImage;

        private void SetBackground(string artKey)
        {
            if (backgroundImage != null) backgroundImage.sprite = UIArt.Get(artKey);
        }

        private GameObject StyledPanel(string name, Transform parent, Color fill, Vector2 min, Vector2 max)
        {
            var go = UIComponents.Panel(name, parent, fill, min, max);
            bodyObjects.Add(go);
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
            SetNavigationInteractable(value);
        }

        private static bool IsProtectedPage(LobbyPage page)
        {
            return page == LobbyPage.Lobby || page == LobbyPage.Mission || page == LobbyPage.Armory ||
                   page == LobbyPage.WeaponDetails || page == LobbyPage.Shop || page == LobbyPage.Upgrades ||
                   page == LobbyPage.Settings || page == LobbyPage.Hud || page == LobbyPage.Pause ||
                   page == LobbyPage.Results || page == LobbyPage.Loading;
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
            Navigate(LobbyPage.Loading);
            var loadOp = SceneManager.LoadSceneAsync(gameplaySceneName, LoadSceneMode.Single);
            while (loadOp != null && !loadOp.isDone)
            {
                if (loadingFill != null) loadingFill.fillAmount = Mathf.Clamp01(loadOp.progress / 0.9f);
                await Task.Yield();
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

        private void Logout()
        {
            api.ClearToken();
            session.Clear();
            SetNavigationVisible(false);
            Navigate(LobbyPage.Login);
        }
    }
}
