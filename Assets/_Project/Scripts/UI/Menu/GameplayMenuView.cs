using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using Game.Gameplay.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Game.UI.Menu
{
    /// <summary>
    /// Arena 游戏菜单视图（Phase A）：uGUI 纯代码构建，运行时由 GameplayMenuController
    /// 经反射 TryMount 挂入（Gameplay 不引用 Game.UI，反射按名为本项目既有惯例）。
    /// 视觉参考《无畏契约/VALORANT》的信息层级与布局语言：暗色半透明底、红色强调、
    /// 左侧导航大标题与当前选中态——只借鉴布局语言，不复制其 Logo/图标/版权素材。
    /// 交互契约：所有点击只调控制器（状态唯一真相）；菜单打开时世界/网络/AI 继续运行
    /// （控制器绝不写 Time.timeScale）。HUD 不拦截按钮射线：本画布 sortingOrder 500 最高层。
    /// </summary>
    public sealed class GameplayMenuView : MonoBehaviour, GameplayMenuController.IGameplayMenuView
    {
        // —— 局部配色（VALORANT 式布局语言；非其官方素材）——
        private static readonly Color VeilDark = new Color(0.04f, 0.06f, 0.10f, 0.90f);
        private static readonly Color AccentRed = new Color(1.00f, 0.27f, 0.33f);   // #FF4655
        private static readonly Color AccentRedDim = new Color(0.55f, 0.15f, 0.19f);
        private static readonly Color RailDark = new Color(0.07f, 0.10f, 0.15f, 0.97f);
        private static readonly Color CardDark = new Color(0.10f, 0.13f, 0.19f, 0.96f);
        private static readonly Color TextBright = new Color(0.96f, 0.97f, 0.98f);
        private static readonly Color TextDim = new Color(0.60f, 0.66f, 0.73f);

        private GameplayMenuController _controller;
        private GameObject _root;
        private GameObject _pauseHome;
        private GameplaySettingsPanel _settingsPanel;
        private GameObject _leaveDialog;
        private GameObject _conflictDialog;

        private TMP_Text _leaveBodyText;
        private TMP_Text _contextText;
        private Button _leaveConfirmButton;
        private TMP_Text _conflictBodyText;
        private readonly (SettingsKeyMap.Action action, Button keyBtn, TMP_Text keyLabel)[] _keyRows = new (SettingsKeyMap.Action, Button, TMP_Text)[SettingsKeyMap.Bindings.Length];

        /// <summary>反射挂载入口（签名不可改）：幂等；controller 由 GameplayMenuController.EnsureMounted 传入。
        /// 顺带保证 EventSystem 存在且为 InputSystemUIInputModule（Gameplay 程序集无 UI 引用，故在此做）。</summary>
        public static void TryMount(GameplayMenuController controller)
        {
            if (controller == null) return;
            EnsureEventSystem();
            // 画布初始即隐藏（SetActive(false)）——幂等闸必须含未激活对象，否则重复挂载会造出第二套
            if (FindFirstObjectByType<GameplayMenuView>(FindObjectsInactive.Include) != null) return;
            var canvasGo = new GameObject("GameplayMenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500; // 压过 HUD 层：菜单开启时按钮射线不落进 HUD
            canvasGo.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920, 1080);
            var view = canvasGo.AddComponent<GameplayMenuView>();
            view._controller = controller; // 回接控制器（视图所有交互都经它；缺失=菜单永久隐形）
            view.Build();
            controller.AttachView(view);
        }

        private static void EnsureEventSystem()
        {
            // 与 LobbyPresenter.EnsureInputSystemEventSystem 同款（照抄惯例）：无则建，有则换新输入模块
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
            var inputSystemModuleType = typeof(InputSystemUIInputModule);
            if (eventSystem.GetComponent(inputSystemModuleType) == null)
                eventSystem.gameObject.AddComponent(inputSystemModuleType);
        }

        private void Build()
        {
            var rootRect = (RectTransform)transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // —— 全屏暗色半透明纱 ——
            var veil = UIMenuKit.Panel("Veil", rootRect, VeilDark, Vector2.zero, Vector2.one);
            veil.GetComponent<Image>().raycastTarget = true; // 吃掉穿透点击

            _root = veil;

            // —— 左侧导航栏 ——
            var rail = UIMenuKit.Panel("NavRail", rootRect, RailDark, new Vector2(0.02f, 0.06f), new Vector2(0.24f, 0.94f));
            UIMenuKit.Title(rail.transform, "游戏菜单", new Vector2(0.08f, 0.84f), new Vector2(0.95f, 0.95f));
            UIMenuKit.AccentBar(rail.transform, new Vector2(0.04f, 0.845f), new Vector2(0.065f, 0.935f));
            UIMenuKit.Caption(rail.transform, "对局不会暂停\n其他玩家与服务器继续运行", new Vector2(0.08f, 0.70f), new Vector2(0.95f, 0.80f), TextDim);

            _navButtons[0] = UIMenuKit.NavButton(rail.transform, "Nav_Resume", "返回游戏", new Vector2(0.08f, 0.56f), new Vector2(0.94f, 0.66f));
            _navButtons[0].button.onClick.AddListener(() => _controller?.RequestResume());
            _navButtons[1] = UIMenuKit.NavButton(rail.transform, "Nav_Settings", "设 置", new Vector2(0.08f, 0.44f), new Vector2(0.94f, 0.54f));
            _navButtons[1].button.onClick.AddListener(() => _controller?.RequestOpenSettings());
            _navButtons[2] = UIMenuKit.NavButton(rail.transform, "Nav_Leave", "退出对局", new Vector2(0.08f, 0.32f), new Vector2(0.94f, 0.42f), AccentRed);
            _navButtons[2].button.onClick.AddListener(() => _controller?.RequestLeave());

            _contextText = UIMenuKit.Caption(rail.transform, string.Empty, new Vector2(0.08f, 0.02f), new Vector2(0.95f, 0.12f), TextDim);
            _contextText.alignment = TextAlignmentOptions.BottomLeft;

            // —— 内容区：暂停主页 ——
            _pauseHome = UIMenuKit.Panel("PauseHome", rootRect, new Color(0, 0, 0, 0), new Vector2(0.26f, 0.06f), new Vector2(0.98f, 0.94f));
            _pauseHome.GetComponent<Image>().raycastTarget = false;
            UIMenuKit.Title(_pauseHome.transform, "已暂停", new Vector2(0.04f, 0.74f), new Vector2(0.9f, 0.9f));
            UIMenuKit.Body(_pauseHome.transform,
                "ESC 或「返回游戏」回到战斗。\n· 世界继续运行：计时、AI、其他玩家不受影响\n· 打开菜单期间本地输入已屏蔽（移动/开火/换弹）\n· 设置更改即时预览，「应用」后才保存",
                new Vector2(0.04f, 0.34f), new Vector2(0.9f, 0.68f), TextDim);

            // —— 内容区：设置页 ——
            var settingsHost = UIMenuKit.Panel("SettingsHost", rootRect, new Color(0, 0, 0, 0), new Vector2(0.26f, 0.06f), new Vector2(0.98f, 0.94f));
            settingsHost.GetComponent<Image>().raycastTarget = false;
            _settingsPanel = settingsHost.AddComponent<GameplaySettingsPanel>();
            _settingsPanel.Build(this);

            // —— 退出确认弹窗 ——
            _leaveDialog = UIMenuKit.Panel("LeaveDialog", rootRect, CardDark, new Vector2(0.32f, 0.32f), new Vector2(0.70f, 0.68f));
            UIMenuKit.Title(_leaveDialog.transform, "退出当前对局", new Vector2(0.08f, 0.76f), new Vector2(0.92f, 0.92f));
            _leaveBodyText = UIMenuKit.Body(_leaveDialog.transform, string.Empty, new Vector2(0.08f, 0.40f), new Vector2(0.92f, 0.72f), TextDim);
            _leaveConfirmButton = UIMenuKit.MenuButton(_leaveDialog.transform, "LeaveConfirm", "确认退出", AccentRed,
                new Vector2(0.08f, 0.10f), new Vector2(0.47f, 0.26f));
            _leaveConfirmButton.onClick.AddListener(() =>
            {
                SetLeaveBusy(true);
                FireLeaveRoomApi();
                _controller?.ConfirmLeave();
            });
            var leaveCancel = UIMenuKit.MenuButton(_leaveDialog.transform, "LeaveCancel", "取 消", new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0.53f, 0.10f), new Vector2(0.92f, 0.26f));
            leaveCancel.onClick.AddListener(() => { SetLeaveBusy(false); _controller?.CancelLeave(); });

            // —— 键位冲突弹窗 ——
            _conflictDialog = UIMenuKit.Panel("ConflictDialog", rootRect, CardDark, new Vector2(0.32f, 0.34f), new Vector2(0.70f, 0.66f));
            UIMenuKit.Title(_conflictDialog.transform, "键位冲突", new Vector2(0.08f, 0.74f), new Vector2(0.92f, 0.90f));
            _conflictBodyText = UIMenuKit.Body(_conflictDialog.transform, string.Empty, new Vector2(0.08f, 0.42f), new Vector2(0.92f, 0.70f), TextDim);
            var swapBtn = UIMenuKit.MenuButton(_conflictDialog.transform, "ConflictSwap", "交换键位", AccentRed,
                new Vector2(0.08f, 0.10f), new Vector2(0.47f, 0.26f));
            swapBtn.onClick.AddListener(() => _controller?.ResolveRebindConflict(swap: true));
            var cancelSwap = UIMenuKit.MenuButton(_conflictDialog.transform, "ConflictCancel", "取 消", new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0.53f, 0.10f), new Vector2(0.92f, 0.26f));
            cancelSwap.onClick.AddListener(() => _controller?.ResolveRebindConflict(swap: false));

            gameObject.SetActive(false); // 初始关闭（OnMenuStateChanged 统一显隐）
        }

        private readonly (Button button, Image accent, TMP_Text label)[] _navButtons = new (Button, Image, TMP_Text)[3];

        // ---- 控制器状态回调 ----

        public void OnMenuStateChanged(GameplayMenuState state)
        {
            bool visible = _controller != null && _controller.Machine.MenuVisible;
            gameObject.SetActive(visible);
            if (!visible) return;

            bool inSettings = state == GameplayMenuState.Settings || state == GameplayMenuState.RebindCapture;
            _pauseHome.SetActive(state == GameplayMenuState.PauseMenu);
            _settingsPanel.gameObject.SetActive(inSettings);
            if (inSettings) _settingsPanel.Refresh(_controller);

            _leaveDialog.SetActive(state == GameplayMenuState.LeaveConfirm);
            if (state == GameplayMenuState.LeaveConfirm) RefreshLeaveText();

            bool conflictOpen = state == GameplayMenuState.RebindCapture && _controller != null && _controller.PendingRebindConflict != null;
            _conflictDialog.SetActive(conflictOpen);
            if (conflictOpen) RefreshConflictText();

            RefreshNav(state);
            RefreshKeyRowLabels();
        }

        public void OnLeaveContextChanged() => RefreshLeaveText();

        private void RefreshNav(GameplayMenuState state)
        {
            for (var i = 0; i < _navButtons.Length; i++)
            {
                bool selected = state == GameplayMenuState.PauseMenu
                    ? i == 0
                    : i == 1 && (state == GameplayMenuState.Settings || state == GameplayMenuState.RebindCapture);
                _navButtons[i].accent.color = selected ? AccentRed : new Color(1f, 1f, 1f, 0.08f);
                _navButtons[i].label.color = selected ? TextBright : TextDim;
                _navButtons[i].label.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
            }
        }

        private void RefreshLeaveText()
        {
            if (_controller == null) return;
            int count = _controller.EffectivePlayerCount;
            string body;
            if (!GameplayMenuController.IsNetworkActive)
                body = "当前为离线模式。\n确认后将返回大厅。";
            else if (_controller.IsLocalServer)
                body = $"你是房主（服务器随你的客户端运行）。\n你退出后服务器将关闭，本局对所有玩家结束。\n当前有效玩家数：{count}";
            else if (count <= 2)
                body = "当前为 2 人对局。\n退出将立即结束本局，并按退出处理（你将判负）。\n当前有效玩家数：2";
            else
                body = $"你将退出本局，其他玩家继续比赛。\n当前有效玩家数：{count}";
            if (_leaveBodyText != null) _leaveBodyText.text = body;
        }

        private void RefreshConflictText()
        {
            if (_controller == null || _conflictBodyText == null) return;
            var conflict = _controller.PendingRebindConflict;
            if (conflict == null) return;
            var binding = SettingsKeyMap.Find(conflict.Value);
            _conflictBodyText.text = $"该按键已被「{(binding != null ? binding.label : conflict.Value.ToString())}」占用。\n选择「交换键位」互换两键，或取消本次重绑。";
        }

        private void RefreshKeyRowLabels()
        {
            if (_controller == null) return;
            for (var i = 0; i < _keyRows.Length; i++)
            {
                var (action, _, label) = _keyRows[i];
                if (label == null) continue;
                bool capturing = _controller.RebindAction == action;
                label.text = capturing ? "按任意键…" : SettingsKeyMap.DisplayName(SettingsKeyMap.Get(action));
                label.color = capturing ? AccentRed : TextBright;
            }
        }

        /// <summary>键位行注册（GameplaySettingsPanel 构建时调用；标签随重绑态刷新）。</summary>
        public void RegisterKeyRow(int index, SettingsKeyMap.Action action, Button keyBtn, TMP_Text keyLabel)
            => _keyRows[index] = (action, keyBtn, keyLabel);

        /// <summary>重绑后键位标签即时刷新入口。</summary>
        internal void NotifyKeyLabelsDirty() => RefreshKeyRowLabels();

        /// <summary>确认按钮 busy 态（防双击/重复请求）。</summary>
        public void SetLeaveBusy(bool busy)
        {
            if (_leaveConfirmButton != null)
            {
                _leaveConfirmButton.interactable = !busy;
                var label = _leaveConfirmButton.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = busy ? "处理中…" : "确认退出";
            }
        }

        /// <summary>离开房间注册表（POST /api/rooms/leave；fire-and-forget，失败不打断本地流程）。</summary>
        private async void FireLeaveRoomApi()
        {
            var app = AppRoot.Instance;
            if (app == null || app.ApiClient == null) return;
            if (app.Session?.Room == null) return;
            try
            {
                var result = await app.ApiClient.LeaveRoomAsync();
                if (result.Success) app.Session.ClearRoom();
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[GameplayMenuView] 房间注册表 leave 调用失败（不打断本地退出流程）: " + ex.Message);
            }
        }
    }
}
