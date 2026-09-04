using Game.Gameplay.Network;
using UnityEngine;
using UnityEngine.InputSystem;
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
        private Text _boardText;
        private GameObject _boardPanel;
        private Image _healthFill;
        private Text _healthText;

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
            UpdateBoard();
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
            _healthText.text = $"HP {hp}";
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

        private void UpdateBoard()
        {
            var kb = Keyboard.current;
            bool showTab = kb != null && kb.tabKey.isPressed;
            if (_boardPanel.activeSelf != showTab) _boardPanel.SetActive(showTab);
            if (showTab && _boardText != null)
                _boardText.text = $"YOU    K {_local?.Kills ?? 0:00}   D {_local?.Deaths ?? 0:00}\nENEMY  K {_opponent?.Kills ?? 0:00}   D {_opponent?.Deaths ?? 0:00}";
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

            // 顶部比分常驻条
            var score = CreateText("ScoreBar", rootRect, 26, Color.white, TextAnchor.MiddleCenter);
            Stretch(score.rectTransform, 0.25f, 0.75f, 0.94f, 0.985f);
            _scoreText = score;

            // kill feed（比分条下方，右对齐）
            var feed = CreateText("KillFeed", rootRect, 18, new Color(1f, 0.85f, 0.4f), TextAnchor.UpperRight);
            Stretch(feed.rectTransform, 0.55f, 0.98f, 0.72f, 0.93f);
            _killFeedText = feed;
            _killFeedText.text = string.Empty;

            // 血条（底部居中：底槽 + 填充）
            var hpBack = CreateImage("HpBack", rootRect, new Color(0f, 0f, 0f, 0.55f));
            Stretch(hpBack.rectTransform, 0.40f, 0.60f, 0.905f, 0.935f);
            var hpFillGo = new GameObject("HpFill", typeof(RectTransform), typeof(Image));
            hpFillGo.transform.SetParent(hpBack.transform, false);
            _healthFill = hpFillGo.GetComponent<Image>();
            _healthFill.color = new Color(0.3f, 0.85f, 0.3f);
            Stretch((RectTransform)_healthFill.transform, 0f, 1f, 0f, 1f);
            _healthFill.type = Image.Type.Filled;
            _healthFill.fillMethod = Image.FillMethod.Horizontal;
            var hpText = CreateText("HpText", rootRect, 15, Color.white, TextAnchor.MiddleCenter);
            Stretch(hpText.rectTransform, 0.40f, 0.60f, 0.905f, 0.935f);
            _healthText = hpText;

            // Tab 按住比分板（居中面板，默认隐藏）
            var board = CreateImage("Board", rootRect, new Color(0f, 0f, 0f, 0.72f));
            Stretch(board.rectTransform, 0.32f, 0.68f, 0.32f, 0.62f);
            _boardPanel = board.gameObject;
            _boardPanel.SetActive(false);
            var boardText = CreateText("BoardText", _boardPanel.transform, 24, Color.white, TextAnchor.MiddleCenter);
            Stretch(boardText.rectTransform, 0.05f, 0.95f, 0.05f, 0.95f);
            _boardText = boardText;
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
