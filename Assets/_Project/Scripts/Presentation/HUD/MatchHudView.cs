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
        private Text _scoreCaption;
        private TMP_Text _killFeedText;
        private TMP_Text _centerNotice;
        private TMP_Text _aimedPlayerText;
        private readonly RaycastHit[] _aimHits = new RaycastHit[32];
        private float _centerNoticeUntil;
        private readonly System.Collections.Generic.HashSet<string> _seenCombatEvents = new();
        private Image _healthFill;
        private TMP_Text _healthText;
        private Image _healthTrail;
        private float _displayHealth = 1f;
        private float _damageTrail = 1f;

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
            root.AddComponent<EnemyOutlinePresenter>();
            // Tab 战绩面板（同画布独立组件；旧内置两行板已由其取代）
            MatchScoreboardView.TryMount(canvas);
            // 复活/出生保护条（Phase 3：同画布独立组件，单一三态机）
            RespawnProtectionHudView.TryMount(canvas);
        }

        private void OnEnable() => NetworkCombatAuthority.OnMatchEvent += HandleMatchEvent;
        private void OnDisable() => NetworkCombatAuthority.OnMatchEvent -= HandleMatchEvent;

        private void HandleMatchEvent(MatchEventKind kind, string payload)
        {
            ResolvePlayers();
            if (kind == MatchEventKind.Assist)
            {
                var assist = JsonUtility.FromJson<MatchLifecycle.MatchAssistPayload>(payload);
                string key = $"A/{assist.matchId}/{assist.assistantId}/{assist.victimName}/{assist.victimLifeEpoch}";
                if (!_seenCombatEvents.Add(key)) return;
                if (_local != null && assist.assistantId == MatchLifecycle.PlayerId(_local))
                    ShowCenterNotice($"助攻  {assist.victimName}", new Color32(112, 168, 255, 255));
                return;
            }
            if (kind != MatchEventKind.Kill) return;
            var kill = JsonUtility.FromJson<MatchLifecycle.MatchKillPayload>(payload);
            string eventKey = $"K/{kill.matchId}/{kill.killerId}/{kill.victimId}/{kill.victimLifeEpoch}";
            if (!_seenCombatEvents.Add(eventKey)) return;
            if (_seenCombatEvents.Count > 256) _seenCombatEvents.Clear();
            string weapon = string.IsNullOrWhiteSpace(kill.weaponName) ? kill.weaponId : kill.weaponName;
            _feedLines.Add($"{kill.killerName}  [{weapon}]  {kill.victimName}{(kill.headshot ? "  ◆" : "")}");
            _feedTimes.Add(Time.unscaledTime);
            while (_feedLines.Count > 4)
            {
                _feedLines.RemoveAt(0);
                _feedTimes.RemoveAt(0);
            }
            if (_killFeedText != null) _killFeedText.text = string.Join("\n", _feedLines);
            if (_local != null && kill.killerId == MatchLifecycle.PlayerId(_local)
                && kill.killerLifeEpoch >= _local.LifeEpochForPresentation)
                ShowCenterNotice($"击杀  {kill.victimName}{(kill.headshot ? "  ◆ 爆头" : "")}",
                    kill.headshot ? new Color32(255, 185, 78, 255) : Color.white);
        }

        private void ShowCenterNotice(string content, Color color)
        {
            if (_centerNotice == null) return;
            _centerNotice.text = content;
            _centerNotice.color = color;
            _centerNoticeUntil = Time.unscaledTime + 1f;
        }

        private void Update()
        {
            ResolvePlayers();
            UpdateScore();
            UpdateHealth();
            UpdateFeed();
            UpdateAimedPlayerId();
            if (_centerNotice != null && _centerNotice.gameObject.activeSelf != (Time.unscaledTime < _centerNoticeUntil))
                _centerNotice.gameObject.SetActive(Time.unscaledTime < _centerNoticeUntil);
        }

        private void UpdateAimedPlayerId()
        {
            if (_aimedPlayerText == null) return;
            _aimedPlayerText.gameObject.SetActive(false);
            if (_local == null || _local.IsDead) return;
            var camera = UnityEngine.Camera.main;
            if (camera == null) return;
            Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0f));
            int count = Physics.RaycastNonAlloc(ray, _aimHits, 100f, ~0, QueryTriggerInteraction.Collide);
            int nearest = -1;
            for (int i = 0; i < count; i++)
            {
                var collider = _aimHits[i].collider;
                if (collider == null || collider.GetComponentInParent<NetworkCombatAuthority>() == _local) continue;
                if (nearest < 0 || _aimHits[i].distance < _aimHits[nearest].distance) nearest = i;
            }
            if (nearest < 0) return;
            var player = _aimHits[nearest].collider.GetComponentInParent<NetworkCombatAuthority>();
            if (player == null || player.IsDead) return; // nearest wall means no identity leak
            bool friendly = MatchLifecycle.CurrentMode == MatchRules.ModeTdm
                && _local.TeamId != MatchRules.TeamNone && player.TeamId == _local.TeamId;
            string name = player.DisplayName;
            _aimedPlayerText.text = !string.IsNullOrWhiteSpace(name)
                ? name : MatchScoreboardView.DisplayNameFor(MatchLifecycle.PlayerId(player));
            _aimedPlayerText.color = friendly ? new Color32(112, 168, 255, 255)
                : new Color32(235, 83, 83, 255);
            _aimedPlayerText.gameObject.SetActive(true);
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
            if (MatchScoreboardView.TryGetTeamScores(out int red, out int blue, out int timeLeft,
                    out int target))
            {
                string timer = timeLeft >= 0 ? $"{timeLeft / 60:00}:{timeLeft % 60:00}" : "--:--";
                _scoreText.text = $"<color=#F06B6B>红队 {red:00}</color>     <b>{timer}</b>     "
                    + $"<color=#70A8FF>蓝队 {blue:00}</color>";
                if (_scoreCaption != null) _scoreCaption.text = $"团队竞技  ·  目标 {target}";
                return;
            }
            int mine = _local != null ? _local.Kills : 0;
            int theirs = _opponent != null ? _opponent.Kills : 0;
            _scoreText.text = $"<color=#F5F7FA>你 {mine:00}</color>     <b>VS</b>     <color=#FF9C8F>对手 {theirs:00}</color>";
            if (_scoreCaption != null) _scoreCaption.text = $"击杀竞赛  ·  目标 {MatchRules.TargetKills}";
        }

        private void UpdateHealth()
        {
            if (_healthFill == null || _healthText == null) return;
            int hp = _local != null ? _local.Health : 0;
            // 服务器权威 HP（G3 出口）；满值 100 与 DamageableTarget 默认 maxHealth 对齐（显示用）
            float fraction = Mathf.Clamp01(hp / 100f);
            _displayHealth = Mathf.MoveTowards(_displayHealth, fraction, Time.unscaledDeltaTime * 3f);
            _damageTrail = fraction >= _damageTrail ? fraction : Mathf.MoveTowards(_damageTrail, fraction, Time.unscaledDeltaTime * 0.3f);
            _healthFill.rectTransform.anchorMax = new Vector2(_displayHealth, 1f);
            if (_healthTrail != null) _healthTrail.rectTransform.anchorMax = new Vector2(_damageTrail, 1f);
            // 2026-09-18 问题9：阈值变色（>50 主题绿 / 26-50 琥珀 / ≤25 红），数字满血保持主文本色
            var color = hp > 50 ? HpHealthy : hp > 25 ? HpWarn : HpDanger;
            _healthFill.color = color;
            _healthText.text = $"<b>{hp:000}</b><size=18><color=#9AA7B8> / 100</color></size>";
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

            // Compact match card: a legible score, clock and target with a clear team accent.
            var scorePanel = CreateImage("ScorePanel", rootRect, new Color(0.035f, .075f, .085f, .82f));
            Stretch(scorePanel.rectTransform, .32f, .68f, .916f, .989f);
            var redAccent = CreateImage("RedAccent", scorePanel.transform, new Color32(240, 107, 107, 255));
            Stretch(redAccent.rectTransform, 0f, .008f, 0f, 1f);
            var blueAccent = CreateImage("BlueAccent", scorePanel.transform, new Color32(112, 168, 255, 255));
            Stretch(blueAccent.rectTransform, .992f, 1f, 0f, 1f);
            var score = CreateText("ScoreBar", scorePanel.transform, 23, Color.white, TextAnchor.MiddleCenter);
            Stretch(score.rectTransform, .02f, .98f, .34f, 1f);
            _scoreText = score;
            _scoreCaption = CreateText("ScoreCaption", scorePanel.transform, 13,
                new Color32(163, 185, 191, 255), TextAnchor.MiddleCenter);
            Stretch(_scoreCaption.rectTransform, .02f, .98f, .05f, .37f);

            // kill feed（比分条下方，右对齐）
            var feed = CreateTmpText("KillFeed", rootRect, string.Empty, 18,
                new Color(1f, 0.85f, 0.4f), TextAlignmentOptions.TopRight);
            Stretch(feed.rectTransform, .67f, .96f, .72f, .91f);
            _killFeedText = feed;
            _killFeedText.text = string.Empty;
            _centerNotice = CreateTmpText("CombatNotice", rootRect, string.Empty, 19,
                Color.white, TextAlignmentOptions.Center);
            Stretch(_centerNotice.rectTransform, .34f, .66f, .42f, .47f);
            _centerNotice.gameObject.SetActive(false);
            _aimedPlayerText = CreateTmpText("AimedPlayerId", rootRect, string.Empty, 16,
                Color.white, TextAlignmentOptions.Center);
            Stretch(_aimedPlayerText.rectTransform, .39f, .61f, .365f, .405f);
            _aimedPlayerText.gameObject.SetActive(false);

            // 血量（2026-09-18 实机问题9，主流 FPS 简洁风：左下半透 chip 内大数字+细条+阈值变色；
            // 原"顶中黑底绿条 HP 100"下线——顶部只留比赛状态条）
            // 第三轮：底/左边距抬高一点，避免非 16:9 窗口下贴住屏幕边被切
            var hpChip = new GameObject("HealthCluster", typeof(RectTransform));
            hpChip.transform.SetParent(rootRect, false);
            Stretch((RectTransform)hpChip.transform, 0.025f, 0.225f, 0.04f, 0.145f);
            var accent = CreateImage("HealthAccent", hpChip.transform, new Color32(62, 216, 186, 255));
            Stretch(accent.rectTransform, 0f, 0.012f, 0.12f, 0.9f);
            var label = CreateTmpText("HealthLabel", hpChip.transform, "+  VITALS", 13, new Color32(163, 205, 200, 255), TextAlignmentOptions.MidlineLeft);
            Stretch(label.rectTransform, 0.05f, 1f, 0.78f, 1f);
            _healthText = CreateTmpText("HpNumber", hpChip.transform, "100", 42, TextPrimary, TextAlignmentOptions.MidlineLeft);
            Stretch(_healthText.rectTransform, 0.05f, 1f, 0.22f, 0.82f);
            var hpBarBack = CreateImage("HpBarBack", hpChip.transform, new Color(0.04f, 0.09f, 0.1f, 0.7f));
            Stretch(hpBarBack.rectTransform, 0.05f, 1f, 0.07f, 0.18f);
            _healthTrail = CreateImage("DamageTrail", hpBarBack.transform, HpWarn);
            Stretch(_healthTrail.rectTransform, 0f, 1f, 0f, 1f);
            _healthFill = CreateImage("HpFill", hpBarBack.transform, new Color32(62, 216, 186, 255));
            Stretch(_healthFill.rectTransform, 0f, 1f, 0f, 1f);
            for (int i = 1; i < 4; i++)
            {
                var tick = CreateImage("HealthSegment" + i, hpBarBack.transform, new Color(0.04f, 0.09f, 0.1f, 0.8f));
                Stretch(tick.rectTransform, i * 0.25f - 0.004f, i * 0.25f + 0.004f, 0f, 1f);
            }
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
