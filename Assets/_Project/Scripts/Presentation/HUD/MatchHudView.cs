using Game.Gameplay.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 击杀竞赛 HUD（Docs/23 P1-6，G4）：纯代码 uGUI 运行时自挂载（零资产改动）。
    /// 挂载点 = WeaponHudView 同画布（场景锚点先例）；内容全部代码构建；
    /// 只读网络同步状态（比分/血量 SyncVar + OnMatchEvent），本地路径零侵入。
    /// 挂载入口 TryMount 由 NetworkCombatAuthority.OnStartClient 经反射调用
    /// （Gameplay 禁止引用 Presentation，反射按名调用为本项目既有惯例）。
    /// </summary>
    public sealed class MatchHudView : MonoBehaviour
    {
        private Text _scoreText;
        private Text _killFeedText;
        private Image _healthFill;
        private TMP_Text _healthText;

        // 2026-09-18 实机问题9（主流 FPS 简洁风）：主题令牌同源镜像（Presentation 不引 Game.UI，按值同源）
        private static readonly Color TextPrimary = new Color32(0xF5, 0xF7, 0xFA, 0xFF);
        private static readonly Color HpHealthy = new Color32(0x6B, 0xCB, 0x77, 0xFF);
        private static readonly Color HpWarn = new Color32(0xFF, 0xA4, 0x1B, 0xFF);
        private static readonly Color HpDanger = new Color32(0xFF, 0x6B, 0x6B, 0xFF);
        private static TMP_FontAsset _hudFont;
        private static TMP_FontAsset HudFont
        {
            get
            {
                if (_hudFont == null)
                {
                    _hudFont = Resources.Load<TMP_FontAsset>("Fonts/NotoSansSC-Regular SDF");
                    if (_hudFont == null) _hudFont = TMP_Settings.defaultFontAsset;
                }
                return _hudFont;
            }
        }

        private readonly System.Collections.Generic.List<string> _feedLines = new();
        private readonly System.Collections.Generic.List<float> _feedTimes = new();
        private NetworkCombatAuthority _local;
        private NetworkCombatAuthority _opponent;

        /// <summary>挂载入口（反射调用点，签名不可改）：幂等；锚点 = WeaponHudView 所在画布。</summary>
        public static void TryMount()
        {
            if (FindFirstObjectByType<MatchHudView>() != null) return;
            var anchor = FindFirstObjectByType<WeaponHudView>();
            Canvas canvas = anchor != null ? anchor.GetComponentInParent<Canvas>() : null;
            if (canvas == null)
            {
                Debug.LogWarning("[MatchHudView] 未找到 WeaponHudView 画布锚点，比赛 HUD 未挂载");
                return;
            }
            var root = new GameObject("MatchHud", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            var view = root.AddComponent<MatchHudView>();
            view.Build();
            // Tab 战绩面板（同画布独立组件；旧内置两行板已由其取代）
            MatchScoreboardView.TryMount(canvas);
            // 复活/出生保护条（Phase 3：同画布独立组件，单一三态机）
            RespawnProtectionHudView.TryMount(canvas);
        }

        private void OnEnable() => NetworkCombatAuthority.OnMatchEvent += HandleMatchEvent;
        private void OnDisable() => NetworkCombatAuthority.OnMatchEvent -= HandleMatchEvent;

        private void HandleMatchEvent(MatchEventKind kind, string payload)
        {
            if (kind != MatchEventKind.Kill) return;
            // kill feed：最多显示 4 条、5 秒后移除（简式；不做 alpha 渐隐，见执行报告）
            var kill = JsonUtility.FromJson<MatchLifecycle.MatchKillPayload>(payload);
            _feedLines.Add($"KILL  {kill?.killerId ?? "?"}  >>  {kill?.victimId ?? "?"}");
            _feedTimes.Add(Time.unscaledTime);
            while (_feedLines.Count > 4)
            {
                _feedLines.RemoveAt(0);
                _feedTimes.RemoveAt(0);
            }
            if (_killFeedText != null) _killFeedText.text = string.Join("\n", _feedLines);
        }

        private void Update()
        {
            ResolvePlayers();
            UpdateScore();
            UpdateHealth();
            UpdateFeed();
        }

        private void ResolvePlayers()
        {
            if (_local == null || _opponent == null)
            {
                _local = null;
                _opponent = null;
                foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                {
                    if (player.IsOwnerPlayer) _local = player;
                    else if (_opponent == null) _opponent = player;
                }
            }
        }

        private void UpdateScore()
        {
            if (_scoreText == null) return;
            // Docs/26 §2.4 TDM：常驻 HUD 展示红蓝总分/目标/倒计时（服务器快照权威聚合；
            // 离线或非团队模式回退既有个人比分行）
            if (MatchScoreboardView.TryGetTeamScores(out int red, out int blue, out int timeLeft))
            {
                string timer = timeLeft >= 0 ? $"   {timeLeft / 60:00}:{timeLeft % 60:00}" : string.Empty;
                _scoreText.text = $"<color=#F06B6B>RED {red:00}</color>   / {MatchRules.TargetKills} /   "
                    + $"<color=#70A8FF>BLUE {blue:00}</color>{timer}";
                return;
            }
            int mine = _local != null ? _local.Kills : 0;
            int theirs = _opponent != null ? _opponent.Kills : 0;
            _scoreText.text = $"YOU {mine:00}   / {MatchRules.TargetKills} /   {theirs:00} ENEMY";
        }

        private void UpdateHealth()
        {
            if (_healthFill == null || _healthText == null) return;
            int hp = _local != null ? _local.Health : 0;
            // 服务器权威 HP（G3 出口）；满值 100 与 DamageableTarget 默认 maxHealth 对齐（显示用）
            _healthFill.fillAmount = Mathf.Clamp01(hp / 100f);
            // 2026-09-18 问题9：阈值变色（>50 主题绿 / 26-50 琥珀 / ≤25 红），数字满血保持主文本色
            var color = hp > 50 ? HpHealthy : hp > 25 ? HpWarn : HpDanger;
            _healthFill.color = color;
            _healthText.text = hp.ToString();
            _healthText.color = hp > 50 ? TextPrimary : color;
        }

        private void UpdateFeed()
        {
            if (_feedLines.Count == 0) return;
            // 5 秒过期（从最旧端弹出）
            bool expired = Time.unscaledTime - _feedTimes[0] > 5f;
            if (expired)
            {
                _feedLines.RemoveAt(0);
                _feedTimes.RemoveAt(0);
                if (_killFeedText != null) _killFeedText.text = string.Join("\n", _feedLines);
            }
        }

        // ---- 纯代码 uGUI 构建（参照 WeaponHudView 先例：内置 ugui + 英文文案） ----

        private static Font BuiltinFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private void Build()
        {
            var rootRect = (RectTransform)transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // 2026-09-18 实机（00:55）排查"不同窗口尺寸下底部 UI 被切/挤到边"：
            // 场景里游戏 HUD 的 Canvas 是 matchWidthOrHeight=0（纯按宽缩放，Arena.unity:4007），
            // 而本项目所有运行时建的 Canvas 都是 0.5（UIComponents / LobbyViewFactory）。
            // 两套基准并存 → 非 16:9 窗口下按 y 分数布局的条带整体漂移、贴边元素被切。
            // 这里只在"已经是 Scale With Screen Size"时把基准对齐到 0.5，不改缩放模式本身。
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
                    scaler.matchWidthOrHeight = 0.5f;
            }

            // 顶部比分常驻条
            var score = CreateText("ScoreBar", rootRect, 26, Color.white, TextAnchor.MiddleCenter);
            Stretch(score.rectTransform, 0.25f, 0.75f, 0.94f, 0.985f);
            _scoreText = score;

            // kill feed（比分条下方，右对齐）
            var feed = CreateText("KillFeed", rootRect, 18, new Color(1f, 0.85f, 0.4f), TextAnchor.UpperRight);
            Stretch(feed.rectTransform, 0.55f, 0.98f, 0.72f, 0.93f);
            _killFeedText = feed;
            _killFeedText.text = string.Empty;

            // 血量（2026-09-18 实机问题9，主流 FPS 简洁风：左下半透 chip 内大数字+细条+阈值变色；
            // 原"顶中黑底绿条 HP 100"下线——顶部只留比赛状态条）
            // 第三轮：底/左边距抬高一点，避免非 16:9 窗口下贴住屏幕边被切
            var hpChip = CreateImage("HpChip", rootRect, new Color(0f, 0f, 0f, 0.45f));
            Stretch(hpChip.rectTransform, 0.03f, 0.27f, 0.045f, 0.12f);
            var hpText = CreateTmpText("HpNumber", hpChip.transform, "100", 44, TextPrimary,
                TextAlignmentOptions.MidlineLeft);
            Stretch(hpText.rectTransform, 0.05f, 0.44f, 0f, 1f);
            _healthText = hpText;
            var hpBarBack = CreateImage("HpBarBack", hpChip.transform, new Color(1f, 1f, 1f, 0.14f));
            Stretch(hpBarBack.rectTransform, 0.48f, 0.94f, 0.40f, 0.60f);
            var hpFillGo = new GameObject("HpFill", typeof(RectTransform), typeof(Image));
            hpFillGo.transform.SetParent(hpBarBack.transform, false);
            _healthFill = hpFillGo.GetComponent<Image>();
            _healthFill.color = HpHealthy;
            Stretch((RectTransform)_healthFill.transform, 0f, 1f, 0f, 1f);
            _healthFill.type = Image.Type.Filled;
            _healthFill.fillMethod = Image.FillMethod.Horizontal;
        }

        private static TMP_Text CreateTmpText(string name, Transform parent, string value, int size,
            Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (HudFont != null) text.font = HudFont;
            text.fontSize = size;
            text.color = color;
            text.alignment = align;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

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
}
