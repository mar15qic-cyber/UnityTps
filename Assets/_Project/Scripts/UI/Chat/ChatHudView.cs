using System;
using System.Collections.Generic;
using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI.Chat
{
    /// <summary>
    /// 聊天 HUD（C4/I1，Docs/26 §3.1；纯代码 uGUI，挂接范式同 MatchScoreboardView.TryMount）：
    /// 收起=左下角最近 3 条（每条 8s 后 2s 淡出）；展开=深色底板最近 30 条可滚动 + 一行输入；
    /// Enter 打开全体、Shift+Enter 队伍、Esc 关闭保留草稿；输入期 GameplayInputGate 聊天占用
    /// （封锁移动/视角/开火/ADS/换弹/切枪，只释放自己的原因）；菜单打开让出焦点（光标归菜单）；
    /// 正文渲染禁用富文本（TMP 标签注入防护——转义显示）。死亡/结算界面同样可用。
    /// </summary>
    public sealed class ChatHudView : MonoBehaviour
    {
        private const int RecentRows = 3;
        private const float RecentLifetimeSeconds = 8f;
        private const float RecentFadeSeconds = 2f;
        private const string OpenHint = "ENTER 聊天  ·  SHIFT+ENTER 队伍频道";
        private const string ChannelAllLabel = "【全体】";
        private const string ChannelTeamLabel = "【队伍】";
        private const string ChannelSystemLabel = "【系统】";

        private readonly struct RecentEntry
        {
            public readonly ChatClientMessage Message;
            public readonly float ShownAtRealtime;
            public RecentEntry(ChatClientMessage message, float shownAtRealtime)
            {
                Message = message;
                ShownAtRealtime = shownAtRealtime;
            }
        }

        private GameObject _panel;
        /// <summary>背景板 Image（2026-09-18 问题2：收起态隐藏、展开态显示；Build 时捕获）。</summary>
        private UnityEngine.UI.Image _panelImage;
        private RectTransform _recentRoot;
        private readonly List<TMP_Text> _recentRows = new();
        private readonly List<CanvasGroup> _recentGroups = new();
        private readonly List<RecentEntry> _recentEntries = new();
        private GameObject _expandedRoot;
        private ScrollRect _historyScroll;
        private TMP_Text _historyText;
        private TMP_Text _channelLabel;
        private TMP_InputField _input;
        private TMP_Text _hintText;
        private TMP_Text _newMessageHint;
        private bool _newMessagePending;

        private readonly List<ChatClientMessage> _history = new();
        private string _pendingChannel = ChatRules.ChannelAll;
        private string _draft = string.Empty;
        private bool _open;
        /// <summary>在途发送正文（非 null = 已提交未确认；R9：确认前真实草稿不丢）。</summary>
        private string _pendingSend;
        /// <summary>上下文闸门（审计 2026-09-16 §5.3-6）：false=无有效房间/页面上下文，热键不得开聊天。
        /// 旧实现 StopAndClear 只清数据，Hud 仍每帧跑 Update，Enter 还能重新打开并抢焦点。</summary>
        private bool _interactive = true;

        public event Action<string, string> SendRequested; // (channel, body)
        public event Action Closed;
        public event Action ChannelChanged;                 // 换频道时通知（清队伍草稿等由控制器决策）

        public bool IsOpen => _open;
        public string PendingChannel => _pendingChannel;

        /// <summary>挂载入口（幂等）：只认**目标画布**下的既有实例（审计 §5.3-4：不允许任意画布上的
        /// 旧 Hud 冒充本次挂载成功），包含未激活实例（池化/被卸载态复用）。</summary>
        internal static ChatHudView TryMount(Canvas canvas)
        {
            if (canvas == null) return null;
            var existing = canvas.GetComponentInChildren<ChatHudView>(true);
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                existing.SetInteractive(true);
                return existing;
            }
            var root = new GameObject("ChatHud", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            return root.AddComponent<ChatHudView>();
        }

        /// <summary>上下文闸门：false 时关闭展开窗、释放输入焦点/IME，且热键不再打开聊天。</summary>
        public void SetInteractive(bool interactive)
        {
            _interactive = interactive;
            if (!interactive && _open) Close(keepDraft: true);
            if (!interactive) ReleaseFocus();
        }

        /// <summary>卸载前的确定性收口：关展开窗、释放输入焦点/IME（数据由控制器按会话语义决定去留）。</summary>
        public void ForceCloseAndRelease()
        {
            if (_open) Close(keepDraft: true);
            else ReleaseFocus();
        }

        private void OnEnable()
        {
            if (_panel == null) Build();
        }

        private void OnDisable()
        {
            // 场景卸载/隐藏：释放聊天占用（草稿保留）
            ReleaseFocus();
        }

        public void Push(ChatClientMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.Body)) return;
            _history.Add(message);
            while (_history.Count > 30) _history.RemoveAt(0);
            _recentEntries.Add(new RecentEntry(message, Time.unscaledTime));
            while (_recentEntries.Count > RecentRows * 4) _recentEntries.RemoveAt(0);
            RebuildHistoryText();
            if (_open)
            {
                if (NearBottom()) ScrollToBottom();
                else _newMessagePending = true;
            }
        }

        /// <summary>清空全部可见历史（离开房间时调用；同一房间返房保留）。</summary>
        public void ClearHistory()
        {
            _history.Clear();
            _recentEntries.Clear();
            RebuildHistoryText();
            for (int i = 0; i < _recentRows.Count; i++)
            {
                _recentRows[i].text = string.Empty;
                SetRowAlpha(i, 0f);
            }
            UpdateHintVisibility();
        }

        /// <summary>清空草稿（含输入行当前文字与在途发送；换队/退房清理用）。</summary>
        public void ClearDraft()
        {
            _draft = string.Empty;
            _pendingSend = null;
            if (_input != null) _input.text = string.Empty;
        }

        /// <summary>
        /// 换队清理（R9 审计修复）：删除旧队聊显示（30 条历史与最近窗口内的队伍频道消息）
        /// 并清空草稿/在途/输入行——满足"换队删除旧队历史与草稿"要求。
        /// </summary>
        public void PurgeTeamChannel()
        {
            _history.RemoveAll(m => m != null && m.Channel == ChatRules.ChannelTeam);
            _recentEntries.RemoveAll(e => e.Message == null || e.Message.Channel == ChatRules.ChannelTeam);
            ClearDraft();
            RebuildHistoryText();
        }

        /// <summary>发送确认（HTTP 成功 / RPC 已交付服务器）：清除在途草稿与输入行。</summary>
        public void CommitPendingDraft()
        {
            _pendingSend = null;
            _draft = string.Empty;
            if (_input != null) _input.text = string.Empty;
        }

        /// <summary>
        /// RPC 回显确认（2026-09-13 矩阵 D）：收到服务器回显后才清在途草稿。
        /// 若用户已在输入行输入新内容（OnSubmit 后输入行被清空，非空即新输入），只解除在途标记、
        /// 绝不清除输入行——避免确认覆盖用户后续输入。
        /// </summary>
        public void CommitRpcDraftIfUnedited()
        {
            if (_pendingSend == null) return;
            bool untouched = _input == null || string.IsNullOrEmpty(_input.text);
            _pendingSend = null;
            if (untouched)
            {
                _draft = string.Empty;
                if (_input != null) _input.text = string.Empty;
            }
        }

        /// <summary>RPC 超时未确认：在途正文还原回输入行（草稿不丢），用户可重试。</summary>
        public void FailPendingRpcDraft() => RestorePendingDraft();

        /// <summary>发送失败（R9 审计修复）：把在途正文还原到输入行——真实草稿不丢。
        /// 2026-09-18：Enter=发送即关后，失败还原需要把聊天重新打开（否则草稿沉在不可见输入行）。</summary>
        public void RestorePendingDraft()
        {
            if (_pendingSend == null) return;
            _draft = _pendingSend;
            _pendingSend = null;
            if (!_open && _interactive) Open(_pendingChannel);
            if (_input != null)
            {
                _input.text = _draft;
                _input.ActivateInputField();
            }
        }

        /// <summary>重建历史显示（R9 审计修复：从有界消息模型整体重建——删除条目真正从显示中消失）。</summary>
        private void RebuildHistoryText()
        {
            if (_historyText == null) return;
            if (_history.Count == 0) { _historyText.text = string.Empty; return; }
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < _history.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(Format(_history[i]));
            }
            _historyText.text = builder.ToString();
        }

        private static string Format(ChatClientMessage message)
        {
            // 注入防护（Docs/26 §3.3 禁止富文本解析）：TMP 富文本开启时对用户内容做转义显示
            if (message.Channel == ChatRules.ChannelSystem) return ChannelSystemLabel + " " + Escape(message.Body);
            var label = message.Channel == ChatRules.ChannelTeam ? ChannelTeamLabel : ChannelAllLabel;
            var name = string.IsNullOrEmpty(message.SenderUsername) ? "系统" : message.SenderUsername;
            return label + " " + Escape(name) + ": " + Escape(message.Body);
        }

        private static string Escape(string value) => (value ?? string.Empty).Replace("<", "&lt;");

        private void Update()
        {
            if (_panel == null) return;

            // 菜单打开 → 聊天让出焦点（保留草稿；光标归菜单所有）
            if (_open && GameplayInputGate.MenuOpen) Close(keepDraft: true);
            if (GameplayInputGate.MenuOpen || GameplayInputGate.HardLocked)
            {
                UpdateRecentFade();
                return;
            }

            // 收起态热键：Enter=全体 / Shift+Enter=队伍（战斗输入未阻塞时才响应）
            // 审计 §5.3-6：无房间/页面上下文（_interactive=false）时不得打开聊天、不得抢焦点
            if (!_open && _interactive && !GameplayInputGate.InputBlocked && KeyboardAvailable())
            {
                bool enter = Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame;
                bool shift = Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
                if (enter)
                    Open(shift ? ChatRules.ChannelTeam : ChatRules.ChannelAll);
            }

            // 展开态：Esc 关闭（保留草稿）；输入失焦释放占用
            if (_open)
            {
                if (KeyboardAvailable() && Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    Close(keepDraft: true);
                }
                else if (_input != null && !_input.isFocused && GameplayInputGate.ChatFocused)
                {
                    ReleaseFocus();
                }
                if (_newMessagePending && _historyScroll != null)
                {
                    _newMessageHint?.gameObject.SetActive(true);
                }
            }

            UpdateRecentFade();
        }

        private static bool KeyboardAvailable() => Keyboard.current != null;

        private void UpdateRecentFade()
        {
            // 清理完全淡出的旧条目（保持最近窗口语义）
            while (_recentEntries.Count > 0
                   && Time.unscaledTime - _recentEntries[0].ShownAtRealtime > RecentLifetimeSeconds + RecentFadeSeconds)
            {
                _recentEntries.RemoveAt(0);
            }
            // R9 审计修复：收起态 3 行从条目尾部整体重建（旧实现只写末行，前两行不滚动）
            for (int row = 0; row < RecentRows; row++)
            {
                if (row >= _recentRows.Count) continue;
                int entryIndex = _recentEntries.Count - RecentRows + row;
                if (entryIndex < 0 || entryIndex >= _recentEntries.Count)
                {
                    if (_recentRows[row].text.Length > 0)
                    {
                        _recentRows[row].text = string.Empty;
                        SetRowAlpha(row, 0f);
                    }
                    continue;
                }
                var entry = _recentEntries[entryIndex];
                float age = Time.unscaledTime - entry.ShownAtRealtime;
                float alpha = age <= RecentLifetimeSeconds ? 1f
                    : Mathf.Clamp01(1f - (age - RecentLifetimeSeconds) / RecentFadeSeconds);
                string text = Format(entry.Message);
                if (_recentRows[row].text != text) _recentRows[row].text = text;
                SetRowAlpha(row, alpha);
            }
            UpdateHintVisibility();
        }

        private void SetRowAlpha(int index, float alpha)
        {
            if (index < _recentGroups.Count && _recentGroups[index] != null)
                _recentGroups[index].alpha = alpha;
        }

        /// <summary>
        /// 收起态底部提示（"ENTER 聊天…"）与最新消息行共用同一底层带：只要还有可见消息行就隐藏提示，
        /// 否则提示文字会与消息（尤其系统消息）叠在一起（2026-09-15 用户实测：系统提示压住聊天消息）。
        /// </summary>
        private void UpdateHintVisibility()
        {
            if (_hintText == null) return;
            bool anyRow = false;
            for (int i = 0; i < _recentRows.Count; i++)
                if (_recentRows[i] != null && _recentRows[i].text.Length > 0) { anyRow = true; break; }
            _hintText.gameObject.SetActive(!_open && !anyRow);
        }

        // ---- 开合与焦点 ----

        private void Open(string channel)
        {
            _open = true;
            _pendingChannel = channel;
            // 2026-09-18 主流 FPS 聊天（实机问题2）：展开态才显示背景板
            SetPanelBackgroundVisible(true);
            if (_expandedRoot != null) _expandedRoot.SetActive(true);
            UpdateHintVisibility();
            UpdateChannelLabel();
            if (_input != null)
            {
                _input.text = _draft;
                _input.gameObject.SetActive(true);
                _input.ActivateInputField();
            }
            AcquireFocus();
            ScrollToBottom();
        }

        private void Close(bool keepDraft)
        {
            if (_input != null)
            {
                // R9：关闭保留草稿时，在途发送正文同样保留（确认/失败由控制器异步收敛）
                if (keepDraft) _draft = _pendingSend ?? _input.text;
                _input.text = string.Empty;
                _input.DeactivateInputField();
            }
            _open = false;
            if (_expandedRoot != null) _expandedRoot.SetActive(false);
            // 收起态：隐藏背景板，只留纯文字消息行（无大块阴影常驻）
            SetPanelBackgroundVisible(false);
            UpdateHintVisibility();
            ReleaseFocus();
            Closed?.Invoke();
        }

        /// <summary>背景板显隐（2026-09-18 问题2）：收起态无背景（CF 式纯文字+淡出），
        /// 展开态（Enter 呼出）才有半透明底板。CanvasGroup 不在此路径（淡出只作用于消息行）。</summary>
        private void SetPanelBackgroundVisible(bool visible)
        {
            if (_panelImage != null) _panelImage.enabled = visible;
        }

        private void AcquireFocus()
        {
            GameplayInputGate.SetChatFocused(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void ReleaseFocus()
        {
            if (!GameplayInputGate.ChatFocused) return;
            GameplayInputGate.SetChatFocused(false);
            // R9 审计修复：光标归还场景输入所有者——仅战斗上下文（存在战斗菜单控制器，其关闭菜单时
            // 恢复锁定）才重新锁定；等待房间/大厅没有锁定语义，聊天关闭不得隐藏/锁定鼠标
            //（选边/准备/设置按钮需要可见光标）。
            if (!GameplayInputGate.MenuOpen && HasBattleMenuOwner())
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private static bool HasBattleMenuOwner()
            => FindFirstObjectByType<Game.Gameplay.Menu.GameplayMenuController>() != null;

        private void UpdateChannelLabel()
        {
            if (_channelLabel != null)
                _channelLabel.text = _pendingChannel == ChatRules.ChannelTeam ? "队伍" : "全体";
        }

        /// <summary>切换频道（展开态点按钮 / 队伍热键）；队伍频道在无队伍时由控制器拒绝。</summary>
        public void SetPendingChannel(string channel)
        {
            if (!ChatRules.IsValidChannel(channel)) return;
            _pendingChannel = channel;
            UpdateChannelLabel();
            ChannelChanged?.Invoke();
        }

        private bool NearBottom()
        {
            return _historyScroll == null || _historyScroll.verticalNormalizedPosition <= 0.05f;
        }

        private void ScrollToBottom()
        {
            if (_historyScroll == null) return;
            Canvas.ForceUpdateCanvases();
            _historyScroll.verticalNormalizedPosition = 0f;
            _newMessagePending = false;
            if (_newMessageHint != null) _newMessageHint.gameObject.SetActive(false);
        }

        private void OnSubmit(string text)
        {
            var body = (text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(body))
            {
                // 2026-09-18 主流 FPS 交互（实机问题2）：空草稿 Enter = 直接关闭
                Close(keepDraft: false);
                return;
            }
            if (_pendingSend != null) return; // 在途发送未确认：忽略本次提交（保护在途草稿）
            // R9 审计修复：提交时正文转入"在途"——输入行清空但真实草稿由 _pendingSend 持有，
            // 确认（CommitPendingDraft）才真正清除，失败（RestorePendingDraft）原样还原
            _pendingSend = body;
            _draft = body;
            _input.text = string.Empty;
            SendRequested?.Invoke(_pendingChannel, body);
            // 2026-09-18：Enter = 发送并关闭（主流 FPS；旧实现发送后保持展开需 Esc 关）。
            // 发送失败时 RestorePendingDraft 会带草稿重新打开，不丢正文。
            Close(keepDraft: false);
        }

        // ---- 纯代码 uGUI 构建 ----

        private void Build()
        {
            var rootRect = (RectTransform)transform;
            Stretch(rootRect, 0f, 0.42f, 0f, 0.34f);

            _panel = CreateImage("Panel", transform, new Color(0.04f, 0.05f, 0.07f, 0.78f)).gameObject;
            Stretch((RectTransform)_panel.transform, 0f, 1f, 0f, 1f);
            // 2026-09-18 实机问题2：背景板默认隐藏（收起态=纯文字消息行+淡出，无大块阴影常驻），
            // 展开（Enter 呼出）才显示；消息行加描边保证无底板时的可读性。
            _panelImage = _panel.GetComponent<UnityEngine.UI.Image>();
            _panelImage.enabled = false;

            // 收起态：最近 3 条（底部对齐）
            var recentAnchor = new GameObject("Recent", typeof(RectTransform));
            recentAnchor.transform.SetParent(_panel.transform, false);
            _recentRoot = recentAnchor.GetComponent<RectTransform>();
            Stretch(_recentRoot, 0.02f, 0.98f, 0.02f, 0.98f);
            for (int i = 0; i < RecentRows; i++)
            {
                var rowGo = new GameObject("RecentRow" + i, typeof(RectTransform), typeof(CanvasGroup));
                rowGo.transform.SetParent(_recentRoot, false);
                var rowRect = (RectTransform)rowGo.transform;
                float y0 = 0.66f - i * 0.33f;
                Stretch(rowRect, 0f, 1f, y0, y0 + 0.33f);
                var text = CreateText("Text", rowGo.transform, 14, new Color(0.94f, 0.95f, 0.96f, 1f), TextAlignmentOptions.BottomLeft);
                Stretch(text.rectTransform, 0f, 1f, y0, y0 + 0.33f);
                text.text = string.Empty;
                // 无底板消息行的可读性（2026-09-18 问题2）：深色描边替代旧半透明大阴影
                var outline = text.gameObject.AddComponent<UnityEngine.UI.Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(1.2f, -1.2f);
                _recentRows.Add(text);
                _recentGroups.Add(rowGo.GetComponent<CanvasGroup>());
            }

            // 展开态：深色底板 + 30 条滚动历史 + 输入行（默认隐藏）
            _expandedRoot = CreateImage("Expanded", _panel.transform, new Color(0.03f, 0.04f, 0.06f, 0.9f)).gameObject;
            Stretch((RectTransform)_expandedRoot.transform, 0f, 1f, 0.34f, 1f);
            var scrollGo = new GameObject("HistoryScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(_expandedRoot.transform, false);
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            var scrollRect = (RectTransform)scrollGo.transform;
            Stretch(scrollRect, 0.02f, 0.98f, 0.16f, 0.96f);
            _historyScroll = scrollGo.GetComponent<ScrollRect>();
            _historyScroll.horizontal = false;
            _historyScroll.movementType = ScrollRect.MovementType.Clamped;
            _historyScroll.scrollSensitivity = 24f;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRect = (RectTransform)contentGo.transform;
            Stretch(contentRect, 0f, 1f, 0f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            var layout = contentGo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 2f;
            layout.padding = new RectOffset(4, 4, 4, 4);
            _historyScroll.content = contentRect;

            var historyGo = new GameObject("HistoryText", typeof(RectTransform));
            historyGo.transform.SetParent(contentGo.transform, false);
            _historyText = historyGo.AddComponent<TextMeshProUGUI>();
            _historyText.fontSize = 14;
            _historyText.color = new Color(0.94f, 0.95f, 0.96f, 1f);
            _historyText.alignment = TextAlignmentOptions.TopLeft;
            _historyText.text = string.Empty;

            _newMessageHint = CreateText("NewMessages", _expandedRoot.transform, 12,
                new Color(1f, 0.8f, 0.3f, 1f), TextAlignmentOptions.Right);
            Stretch(_newMessageHint.rectTransform, 0.5f, 0.98f, 0.10f, 0.155f);
            _newMessageHint.text = "↓ 新消息";
            _newMessageHint.gameObject.SetActive(false);

            var channelGo = CreateText("Channel", _expandedRoot.transform, 14,
                new Color(0.55f, 0.75f, 1f, 1f), TextAlignmentOptions.Left);
            Stretch(channelGo.rectTransform, 0.02f, 0.14f, 0.03f, 0.14f);
            _channelLabel = channelGo;
            _channelLabel.text = "全体";

            var inputGo = new GameObject("Input", typeof(RectTransform));
            inputGo.transform.SetParent(_expandedRoot.transform, false);
            Stretch((RectTransform)inputGo.transform, 0.14f, 0.98f, 0.02f, 0.15f);
            var inputImage = inputGo.AddComponent<Image>();
            inputImage.color = new Color(0f, 0f, 0f, 0.55f);

            // Viewport（RectMask2D）+ textViewport 赋值 = TMP 输入框的标准构造（同 UIComponents.Input）：
            // 缺 textViewport 时 TMP 的插入符定位/长文本滚动失去视口基准，插入符会落到可见区之外。
            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(inputGo.transform, false);
            var viewportRect = (RectTransform)viewportGo.transform;
            Stretch(viewportRect, 0f, 1f, 0f, 1f);
            Inset(viewportRect, 6f, 4f);

            // 注：AddComponent<TMP_InputField> 会立即触发 Awake/OnEnable（本 GO 处于激活态），
            // 此时 textComponent 尚未赋值 → 先禁用 GO，全部引用接好后再启用（延迟 Awake）。
            inputGo.SetActive(false);
            _input = inputGo.AddComponent<TMP_InputField>();
            _input.lineType = TMP_InputField.LineType.SingleLine;
            var inputTextGo = new GameObject("Text", typeof(RectTransform));
            inputTextGo.transform.SetParent(viewportGo.transform, false);
            var inputTextRect = (RectTransform)inputTextGo.transform;
            Stretch(inputTextRect, 0f, 1f, 0f, 1f); // 内容铺满视口（像素内边距由视口承担）
            var inputText = inputTextGo.AddComponent<TextMeshProUGUI>();
            inputText.fontSize = 14;
            inputText.color = Color.white;
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(viewportGo.transform, false);
            var placeholderRect = (RectTransform)placeholderGo.transform;
            Stretch(placeholderRect, 0f, 1f, 0f, 1f);
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.fontSize = 13;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);
            placeholder.text = "输入消息（ENTER 发送 · ESC 关闭）";
            _input.textViewport = viewportRect;
            _input.textComponent = inputText;
            _input.placeholder = placeholder;
            _input.richText = false; // 注入防护：输入与回显一律不解析 TMP 标签
            inputGo.SetActive(true);
            _input.onSubmit.AddListener(OnSubmit);
            _input.onSelect.AddListener(_ => AcquireFocus());
            _input.onDeselect.AddListener(_ => { if (_open) ReleaseFocus(); });

            _hintText = CreateText("Hint", _panel.transform, 12,
                new Color(0.6f, 0.655f, 0.72f, 0.8f), TextAlignmentOptions.BottomLeft);
            Stretch(_hintText.rectTransform, 0.02f, 0.98f, 0.02f, 0.16f);
            _hintText.text = OpenHint;

            _expandedRoot.SetActive(false);
        }

        private static void Stretch(RectTransform rect, float x0, float x1, float y0, float y1)
        {
            rect.anchorMin = new Vector2(x0, y0);
            rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>在已铺满父级的锚点基础上加四边像素内边距（注意：参数是像素，不是锚点比例——
        /// 旧实现把像素值传给锚点重载导致 rect 退化为负宽，插入符因此不可见）。</summary>
        private static void Inset(RectTransform rect, float horizontal, float vertical)
        {
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent, float size, Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            return text;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
    }
}
