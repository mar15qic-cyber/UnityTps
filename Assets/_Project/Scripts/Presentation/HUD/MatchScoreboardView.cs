using System.Collections.Generic;
using FishNet;
using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// Tab 战绩面板（Docs/23 P1-6 表现升级）：现代军事 FPS 风格全量战绩板——
    /// 头部（模式/地图/个人击杀竞赛/剩余时间）+ 单榜表格（PLAYER/SCORE/KILLS/DEATHS/K/D/ASSISTS/PING，
    /// 行号 + 头像槽 + 本地玩家淡蓝高亮与 YOU 徽标）+ 底部操作提示；按住 Tab 显示、松开隐藏，
    /// 菜单打开强制隐藏。纯代码 uGUI 运行时自挂载（零资产改动，锚点 = WeaponHudView 同画布）。
    /// 数据源：服务器权威战绩快照（MatchEventKind.ScoreboardSnapshot，含显示名/助攻/ping/剩余时间，
    /// 由 MatchHudView.TryMount 链路挂载）；快照未达（离线/未开局）回退本地扫描（名字降级为身份 id）。
    /// 样式常量仿 MatchHudView 先例（内置 ugui + 英文文案 + 局部色板；大厅 DesignSystem 令牌不跨用——HUD 层惯例）。
    /// </summary>
    public sealed class MatchScoreboardView : MonoBehaviour
    {
        // ---- 色板（军事半透明战术风；与 UITheme 文本色保持同一视觉语言） ----

        private static readonly Color PanelBg = new Color(0.055f, 0.065f, 0.085f, 0.86f);
        private static readonly Color HeaderBg = new Color(0.10f, 0.12f, 0.155f, 0.92f);
        private static readonly Color BlockBg = new Color(1f, 1f, 1f, 0.02f);
        private static readonly Color ScoreAccent = new Color(0.30f, 0.59f, 1.00f, 1f);
        private static readonly Color RowAlt = new Color(1f, 1f, 1f, 0.035f);
        private static readonly Color SelfRow = new Color(0.30f, 0.59f, 1.00f, 0.13f);
        private static readonly Color Divider = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color AvatarSlotBg = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color TextPrimary = new Color(0.96f, 0.97f, 0.98f, 1f);
        private static readonly Color TextMuted = new Color(0.60f, 0.655f, 0.72f, 1f);
        private static readonly Color TextDim = new Color(0.60f, 0.655f, 0.72f, 0.75f);
        private static readonly Color TeamRedColor = new Color(0.94f, 0.42f, 0.42f, 1f);
        private static readonly Color TeamBlueColor = new Color(0.44f, 0.66f, 1.00f, 1f);
        private const string ModeLabel = "FREE-FOR-ALL  ·  KILL RACE";
        private const string HintLabel = "HOLD TAB  ·  SCOREBOARD";
        private const string UnknownClock = "--:--";
        private const float PingReportIntervalSeconds = 2f;

        // ---- 缓存的快照状态 ----

        private MatchScoreboardEntry[] _entries = System.Array.Empty<MatchScoreboardEntry>();
        private long _snapshotTimeLeft = -1;
        private MatchScoreboardPayload _currentPayload;
        private float _snapshotReceivedUnscaled;

        private static MatchScoreboardView _active;

        internal static string DisplayNameFor(string playerId)
        {
            var view = _active;
            if (view != null && view._entries != null)
                foreach (var entry in view._entries)
                    if (entry.playerId == playerId && !string.IsNullOrWhiteSpace(entry.displayName))
                        return entry.displayName;
            return playerId;
        }

        /// <summary>TDM 常驻 HUD 数据口（Docs/26 §2.4 红蓝总分/倒计时）：
        /// Phase 4 口径统一——优先读快照权威 redKills/blueKills（MatchLifecycle.AddTeamKill 单写者），
        /// 双零时回退按 entries 聚合个人击杀（旧载荷兼容）；仅团队模式且快照在位返回 true；
        /// 剩余秒数按快照接收时刻回推。离线/非团队 = false。</summary>
        internal static bool TryGetTeamScores(out int redScore, out int blueScore, out int timeLeftSeconds,
            out int targetKills)
        {
            redScore = 0;
            blueScore = 0;
            timeLeftSeconds = -1;
            targetKills = MatchRules.TargetKills;
            var view = _active;
            if (view == null || view._currentPayload == null || !view._currentPayload.IsTeamMode
                || view._entries == null || view._entries.Length == 0) return false;
            redScore = Mathf.Max(0, view._currentPayload.redKills);
            blueScore = Mathf.Max(0, view._currentPayload.blueKills);
            targetKills = Mathf.Max(1, view._currentPayload.killTarget);
            if (redScore == 0 && blueScore == 0)
            {
                foreach (var entry in view._entries)
                {
                    if (entry.teamId == MatchRules.TeamRed) redScore += Mathf.Max(0, entry.kills);
                    else if (entry.teamId == MatchRules.TeamBlue) blueScore += Mathf.Max(0, entry.kills);
                }
            }
            if (view._snapshotTimeLeft >= 0)
                timeLeftSeconds = Mathf.Max(0, (int)(view._snapshotTimeLeft
                    - (Time.unscaledTime - view._snapshotReceivedUnscaled)));
            return true;
        }

        private bool _hasSnapshot;
        private string _selfId;

        private NetworkCombatAuthority _localPlayer;
        private float _localScanTimer;

        // ---- UI 引用 ----

        private GameObject _panel;
        private Text _modeText;
        private Text _mapText;
        private Text _scoreText;
        private Text _timerText;
        private Text _hintText;
        private readonly ScoreboardBlock _ffa = new ScoreboardBlock(ScoreAccent);
        // Phase 4 CF 式双栏：TDM 红/蓝分榜（KillRace 仍用 _ffa 单榜）
        private readonly ScoreboardBlock _red = new ScoreboardBlock(TeamRedColor);
        private readonly ScoreboardBlock _blue = new ScoreboardBlock(TeamBlueColor);

        private sealed class ScoreboardBlock
        {
            public readonly Color Accent;
            public readonly List<RowViews> Rows = new List<RowViews>();
            /// <summary>行高（面板锚系）：FFA 单榜高密度、TDM 半栏适中（Phase 4 压缩防溢出）。</summary>
            public float RowHeight = 0.122f;
            public GameObject Root;
            public Text TitleText;
            public Text MetaText;

            public ScoreboardBlock(Color accent) => Accent = accent;
        }

        private sealed class RowViews
        {
            public GameObject Root;
            public Image Background;
            public Text Rank;
            public Text Name;
            public GameObject YouBadge;
            public Text Score;
            public Text Kills;
            public Text Deaths;
            public Text Ratio;
            public Text Assists;
            public Text Ping;
        }

        /// <summary>挂载入口（MatchHudView.TryMount 调用；幂等）：锚点 = WeaponHudView 所在画布。</summary>
        internal static void TryMount(Canvas canvas)
        {
            if (FindFirstObjectByType<MatchScoreboardView>() != null) return;
            if (canvas == null)
            {
                Debug.LogWarning("[MatchScoreboardView] 未找到画布锚点，战绩面板未挂载");
                return;
            }
            var root = new GameObject("MatchScoreboard", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            var view = root.AddComponent<MatchScoreboardView>();
            _active = view;
            view.Build();
        }

        private void OnEnable() => NetworkCombatAuthority.OnMatchEvent += HandleMatchEvent;
        private void OnDisable() => NetworkCombatAuthority.OnMatchEvent -= HandleMatchEvent;

        private void OnDestroy()
        {
            if (_active == this) _active = null;
        }

        private void HandleMatchEvent(MatchEventKind kind, string payload)
        {
            if (kind != MatchEventKind.ScoreboardSnapshot || string.IsNullOrEmpty(payload)) return;
            var snapshot = JsonUtility.FromJson<MatchScoreboardPayload>(payload);
            if (snapshot?.entries == null) return;
            _entries = snapshot.entries;
            _currentPayload = snapshot;
            _snapshotTimeLeft = snapshot.timeLeftSeconds;
            _snapshotReceivedUnscaled = Time.unscaledTime;
            _hasSnapshot = true;
            RebuildRows();
        }

        private void Update()
        {
            UpdateLocalPlayer();
            UpdatePingReport();
            UpdateVisibility();
            if (_panel != null && _panel.activeSelf)
            {
                UpdateHeader();
                UpdateTimer();
                if (!_hasSnapshot)
                {
                    // 快照未达（离线/未开局）：低频回退本地扫描，保证面板不空转
                    _localScanTimer -= Time.unscaledDeltaTime;
                    if (_localScanTimer <= 0f)
                    {
                        _localScanTimer = PingReportIntervalSeconds;
                        RebuildFromLocalScan();
                    }
                }
            }
        }

        // ---- 显隐（按住 Tab；菜单打开强制隐藏） ----

        private void UpdateVisibility()
        {
            // Phase 4：聊天聚焦（IME 输入中）按 Tab 是输入法行为，不切换战绩面板
            bool show = !GameplayInputGate.MenuOpen
                && !GameplayInputGate.ChatFocused
                && Keyboard.current != null
                && Keyboard.current.tabKey.isPressed;
            if (_panel == null) return;
            if (_panel.activeSelf == show) return;
            _panel.SetActive(show);
            if (show && !_hasSnapshot) RebuildFromLocalScan();
        }

        // ---- 本地玩家定位 + ping 自报（战绩面板数据配套；离线安全） ----

        private void UpdateLocalPlayer()
        {
            if (_localPlayer != null && _localPlayer.IsOwnerPlayer && !string.IsNullOrEmpty(_selfId)) return;
            _localPlayer = null;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (player.IsOwnerPlayer) { _localPlayer = player; break; }
            }
            _selfId = _localPlayer != null ? MatchPlayerIdentity.Resolve(_localPlayer) : null;
        }

        private void UpdatePingReport()
        {
            // 低频自报（FishNet 4.7.2 无逐连接 RTT 公开 API；客户端 TimeManager.RoundTripTime = 本端真实 ping）
            if (_localPlayer == null) return;
            if (_localScanTimer > 0f) return;
            _localScanTimer = PingReportIntervalSeconds;
            var timeManager = InstanceFinder.TimeManager;
            long ping = timeManager != null ? timeManager.RoundTripTime : 0;
            _localPlayer.SubmitPingReport(ping);
        }

        // ---- 渲染 ----

        private void UpdateHeader()
        {
            if (_scoreText != null)
            {
                // C3/Q04 TDM：中栏显示团队比分（红蓝 + 目标）；KillRace 保持个人击杀/助攻标题
                _scoreText.text = _currentPayload != null && _currentPayload.IsTeamMode
                    ? $"红 {_currentPayload.redKills} : {_currentPayload.blueKills} 蓝  ·  目标 {_currentPayload.killTarget}"
                    : "PERSONAL KILLS  /  ASSISTS";
            }
            if (_modeText != null)
                _modeText.text = _currentPayload != null && _currentPayload.IsTeamMode ? "TEAM DEATHMATCH" : ModeLabel;
            bool teamMode = _currentPayload != null && _currentPayload.IsTeamMode;
            _ffa.MetaText?.SetTextSafe($"{_entries.Length} PLAYERS");
            _red.MetaText?.SetTextSafe(teamMode ? $"SCORE {_currentPayload.redKills}" : string.Empty);
            _blue.MetaText?.SetTextSafe(teamMode ? $"SCORE {_currentPayload.blueKills}" : string.Empty);
        }

        private void UpdateTimer()
        {
            if (_timerText == null) return;
            if (!_hasSnapshot || _snapshotTimeLeft < 0)
            {
                _timerText.text = UnknownClock;
                return;
            }
            long elapsed = (long)(Time.unscaledTime - _snapshotReceivedUnscaled);
            _timerText.text = MatchScoreboardSnapshot.FormatClock(_snapshotTimeLeft - elapsed);
        }

        private void RebuildRows()
        {
            // Phase 4：TDM → 红/蓝双栏（CF 式）；KillRace/离线 → 单榜。
            // TDM 无队伍（None，极少：旧票据回退）行并入红栏尾部，保证数据可见。
            bool teamMode = _currentPayload != null && _currentPayload.IsTeamMode;
            _ffa.Root.SetActive(!teamMode);
            _red.Root.SetActive(teamMode);
            _blue.Root.SetActive(teamMode);
            if (teamMode)
            {
                RenderEntries(_red, entry =>
                    entry.teamId == MatchRules.TeamRed || entry.teamId == MatchRules.TeamNone);
                RenderEntries(_blue, entry => entry.teamId == MatchRules.TeamBlue);
            }
            else
            {
                RenderEntries(_ffa, null);
            }
        }

        private void RebuildFromLocalScan()
        {
            // 离线/快照前回退：本地扫描（击杀竞赛为联网玩法，离线通常全 0；面板结构保持完整）
            var inputs = new List<MatchScoreboardInput>();
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                string id = MatchPlayerIdentity.Resolve(player);
                long ping = player.IsOwnerPlayer && InstanceFinder.TimeManager != null
                    ? InstanceFinder.TimeManager.RoundTripTime
                    : 0;
                inputs.Add(new MatchScoreboardInput
                {
                    playerId = id,
                    displayName = id,
                    kills = player.Kills,
                    deaths = player.Deaths,
                    assists = player.Assists,
                    pingMs = (int)Mathf.Max(0, ping),
                });
            }
            _entries = MatchScoreboardSnapshot.BuildEntries(inputs);
            _snapshotTimeLeft = -1;
            RebuildRows();
            UpdateHeader();
        }

        private void RenderEntries(ScoreboardBlock block, System.Predicate<MatchScoreboardEntry> filter)
        {
            if (block.Root == null) return;
            var entries = new List<MatchScoreboardEntry>(_entries.Length);
            for (int i = 0; i < _entries.Length; i++)
                if (filter == null || filter(_entries[i])) entries.Add(_entries[i]);

            EnsureRowCount(block, entries.Count);
            for (int i = 0; i < block.Rows.Count; i++)
            {
                var row = block.Rows[i];
                bool used = i < entries.Count;
                row.Root.SetActive(used);
                if (!used) continue;

                var entry = entries[i];
                bool isSelf = !string.IsNullOrEmpty(_selfId) && entry.playerId == _selfId;
                row.Background.color = isSelf ? SelfRow : i % 2 == 1 ? RowAlt : Color.clear;
                row.YouBadge.SetActive(isSelf);
                row.Rank.text = (i + 1).ToString("00");
                // C3/Q04 TDM：队标前缀（红/蓝）+ 队色名字；None 队（KillRace/离线）无前缀
                bool teamMode = _currentPayload != null && _currentPayload.IsTeamMode
                    && !string.IsNullOrEmpty(entry.teamId) && entry.teamId != MatchRules.TeamNone;
                row.Name.text = teamMode ? (entry.teamId == MatchRules.TeamRed ? "红 " : "蓝 ") + entry.displayName : entry.displayName;
                row.Score.text = MatchScoreboardSnapshot.ComputeScore(entry.kills, entry.assists).ToString();
                row.Kills.text = entry.kills.ToString();
                row.Deaths.text = entry.deaths.ToString();
                row.Ratio.text = MatchScoreboardSnapshot.FormatRatio(entry.kills, entry.deaths);
                row.Assists.text = entry.assists.ToString();
                row.Ping.text = entry.pingMs > 0 ? $"{entry.pingMs} ms" : "--";
                // Phase 4：阵亡置灰 + † 后缀（数据仍完整展示；復活后快照刷新自动还原）
                if (entry.isDead)
                {
                    row.Name.text += "  †";
                    row.Name.color = TextDim;
                }
                else
                {
                    row.Name.color = isSelf ? ScoreAccent
                        : teamMode ? (entry.teamId == MatchRules.TeamRed ? TeamRedColor : TeamBlueColor)
                        : TextPrimary;
                }
            }
        }

        private static void EnsureRowCount(ScoreboardBlock block, int count)
        {
            while (block.Rows.Count < count)
            {
                var row = CreateRow(block);
                block.Rows.Add(row);
            }
            for (int i = count; i < block.Rows.Count; i++)
                block.Rows[i].Root.SetActive(false);
        }

        // ---- 纯代码 uGUI 构建（参照 MatchHudView/WeaponHudView 先例） ----

        private static Font BuiltinFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private void Build()
        {
            var rootRect = (RectTransform)transform;
            Stretch(rootRect, 0f, 1f, 0f, 1f);

            _panel = CreateImage("Panel", transform, PanelBg).gameObject;
            Stretch((RectTransform)_panel.transform, 0.13f, 0.87f, 0.15f, 0.85f);
            var panelTransform = _panel.transform;

            // 头部：模式/地图（左） + 个人击杀竞赛（中） + 剩余时间（右）
            var header = CreateImage("Header", panelTransform, HeaderBg);
            Stretch(header.rectTransform, 0f, 1f, 0.84f, 1f);
            _modeText = CreateText("Mode", header.transform, 28, TextPrimary, TextAnchor.MiddleLeft);
            Stretch(_modeText.rectTransform, 0.03f, 0.32f, 0.40f, 0.94f);
            _modeText.fontStyle = FontStyle.Bold;
            _mapText = CreateText("Map", header.transform, 15, TextMuted, TextAnchor.MiddleLeft);
            Stretch(_mapText.rectTransform, 0.03f, 0.32f, 0.06f, 0.38f);
            _scoreText = CreateText("Score", header.transform, 32, TextPrimary, TextAnchor.MiddleCenter);
            Stretch(_scoreText.rectTransform, 0.32f, 0.68f, 0.10f, 0.90f);
            _timerText = CreateText("Timer", header.transform, 32, TextPrimary, TextAnchor.MiddleRight);
            Stretch(_timerText.rectTransform, 0.68f, 0.97f, 0.10f, 0.90f);

            BuildScoreboardBlock(_ffa, panelTransform, "FreeForAll", 0.02f, 0.98f, 0.075f, "FREE-FOR-ALL / KILL RACE");
            // Phase 4：TDM 红/蓝双栏（CF 式左右分栏；非团队模式由 RebuildRows 隐藏）
            BuildScoreboardBlock(_red, panelTransform, "RedTeam", 0.02f, 0.49f, 0.075f, "RED");
            BuildScoreboardBlock(_blue, panelTransform, "BlueTeam", 0.51f, 0.98f, 0.075f, "BLUE");
            _red.Root.SetActive(false);
            _blue.Root.SetActive(false);

            // 屏幕底部低调操作提示（面板外、画布级）
            _hintText = CreateText("Hint", transform, 14, TextDim, TextAnchor.MiddleCenter);
            Stretch(_hintText.rectTransform, 0.35f, 0.65f, 0.045f, 0.075f);
            _hintText.text = HintLabel;

            _panel.SetActive(false);
            _mapText.text = SceneManager.GetActiveScene().name.ToUpperInvariant();
            _modeText.text = ModeLabel;
            UpdateHeader();
            UpdateTimer();
        }

        private static void BuildScoreboardBlock(
            ScoreboardBlock block, Transform parent, string name, float xMin, float xMax, float rowHeight, string title)
        {
            var root = CreateImage(name, parent, BlockBg);
            block.Root = root.gameObject;
            block.RowHeight = rowHeight;
            Stretch(root.rectTransform, xMin, xMax, 0.02f, 0.82f);
            // Phase 4：块根裁剪（RectMask2D）——行数超出可视区的部分不再溢出面板底缘
            root.gameObject.AddComponent<RectMask2D>();
            var blockTransform = root.transform;

            // 左侧细强调条（蓝/红）
            var accent = CreateImage("Accent", blockTransform, block.Accent);
            Stretch(accent.rectTransform, 0f, 0.012f, 0f, 1f);

            block.TitleText = CreateText("Title", blockTransform, 20, block.Accent, TextAnchor.MiddleLeft);
            Stretch(block.TitleText.rectTransform, 0.03f, 0.35f, 0.82f, 0.99f);
            block.TitleText.fontStyle = FontStyle.Bold;
            block.TitleText.text = title;
            block.MetaText = CreateText("Meta", blockTransform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(block.MetaText.rectTransform, 0.40f, 0.98f, 0.82f, 0.99f);

            // 列头（字号小于数据行；数值列右对齐与数据一致）
            CreateColumnHeader(blockTransform, "PLAYER", 0.124f, 0.40f, TextAnchor.MiddleLeft);
            CreateColumnHeader(blockTransform, "SCORE", 0.40f, 0.50f, TextAnchor.MiddleRight);
            CreateColumnHeader(blockTransform, "KILLS", 0.50f, 0.59f, TextAnchor.MiddleRight);
            CreateColumnHeader(blockTransform, "DEATHS", 0.59f, 0.68f, TextAnchor.MiddleRight);
            CreateColumnHeader(blockTransform, "K/D", 0.68f, 0.77f, TextAnchor.MiddleRight);
            CreateColumnHeader(blockTransform, "ASSISTS", 0.77f, 0.86f, TextAnchor.MiddleRight);
            CreateColumnHeader(blockTransform, "PING", 0.86f, 0.995f, TextAnchor.MiddleRight);
        }

        private static void CreateColumnHeader(Transform parent, string label, float xMin, float xMax, TextAnchor anchor)
        {
            var text = CreateText("Col_" + label, parent, 13, TextMuted, anchor);
            Stretch(text.rectTransform, xMin, xMax, 0.655f, 0.775f);
            text.text = label;
        }

        private static RowViews CreateRow(ScoreboardBlock block)
        {
            var row = new RowViews();
            var root = new GameObject("Row", typeof(RectTransform));
            root.transform.SetParent(block.Root.transform, false);
            row.Root = root;

            int index = block.Rows.Count;
            // Phase 4：行高按块参数化（0.122→0.075：TDM 满编 8 行/栏完整可见；FFA 可见 8 行，
            // 超出部分由块根 RectMask2D 裁剪——溢出面板底缘的旧缺陷修复）
            float yMax = 0.62f - index * block.RowHeight;
            float yMin = yMax - block.RowHeight;
            Stretch((RectTransform)root.transform, 0f, 1f, yMin, yMax);

            row.Background = root.AddComponent<Image>();
            row.Background.raycastTarget = false;
            row.Background.color = Color.clear;

            var divider = CreateImage("Divider", root.transform, Divider);
            Stretch(divider.rectTransform, 0f, 1f, 0f, 0.05f);

            row.Rank = CreateText("Rank", root.transform, 14, TextMuted, TextAnchor.MiddleLeft);
            Stretch(row.Rank.rectTransform, 0.005f, 0.042f, 0f, 1f);

            var avatarSlot = CreateImage("AvatarSlot", root.transform, AvatarSlotBg);
            Stretch(avatarSlot.rectTransform, 0.048f, 0.075f, 0.12f, 0.88f);

            row.YouBadge = CreateImage("YouBadge", root.transform, block.Accent).gameObject;
            Stretch((RectTransform)row.YouBadge.transform, 0.080f, 0.118f, 0.25f, 0.75f);
            var badgeText = CreateText("You", row.YouBadge.transform, 11, new Color(0.05f, 0.08f, 0.12f, 1f), TextAnchor.MiddleCenter);
            Stretch(badgeText.rectTransform, 0f, 1f, 0f, 1f);
            badgeText.text = "YOU";
            row.YouBadge.SetActive(false);

            row.Name = CreateText("Name", root.transform, 17, TextPrimary, TextAnchor.MiddleLeft);
            Stretch(row.Name.rectTransform, 0.124f, 0.40f, 0f, 1f);
            row.Score = CreateText("Score", root.transform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(row.Score.rectTransform, 0.40f, 0.50f, 0f, 1f);
            row.Kills = CreateText("Kills", root.transform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(row.Kills.rectTransform, 0.50f, 0.59f, 0f, 1f);
            row.Deaths = CreateText("Deaths", root.transform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(row.Deaths.rectTransform, 0.59f, 0.68f, 0f, 1f);
            row.Ratio = CreateText("Ratio", root.transform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(row.Ratio.rectTransform, 0.68f, 0.77f, 0f, 1f);
            row.Assists = CreateText("Assists", root.transform, 16, TextPrimary, TextAnchor.MiddleRight);
            Stretch(row.Assists.rectTransform, 0.77f, 0.86f, 0f, 1f);
            row.Ping = CreateText("Ping", root.transform, 16, TextMuted, TextAnchor.MiddleRight);
            Stretch(row.Ping.rectTransform, 0.86f, 0.995f, 0f, 1f);
            return row;
        }

        // ---- 共享构建工具（与 MatchHudView 同构） ----

        private static Text CreateText(string name, Transform parent, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = BuiltinFont;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
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

        private static void Stretch(RectTransform rt, float xMin, float xMax, float yMin, float yMax)
        {
            rt.anchorMin = new Vector2(xMin, yMin);
            rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    /// <summary>Text 空值安全写入（View 内局部便利）。</summary>
    internal static class MatchScoreboardViewExtensions
    {
        public static void SetTextSafe(this Text text, string value)
        {
            if (text != null) text.text = value;
        }
    }
}
