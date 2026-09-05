using Game.Gameplay.Menu;
using Game.Gameplay.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Menu
{
    /// <summary>
    /// 游戏菜单设置面板（Phase A/B）：大厅与战斗共用的设置 UI（数据/应用逻辑全部走
    /// SettingsRuntime/SettingsDraft/KeybindRules 共享服务，本面板只负责构建与绑定）。
    /// 交互语义：音量/灵敏度滑杆即时预览（写实时层）；键位重绑即时生效（运行时缓存）；
    /// 「应用」才持久化；「取消/返回」回滚到进入设置页前；「恢复默认」即时预览出厂值。
    /// 布局：音量卡 / 键位卡（可滚动，11 项不溢出） / 画质卡 + 底部操作行。
    /// </summary>
    public sealed class GameplaySettingsPanel : MonoBehaviour
    {
        private GameplayMenuView _owner;
        private Slider _master, _music, _sfx, _sens;
        private TMP_Text _adsLabel, _fsLabel;
        private TextMeshProUGUI _resLabel, _capLabel; // Stepper 的 out 形参类型
        private int _resIndex, _capIndex;
        private readonly Button[] _keyButtons = new Button[SettingsKeyMap.Bindings.Length];
        private readonly TMP_Text[] _keyLabels = new TMP_Text[SettingsKeyMap.Bindings.Length];

        public void Build(GameplayMenuView owner)
        {
            _owner = owner;
            var rootRect = (RectTransform)transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            UIMenuKit.AccentBar(rootRect, new Vector2(0f, 0.90f), new Vector2(0.018f, 0.98f));
            UIMenuKit.Title(rootRect, "设 置", new Vector2(0.03f, 0.88f), new Vector2(0.5f, 0.99f));

            BuildAudioCard(rootRect, new Vector2(0f, 0.10f), new Vector2(0.31f, 0.84f));
            BuildKeybindCard(rootRect, new Vector2(0.335f, 0.10f), new Vector2(0.655f, 0.84f));
            BuildGraphicsCard(rootRect, new Vector2(0.68f, 0.10f), new Vector2(1f, 0.84f));

            // 底部操作行（恢复默认 / 取消 / 应用）
            UIMenuKit.MenuButton(rootRect, "Defaults", "恢复默认", new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0f, 0f), new Vector2(0.18f, 0.075f))
                .onClick.AddListener(() => { _controllerRef?.ResetSettingsToDefaults(); Refresh(_controllerRef); });
            UIMenuKit.MenuButton(rootRect, "Cancel", "取消（回滚）", new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0.20f, 0f), new Vector2(0.38f, 0.075f))
                .onClick.AddListener(() => _controllerRef?.CancelSettings());
            UIMenuKit.MenuButton(rootRect, "Apply", "应用并保存", new Color(1f, 0.27f, 0.33f),
                new Vector2(0.82f, 0f), new Vector2(1f, 0.075f))
                .onClick.AddListener(() =>
                {
                    _controllerRef?.ApplySettings();
                    _controllerRef?.RequestBackToPause();
                });
        }

        private void BuildAudioCard(Transform parent, Vector2 min, Vector2 max)
        {
            var card = UIMenuKit.Panel("AudioCard", parent, new Color(0.10f, 0.13f, 0.19f, 0.96f), min, max);
            UIMenuKit.Caption(card.transform, "音量与灵敏度（即时生效）", new Vector2(0.08f, 0.905f), new Vector2(0.95f, 0.965f), TextBright());

            _master = UIMenuKit.LabeledSlider(card.transform, "Master", "主音量",
                new Vector2(0.08f, 0.64f), new Vector2(0.94f, 0.825f), 1f);
            _master.onValueChanged.AddListener(v => SetLive(SensitivityTarget.Master, v));

            _music = UIMenuKit.LabeledSlider(card.transform, "Music", "音乐音量",
                new Vector2(0.08f, 0.455f), new Vector2(0.94f, 0.64f), 1f);
            _music.onValueChanged.AddListener(v => SetLive(SensitivityTarget.Music, v));

            _sfx = UIMenuKit.LabeledSlider(card.transform, "Sfx", "音效音量",
                new Vector2(0.08f, 0.27f), new Vector2(0.94f, 0.455f), 1f);
            _sfx.onValueChanged.AddListener(v => SetLive(SensitivityTarget.Sfx, v));

            // 灵敏度：唯一消费链 = InputReader 源头一次缩放（设置页拖动即时预览）
            _sens = UIMenuKit.LabeledSlider(card.transform, "Sens", "鼠标灵敏度",
                new Vector2(0.08f, 0.085f), new Vector2(0.94f, 0.27f), Mathf.InverseLerp(0.1f, 5f, 1f));
            _sens.onValueChanged.AddListener(v => SetLive(SensitivityTarget.Sensitivity, Mathf.Lerp(0.1f, 5f, v)));
        }

        private void BuildKeybindCard(Transform parent, Vector2 min, Vector2 max)
        {
            var card = UIMenuKit.Panel("KeybindCard", parent, new Color(0.10f, 0.13f, 0.19f, 0.96f), min, max);
            UIMenuKit.Caption(card.transform, "键位（点击重设 · Esc 取消）", new Vector2(0.06f, 0.905f), new Vector2(0.96f, 0.965f), TextBright());

            // 可滚动视口（11 项绑定行高固定像素，超出卡片即滚动——键位增多不溢出）
            var viewport = new GameObject("KeyList", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
            viewport.transform.SetParent(card.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = new Vector2(0.04f, 0.04f);
            vpRect.anchorMax = new Vector2(0.97f, 0.885f);
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = Color.clear;
            viewport.GetComponent<Image>().raycastTarget = true;

            var bindings = SettingsKeyMap.Bindings;
            const float rowPixels = 62f;
            var content = new GameObject("KeyContent", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, bindings.Length * rowPixels);
            contentRect.anchoredPosition = Vector2.zero;

            for (var i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                var rowNormMin = 1f - (float)(i + 1) / bindings.Length;
                var rowNormMax = 1f - (float)i / bindings.Length;
                var row = new GameObject("KeyRow_" + b.action, typeof(RectTransform), typeof(Image));
                row.transform.SetParent(content.transform, false);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0f, rowNormMin);
                rowRect.anchorMax = new Vector2(1f, rowNormMax);
                rowRect.offsetMin = new Vector2(2f, 3f);
                rowRect.offsetMax = new Vector2(-2f, -3f);
                row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);

                UITypography.Text("Action", row.transform, b.label, UITheme.FontCaption + 1, TextDim(),
                    new Vector2(0.04f, 0f), new Vector2(0.46f, 1f), TextAlignmentOptions.MidlineLeft);

                var captured = b;
                var keyBtn = UIMenuKit.MenuButton(row.transform, "Key_" + b.action, string.Empty,
                    new Color(0.16f, 0.21f, 0.28f), new Vector2(0.48f, 0.10f), new Vector2(0.76f, 0.90f));
                keyBtn.onClick.AddListener(() => _controllerRef?.RequestBeginRebind(captured.action));
                _keyButtons[i] = keyBtn;
                _keyLabels[i] = keyBtn.GetComponentInChildren<TMP_Text>();
                _owner.RegisterKeyRow(i, captured.action, keyBtn, _keyLabels[i]);

                UIMenuKit.MenuButton(row.transform, "Reset_" + b.action, "默认",
                    new Color(0.16f, 0.21f, 0.28f), new Vector2(0.79f, 0.10f), new Vector2(0.97f, 0.90f))
                    .onClick.AddListener(() =>
                    {
                        if (_controllerRef?.Draft == null) return;
                        _controllerRef.Draft.PreviewKey(captured.action, captured.defaultKey);
                        RefreshLabels();
                    });
            }

            var scroll = card.AddComponent<ScrollRect>();
            scroll.viewport = vpRect;
            scroll.content = contentRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 20f;
            var wheel = card.AddComponent<MouseWheelScroll>();
            wheel.target = scroll;
        }

        private void BuildGraphicsCard(Transform parent, Vector2 min, Vector2 max)
        {
            var card = UIMenuKit.Panel("GraphicsCard", parent, new Color(0.10f, 0.13f, 0.19f, 0.96f), min, max);
            UIMenuKit.Caption(card.transform, "画质（应用后生效）", new Vector2(0.08f, 0.90f), new Vector2(0.95f, 0.97f), TextBright());

            // 分辨率 stepper
            UITypography.Text("ResLabel", card.transform, "分辨率", UITheme.FontCaption + 1, TextDim(),
                new Vector2(0.08f, 0.84f), new Vector2(0.95f, 0.89f), TextAlignmentOptions.Left);
            UIComponents.Stepper("ResStepper", card.transform, new Vector2(0.08f, 0.72f), new Vector2(0.94f, 0.82f),
                out var resPrev, out var resNext, out _resLabel);
            resPrev.onClick.AddListener(() => { StepOption(ref _resIndex, SettingsModel.SupportedResolutions.Length, -1); RefreshOptionLabels(); });
            resNext.onClick.AddListener(() => { StepOption(ref _resIndex, SettingsModel.SupportedResolutions.Length, +1); RefreshOptionLabels(); });

            // 全屏开关
            var fsBtn = UIMenuKit.MenuButton(card.transform, "Fullscreen", string.Empty, new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0.08f, 0.56f), new Vector2(0.94f, 0.66f));
            _fsLabel = fsBtn.GetComponentInChildren<TMP_Text>();
            fsBtn.onClick.AddListener(() =>
            {
                if (_controllerRef?.Draft == null) return;
                _controllerRef.Draft.Fullscreen = !_controllerRef.Draft.Fullscreen;
                RefreshOptionLabels();
            });

            // 帧率上限 stepper
            UITypography.Text("CapLabel", card.transform, "帧率上限", UITheme.FontCaption + 1, TextDim(),
                new Vector2(0.08f, 0.44f), new Vector2(0.95f, 0.49f), TextAlignmentOptions.Left);
            UIComponents.Stepper("CapStepper", card.transform, new Vector2(0.08f, 0.32f), new Vector2(0.94f, 0.42f),
                out var capPrev, out var capNext, out _capLabel);
            capPrev.onClick.AddListener(() => { StepOption(ref _capIndex, SettingsModel.FrameCapOptions.Length, -1); RefreshOptionLabels(); });
            capNext.onClick.AddListener(() => { StepOption(ref _capIndex, SettingsModel.FrameCapOptions.Length, +1); RefreshOptionLabels(); });

            // ADS 输入模式
            var adsBtn = UIMenuKit.MenuButton(card.transform, "AdsMode", string.Empty, new Color(0.16f, 0.21f, 0.28f),
                new Vector2(0.08f, 0.14f), new Vector2(0.94f, 0.24f));
            _adsLabel = adsBtn.GetComponentInChildren<TMP_Text>();
            adsBtn.onClick.AddListener(() =>
            {
                if (_controllerRef?.Draft == null) return;
                _controllerRef.Draft.AdsToggleMode = !_controllerRef.Draft.AdsToggleMode;
                AdsInputMode.Toggle = _controllerRef.Draft.AdsToggleMode; // 即时生效（取消时 RestoreLive 回写捕获值）
                RefreshOptionLabels();
            });
        }

        // ---- 绑定与刷新 ----

        private GameplayMenuController _controllerRef;

        /// <summary>状态刷新（视图在每次状态变化时调用；滑杆/选项回读草稿值，触发环用 WithoutNotify 规避）。</summary>
        public void Refresh(GameplayMenuController controller)
        {
            _controllerRef = controller;
            var draft = controller?.Draft;
            if (draft == null) return;
            _master.SetValueWithoutNotify(draft.MasterVolume);
            _music.SetValueWithoutNotify(draft.MusicVolume);
            _sfx.SetValueWithoutNotify(draft.SfxVolume);
            _sens.SetValueWithoutNotify(Mathf.InverseLerp(0.1f, 5f, draft.Sensitivity));
            _resIndex = System.Math.Max(0, System.Array.IndexOf(SettingsModel.SupportedResolutions, draft.Resolution));
            _capIndex = System.Math.Max(0, System.Array.IndexOf(SettingsModel.FrameCapOptions, draft.FrameCap));
            RefreshOptionLabels();
            RefreshLabels();
        }

        private void SetLive(SensitivityTarget target, float v)
        {
            var draft = _controllerRef != null ? _controllerRef.Draft : null;
            if (draft == null) return;
            SettingsRuntime.SetLive(target, v);
            switch (target)
            {
                case SensitivityTarget.Master: draft.MasterVolume = v; break;
                case SensitivityTarget.Music: draft.MusicVolume = v; break;
                case SensitivityTarget.Sfx: draft.SfxVolume = v; break;
                case SensitivityTarget.Sensitivity: draft.Sensitivity = v; break;
            }
        }

        private static void StepOption(ref int index, int length, int delta)
            => index = (index + delta + length) % length;

        private void RefreshOptionLabels()
        {
            var draft = _controllerRef != null ? _controllerRef.Draft : null;
            if (draft == null) return;
            if (_resLabel != null) _resLabel.text = SettingsModel.FormatResolution(SettingsModel.SupportedResolutions[_resIndex]);
            if (_capLabel != null) _capLabel.text = SettingsModel.FormatFrameCap(SettingsModel.FrameCapOptions[_capIndex]);
            if (_fsLabel != null) _fsLabel.text = draft.Fullscreen ? "全屏：开" : "全屏：关";
            if (_adsLabel != null) _adsLabel.text = "开镜方式：" + (draft.AdsToggleMode ? "切换" : "长按");
        }

        private void RefreshLabels()
        {
            for (var i = 0; i < SettingsKeyMap.Bindings.Length; i++)
            {
                if (_keyLabels[i] == null) continue;
                var b = SettingsKeyMap.Bindings[i];
                bool capturing = _controllerRef != null && _controllerRef.RebindAction == b.action;
                _keyLabels[i].text = capturing ? "按任意键…" : SettingsKeyMap.DisplayName(SettingsKeyMap.Get(b.action));
                _keyLabels[i].color = capturing ? new Color(1f, 0.27f, 0.33f) : new Color(0.96f, 0.97f, 0.98f);
            }
        }

        private static Color TextDim() => new Color(0.60f, 0.66f, 0.73f);
        private static Color TextBright() => new Color(0.96f, 0.97f, 0.98f);
    }
}
