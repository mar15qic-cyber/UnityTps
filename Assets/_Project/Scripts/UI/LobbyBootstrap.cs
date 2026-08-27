using System;
using System.Collections;
using System.Collections.Generic;
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

/// <summary>Day6 单场景大厅：四个页面只编排 AccountSession，不直接持有 HTTP 细节。</summary>
public sealed class LobbyBootstrap : MonoBehaviour
{
    private static readonly Color Background = new(0.055f, 0.075f, 0.16f, 1f);
    private static readonly Color Panel = new(0.10f, 0.13f, 0.25f, 0.97f);
    private static readonly Color PanelAlt = new(0.14f, 0.18f, 0.32f, 0.98f);
    private static readonly Color Teal = new(0.10f, 0.80f, 0.68f, 1f);
    private static readonly Color Yellow = new(1.00f, 0.76f, 0.23f, 1f);
    private static readonly Color Coral = new(1.00f, 0.34f, 0.38f, 1f);
    private static readonly Color Text = new(0.94f, 0.96f, 1f, 1f);
    private static readonly Color Muted = new(0.62f, 0.68f, 0.82f, 1f);
    private static TMP_FontAsset uiFont;

    private readonly Dictionary<LobbyFlowState, GameObject> pages = new();
    private Canvas canvas;
    private TMP_Text toast;
    private TMP_Text loginError;
    private TMP_InputField usernameInput;
    private TMP_InputField passwordInput;
    private Button loginSubmit;
    private bool registerMode;
    private TMP_Text mainProfile;
    private TMP_Text mainLoadout;
    private TMP_Text mainXp;
    private Image mainXpFill;
    private TMP_Text mainSkillPoints;
    private readonly Dictionary<string, bool> pendingWeapons = new();
    private readonly Dictionary<string, int> upgradeLevels = new();
    private readonly Dictionary<string, WeaponDefinition> weaponDefinitions = new();
    private TMP_Text loadoutSummary;
    private Button loadoutSubmit;
    private TMP_Text upgradeSummary;
    private TMP_Text upgradeError;
    private Button upgradeSubmit;
    private Coroutine pageTransition;

    private IApiClient Api => AppRoot.Ensure().ApiClient;
    private AccountSession Session => AppRoot.Ensure().Session;

    private void Start()
    {
        AppRoot.Ensure();
        EnsureEventSystem();
        BuildCanvas();
        BuildLoginPage();
        BuildMainPage();
        BuildLoadoutPage();
        BuildUpgradePage();
        ShowPage(LobbyFlowState.Login);
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        DontDestroyOnLoad(eventSystem);
    }

    private void BuildCanvas()
    {
        var root = new GameObject("LobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var background = PanelObject("LowPolyColorBackground", root.transform, Background);
        Stretch(background.GetComponent<RectTransform>());
        var facetA = PanelObject("FacetA", background.transform, new Color(0.10f, 0.22f, 0.42f, 0.42f));
        Anchor(facetA.GetComponent<RectTransform>(), new Vector2(0.58f, 0.08f), new Vector2(1.05f, 0.92f), new Vector2(0, 0), new Vector2(0, 0), 20);
        facetA.transform.localRotation = Quaternion.Euler(0, 0, 18);
        var facetB = PanelObject("FacetB", background.transform, new Color(0.08f, 0.55f, 0.52f, 0.20f));
        Anchor(facetB.GetComponent<RectTransform>(), new Vector2(-0.12f, -0.15f), new Vector2(0.42f, 0.25f), new Vector2(0, 0), new Vector2(0, 0), 20);
        facetB.transform.localRotation = Quaternion.Euler(0, 0, -24);

        toast = TextObject("Toast", root.transform, string.Empty, 24, Yellow, TextAnchor.MiddleCenter);
        Anchor(toast.rectTransform, new Vector2(0.30f, 0.04f), new Vector2(0.70f, 0.11f), Vector2.zero, Vector2.zero, 30);
        toast.gameObject.SetActive(false);
    }

    private void BuildLoginPage()
    {
        var page = Page("Login");
        var card = Card("LoginCard", page.transform, new Vector2(0.36f, 0.15f), new Vector2(0.64f, 0.86f));
        TextObject("Title", card.transform, "佣兵档案中心", 42, Text, TextAnchor.MiddleCenter, new Vector2(0.08f, 0.82f), new Vector2(0.92f, 0.94f));
        TextObject("Subtitle", card.transform, "LOW POLY OPERATIONS // DAY 5–6", 15, Teal, TextAnchor.MiddleCenter, new Vector2(0.08f, 0.75f), new Vector2(0.92f, 0.82f));
        usernameInput = InputObject("Username", card.transform, "用户名", new Vector2(0.15f, 0.58f), new Vector2(0.85f, 0.69f));
        passwordInput = InputObject("Password", card.transform, "密码", new Vector2(0.15f, 0.42f), new Vector2(0.85f, 0.53f));
        passwordInput.contentType = TMP_InputField.ContentType.Password;
        loginError = TextObject("Error", card.transform, string.Empty, 16, Coral, TextAnchor.MiddleCenter, new Vector2(0.12f, 0.31f), new Vector2(0.88f, 0.40f));
        loginSubmit = ButtonObject("Submit", card.transform, "登 录", Teal, new Vector2(0.15f, 0.18f), new Vector2(0.85f, 0.29f));
        loginSubmit.onClick.AddListener(() => _ = SubmitLoginAsync());
        var toggle = ButtonObject("ToggleMode", card.transform, "切换为注册", PanelAlt, new Vector2(0.25f, 0.07f), new Vector2(0.75f, 0.14f));
        toggle.onClick.AddListener(() =>
        {
            registerMode = !registerMode;
            toggle.GetComponentInChildren<TMP_Text>().text = registerMode ? "已有档案？返回登录" : "切换为注册";
            loginSubmit.GetComponentInChildren<TMP_Text>().text = registerMode ? "创 建 档 案" : "登 录";
            loginError.text = string.Empty;
        });
    }

    private void BuildMainPage()
    {
        var page = Page("Main");
        Header(page.transform, "作战大厅", "PROFILE // LOADOUT // UPGRADE");
        var card = Card("ProfileCard", page.transform, new Vector2(0.12f, 0.22f), new Vector2(0.88f, 0.82f));
        mainProfile = TextObject("Profile", card.transform, "", 32, Text, TextAnchor.MiddleLeft, new Vector2(0.08f, 0.70f), new Vector2(0.92f, 0.90f));
        mainXp = TextObject("Xp", card.transform, "", 20, Teal, TextAnchor.MiddleLeft, new Vector2(0.08f, 0.56f), new Vector2(0.92f, 0.68f));
        mainXpFill = ProgressBar("XpBar", card.transform, new Vector2(0.08f, 0.49f), new Vector2(0.92f, 0.54f));
        mainSkillPoints = TextObject("SkillPoints", card.transform, "", 20, Yellow, TextAnchor.MiddleLeft, new Vector2(0.08f, 0.44f), new Vector2(0.92f, 0.54f));
        mainLoadout = TextObject("Loadout", card.transform, "", 18, Muted, TextAnchor.MiddleLeft, new Vector2(0.08f, 0.30f), new Vector2(0.92f, 0.42f));
        var loadout = ButtonObject("Loadout", card.transform, "配 装", Teal, new Vector2(0.08f, 0.10f), new Vector2(0.28f, 0.22f));
        loadout.onClick.AddListener(() => ShowPage(LobbyFlowState.Loadout));
        var upgrade = ButtonObject("Upgrade", card.transform, "升 级", Yellow, new Vector2(0.31f, 0.10f), new Vector2(0.51f, 0.22f));
        upgrade.onClick.AddListener(() => ShowPage(LobbyFlowState.Upgrade));
        var play = ButtonObject("Play", card.transform, "开始对战（Day7）", PanelAlt, new Vector2(0.54f, 0.10f), new Vector2(0.78f, 0.22f));
        play.interactable = false;
        var logout = ButtonObject("Logout", card.transform, "退出", Coral, new Vector2(0.80f, 0.10f), new Vector2(0.92f, 0.22f));
        logout.onClick.AddListener(() => { Api.ClearToken(); Session.Clear(); ShowPage(LobbyFlowState.Login); });
    }

    private void BuildLoadoutPage()
    {
        var page = Page("Loadout");
        Header(page.transform, "战术配装", "选择你的主武器与副武器");
        var card = Card("LoadoutCard", page.transform, new Vector2(0.10f, 0.15f), new Vector2(0.90f, 0.82f));
        TextObject("PrimaryLabel", card.transform, "主武器", 22, Yellow, TextAnchor.MiddleLeft, new Vector2(0.06f, 0.80f), new Vector2(0.94f, 0.88f));
        var primary = new[] { ("rifle.day3", "突击步枪"), ("rifle.02", "Rifle 02"), ("rifle.03", "Rifle 03"), ("smg.01", "SMG 01"), ("smg.02", "SMG 02"), ("shotgun.01", "霰弹枪"), ("sniper.01", "狙击枪"), ("sniper.02", "Sniper 02") };
        BuildWeaponRow(card.transform, primary, true, 0.58f);
        TextObject("SecondaryLabel", card.transform, "副武器", 22, Teal, TextAnchor.MiddleLeft, new Vector2(0.06f, 0.42f), new Vector2(0.94f, 0.50f));
        BuildWeaponRow(card.transform, new[] { ("pistol.day2", "服务手枪"), ("handgun.02", "Handgun 02") }, false, 0.20f);
        loadoutSummary = TextObject("Summary", card.transform, "", 16, Muted, TextAnchor.MiddleLeft, new Vector2(0.06f, 0.03f), new Vector2(0.60f, 0.14f));
        loadoutSubmit = ButtonObject("Save", card.transform, "保存配装", Teal, new Vector2(0.65f, 0.04f), new Vector2(0.84f, 0.15f));
        loadoutSubmit.onClick.AddListener(() => _ = SaveLoadoutAsync());
        var back = ButtonObject("Back", card.transform, "返回", PanelAlt, new Vector2(0.85f, 0.04f), new Vector2(0.96f, 0.15f));
        back.onClick.AddListener(() => ShowPage(LobbyFlowState.Main));
    }

    private void BuildUpgradePage()
    {
        var page = Page("Upgrade");
        Header(page.transform, "能力升级", "技能点由比赛结算获得");
        var card = Card("UpgradeCard", page.transform, new Vector2(0.10f, 0.15f), new Vector2(0.90f, 0.82f));
        upgradeSummary = TextObject("Points", card.transform, "", 22, Yellow, TextAnchor.MiddleLeft, new Vector2(0.06f, 0.82f), new Vector2(0.94f, 0.90f));
        BuildUpgradeRow(card.transform, "伤害强化", "upDamage", 0.58f);
        BuildUpgradeRow(card.transform, "弹药容量", "upAmmoCap", 0.38f);
        BuildUpgradeRow(card.transform, "最大生命", "upMaxHealth", 0.18f);
        upgradeError = TextObject("Error", card.transform, "", 16, Coral, TextAnchor.MiddleLeft, new Vector2(0.06f, 0.02f), new Vector2(0.55f, 0.12f));
        upgradeSubmit = ButtonObject("Submit", card.transform, "确认升级", Yellow, new Vector2(0.60f, 0.04f), new Vector2(0.78f, 0.15f));
        upgradeSubmit.onClick.AddListener(() => _ = SaveUpgradesAsync());
        var back = ButtonObject("Back", card.transform, "返回", PanelAlt, new Vector2(0.80f, 0.04f), new Vector2(0.96f, 0.15f));
        back.onClick.AddListener(() => ShowPage(LobbyFlowState.Main));
    }

    private void BuildWeaponRow(Transform parent, (string id, string label)[] weapons, bool primary, float y)
    {
        var width = 0.88f / weapons.Length;
        for (var i = 0; i < weapons.Length; i++)
        {
            var weapon = weapons[i];
            var definition = ResolveWeaponDefinition(weapon.id);
            var displayLabel = definition == null ? weapon.label : definition.DisplayName;
            var button = ButtonObject(weapon.id, parent, displayLabel, WeaponAccent(weapon.id), new Vector2(0.06f + width * i, y), new Vector2(0.06f + width * (i + 1) - 0.01f, y + 0.13f));
            TextObject("Id", button.transform, weapon.id, 10, Muted, TextAnchor.LowerCenter, new Vector2(0.03f, 0.03f), new Vector2(0.97f, 0.25f));
            button.onClick.AddListener(() =>
            {
                if (primary)
                {
                    foreach (var id in new[] { "rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "shotgun.01", "sniper.01", "sniper.02" }) pendingWeapons[id] = false;
                }
                else
                {
                    foreach (var id in new[] { "pistol.day2", "handgun.02" }) pendingWeapons[id] = false;
                }
                pendingWeapons[weapon.id] = true;
                UpdateLoadoutSummary();
            });
        }
    }

    private WeaponDefinition ResolveWeaponDefinition(string id)
    {
        if (weaponDefinitions.TryGetValue(id, out var cached)) return cached;
        foreach (var definition in Resources.FindObjectsOfTypeAll<WeaponDefinition>())
        {
            if (definition != null && definition.WeaponId == id)
            {
                weaponDefinitions[id] = definition;
                return definition;
            }
        }
        weaponDefinitions[id] = null;
        return null;
    }

    private static Color WeaponAccent(string id)
    {
        if (id.StartsWith("sniper", StringComparison.OrdinalIgnoreCase)) return new Color(0.28f, 0.44f, 0.72f, 1f);
        if (id.StartsWith("shotgun", StringComparison.OrdinalIgnoreCase)) return new Color(0.66f, 0.39f, 0.24f, 1f);
        if (id.StartsWith("smg", StringComparison.OrdinalIgnoreCase)) return new Color(0.16f, 0.58f, 0.63f, 1f);
        if (id.StartsWith("pistol", StringComparison.OrdinalIgnoreCase) || id.StartsWith("handgun", StringComparison.OrdinalIgnoreCase)) return new Color(0.42f, 0.35f, 0.63f, 1f);
        return PanelAlt;
    }

    private void BuildUpgradeRow(Transform parent, string label, string key, float y)
    {
        upgradeLevels[key] = 0;
        TextObject(key + "Label", parent, label, 20, Text, TextAnchor.MiddleLeft, new Vector2(0.06f, y), new Vector2(0.32f, y + 0.12f));
        var value = TextObject(key + "Value", parent, "0 / 5", 18, Teal, TextAnchor.MiddleCenter, new Vector2(0.43f, y), new Vector2(0.56f, y + 0.12f));
        var plus = ButtonObject(key + "Plus", parent, "+", Yellow, new Vector2(0.62f, y + 0.01f), new Vector2(0.72f, y + 0.11f));
        plus.onClick.AddListener(() => { upgradeLevels[key] = Mathf.Min(5, upgradeLevels[key] + 1); value.text = upgradeLevels[key] + " / 5"; });
        var minus = ButtonObject(key + "Minus", parent, "−", PanelAlt, new Vector2(0.74f, y + 0.01f), new Vector2(0.84f, y + 0.11f));
        minus.onClick.AddListener(() => { upgradeLevels[key] = Mathf.Max(0, upgradeLevels[key] - 1); value.text = upgradeLevels[key] + " / 5"; });
    }

    private async Task SubmitLoginAsync()
    {
        SetLoginBusy(true);
        loginError.text = string.Empty;
        if (string.IsNullOrWhiteSpace(usernameInput.text) || usernameInput.text.Trim().Length < 3 || passwordInput.text.Length < 8)
        {
            loginError.text = "请输入有效的用户名（至少 3 个字符）和密码（至少 8 个字符）";
            SetLoginBusy(false);
            return;
        }
        var result = registerMode ? await Api.RegisterAsync(usernameInput.text, passwordInput.text) : await Api.LoginAsync(usernameInput.text, passwordInput.text);
        if (!result.Success)
        {
            loginError.text = result.Message;
            SetLoginBusy(false);
            return;
        }
        Api.SetToken(result.Data.token);
        Session.Apply(result.Data);
        SetLoginBusy(false);
        ShowPage(LobbyFlowState.Main);
        ShowToast(registerMode ? "档案创建成功" : "登录成功", Teal);
    }

    private async Task SaveLoadoutAsync()
    {
        loadoutSubmit.interactable = false;
        loadoutSubmit.GetComponentInChildren<TMP_Text>().text = "保存中…";
        var primary = FindPending(new[] { "rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "shotgun.01", "sniper.01", "sniper.02" }, Session.Loadout?.primaryWeaponId ?? "rifle.day3");
        var secondary = FindPending(new[] { "pistol.day2", "handgun.02" }, Session.Loadout?.secondaryWeaponId ?? "pistol.day2");
        var result = await Api.UpdateLoadoutAsync(new LoadoutRequest { primaryWeaponId = primary, secondaryWeaponId = secondary, throwableId = null });
        loadoutSubmit.interactable = true;
        loadoutSubmit.GetComponentInChildren<TMP_Text>().text = "保存配装";
        if (!result.Success) { ShowToast(result.Message, Coral); return; }
        Session.ApplyLoadout(result.Data);
        UpdateMainPage();
        ShowToast("配装已保存", Teal);
    }

    private async Task SaveUpgradesAsync()
    {
        upgradeError.text = string.Empty;
        upgradeSubmit.interactable = false;
        var result = await Api.UpdateUpgradesAsync(new UpgradeRequest { upDamage = upgradeLevels["upDamage"], upAmmoCap = upgradeLevels["upAmmoCap"], upMaxHealth = upgradeLevels["upMaxHealth"] });
        upgradeSubmit.interactable = true;
        if (!result.Success) { upgradeError.text = result.Message; return; }
        Session.ApplyProfile(result.Data);
        UpdateMainPage();
        ShowToast("升级已保存", Teal);
    }

    private string FindPending(string[] ids, string fallback)
    {
        foreach (var id in ids) if (pendingWeapons.TryGetValue(id, out var selected) && selected) return id;
        return fallback;
    }

    private void ShowPage(LobbyFlowState state)
    {
        foreach (var entry in pages) entry.Value.SetActive(entry.Key == state);
        var activePage = pages[state];
        if (pageTransition != null) StopCoroutine(pageTransition);
        pageTransition = StartCoroutine(TransitionPage(activePage));
        if (state == LobbyFlowState.Main) UpdateMainPage();
        if (state == LobbyFlowState.Loadout) { InitializePendingWeapons(); UpdateLoadoutSummary(); }
        if (state == LobbyFlowState.Upgrade) InitializeUpgradeLevels();
    }

    private void InitializePendingWeapons()
    {
        foreach (var id in new[] { "rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "shotgun.01", "sniper.01", "sniper.02", "pistol.day2", "handgun.02" }) pendingWeapons[id] = false;
        if (Session.Loadout == null) { pendingWeapons["rifle.day3"] = true; pendingWeapons["pistol.day2"] = true; return; }
        pendingWeapons[Session.Loadout.primaryWeaponId] = true;
        pendingWeapons[Session.Loadout.secondaryWeaponId] = true;
    }

    private void InitializeUpgradeLevels()
    {
        if (Session.Profile?.upgrades == null) return;
        upgradeLevels["upDamage"] = Session.Profile.upgrades.upDamage;
        upgradeLevels["upAmmoCap"] = Session.Profile.upgrades.upAmmoCap;
        upgradeLevels["upMaxHealth"] = Session.Profile.upgrades.upMaxHealth;
        upgradeSummary.text = $"可用技能点：{Session.Profile.skillPoints}  // 目标等级按服务端校验";
    }

    private void UpdateMainPage()
    {
        if (Session.Profile == null) return;
        mainProfile.text = $"{Session.Profile.username}\n等级 {Session.Profile.level}";
        mainXp.text = $"XP  {Session.Profile.xp} / {Session.Profile.xpToNextLevel}";
        if (mainXpFill != null)
            mainXpFill.fillAmount = Session.Profile.xpToNextLevel <= 0 ? 0f : Mathf.Clamp01((float)Session.Profile.xp / Session.Profile.xpToNextLevel);
        mainSkillPoints.text = $"技能点  {Session.Profile.skillPoints}";
        mainLoadout.text = $"当前配装  {Session.Loadout?.primaryWeaponId ?? "-"}  /  {Session.Loadout?.secondaryWeaponId ?? "-"}";
    }

    private void UpdateLoadoutSummary() => loadoutSummary.text = $"主武器：{FindPending(new[] { "rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "shotgun.01", "sniper.01", "sniper.02" }, "rifle.day3")}   副武器：{FindPending(new[] { "pistol.day2", "handgun.02" }, "pistol.day2")}";

    private void SetLoginBusy(bool busy) { loginSubmit.interactable = !busy; loginSubmit.GetComponentInChildren<TMP_Text>().text = busy ? "请稍候…" : (registerMode ? "创 建 档 案" : "登 录"); }

    private void ShowToast(string message, Color color)
    {
        toast.text = message;
        toast.color = color;
        toast.gameObject.SetActive(true);
        StopCoroutine(nameof(HideToast));
        StartCoroutine(nameof(HideToast));
    }

    private IEnumerator HideToast() { yield return new WaitForSecondsRealtime(2.2f); toast.gameObject.SetActive(false); }

    private GameObject Page(string name)
    {
        var page = PanelObject(name, canvas.transform, Color.clear);
        page.AddComponent<CanvasGroup>();
        Stretch(page.GetComponent<RectTransform>());
        pages[Enum.Parse<LobbyFlowState>(name)] = page;
        return page;
    }

    private IEnumerator TransitionPage(GameObject page)
    {
        var group = page.GetComponent<CanvasGroup>();
        var rect = page.GetComponent<RectTransform>();
        var target = rect.anchoredPosition;
        var start = target + Vector2.up * 18f;
        var elapsed = 0f;
        while (elapsed < 0.15f)
        {
            elapsed += Time.unscaledDeltaTime;
            var t = Mathf.Clamp01(elapsed / 0.15f);
            var eased = 1f - Mathf.Pow(1f - t, 3f);
            group.alpha = eased;
            rect.anchoredPosition = Vector2.Lerp(start, target, eased);
            yield return null;
        }
        group.alpha = 1f;
        rect.anchoredPosition = target;
    }

    private static void Header(Transform parent, string title, string subtitle)
    {
        TextObject("Header", parent, title, 38, Text, TextAnchor.MiddleLeft, new Vector2(0.10f, 0.86f), new Vector2(0.90f, 0.96f));
        TextObject("HeaderSubtitle", parent, subtitle, 15, Teal, TextAnchor.MiddleLeft, new Vector2(0.10f, 0.82f), new Vector2(0.90f, 0.87f));
    }

    private static GameObject Card(string name, Transform parent, Vector2 min, Vector2 max)
    {
        var card = PanelObject(name, parent, Panel);
        Anchor(card.GetComponent<RectTransform>(), min, max, Vector2.zero, Vector2.zero, 10);
        return card;
    }

    private static GameObject PanelObject(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    private static TMP_Text TextObject(string name, Transform parent, string value, float size, Color color, TextAnchor alignment, Vector2 min = default, Vector2 max = default)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = size;
        text.color = color;
        var font = GetUiFont();
        if (font != null) text.font = font;
        text.alignment = ToTmpAlignment(alignment);
        text.enableWordWrapping = true;
        if (max != default) Anchor(text.rectTransform, min, max, Vector2.zero, Vector2.zero, 1);
        return text;
    }

    private static TextAlignmentOptions ToTmpAlignment(TextAnchor anchor)
    {
        switch (anchor)
        {
            case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
            case TextAnchor.MiddleRight: return TextAlignmentOptions.MidlineRight;
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return TextAlignmentOptions.MidlineLeft;
        }
    }

    private static TMP_FontAsset GetUiFont()
    {
        if (uiFont != null) return uiFont;
        try
        {
            uiFont = TMP_FontAsset.CreateFontAsset("Noto Sans SC", "Regular", 90);
        }
        catch (Exception)
        {
            uiFont = null;
        }
        return uiFont;
    }

    private static TMP_InputField InputObject(string name, Transform parent, string placeholder, Vector2 min, Vector2 max)
    {
        var go = PanelObject(name, parent, PanelAlt);
        Anchor(go.GetComponent<RectTransform>(), min, max, Vector2.zero, Vector2.zero, 10);
        var input = go.AddComponent<TMP_InputField>();
        var text = TextObject("Text", go.transform, string.Empty, 20, Text, TextAnchor.MiddleLeft, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f));
        input.textComponent = (TMP_Text)text;
        var hint = TextObject("Placeholder", go.transform, placeholder, 18, Muted, TextAnchor.MiddleLeft, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f));
        input.placeholder = hint;
        return input;
    }

    private static Button ButtonObject(string name, Transform parent, string label, Color color, Vector2 min, Vector2 max)
    {
        var go = PanelObject(name, parent, color);
        Anchor(go.GetComponent<RectTransform>(), min, max, Vector2.zero, Vector2.zero, 10);
        var button = go.AddComponent<Button>();
        var colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.18f);
        colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
        colors.disabledColor = new Color(color.r, color.g, color.b, 0.35f);
        button.colors = colors;
        TextObject("Label", go.transform, label, 17, Text, TextAnchor.MiddleCenter, new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.96f));
        return button;
    }

    private static Image ProgressBar(string name, Transform parent, Vector2 min, Vector2 max)
    {
        var background = PanelObject(name, parent, new Color(0.03f, 0.05f, 0.11f, 0.9f));
        Anchor(background.GetComponent<RectTransform>(), min, max, Vector2.zero, Vector2.zero, 10);
        var fillObject = PanelObject("Fill", background.transform, Teal);
        var fill = fillObject.GetComponent<Image>();
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        Stretch(fill.rectTransform);
        return fill;
    }

    private static void Stretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero; }
    private static void Anchor(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax, int _) { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax; }
}

public enum LobbyFlowState { Login, Main, Loadout, Upgrade }
}
