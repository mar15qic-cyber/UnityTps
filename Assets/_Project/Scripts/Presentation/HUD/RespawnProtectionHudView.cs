using Game.Gameplay.Network;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 复活/出生保护统一 HUD（Phase 3，四组需求 1.1/1.3）：单一组件三态机，避免双 UI 互踩。
    /// Hidden（默认）→ Respawning（死亡后灰白进度条）→ 服务器重生 → Invincible（金黄进度条）→ Hidden。
    /// 数据源 = 本地 Owner 的 NetworkCombatAuthority 服务器权威 SyncVar（RespawnAtTick/InvincibleUntilTick，
    /// 协议 v5）；客户端只做剩余秒换算显示，不做任何状态决策（服务器权威）。
    /// 剩余秒 =（deadline − 本地 TimeManager.Tick)×tickDelta，存在 ≈RTT 级显示偏差（可接受）；
    /// 到点后本地钳制立即 Hidden，不等下一次快照。
    /// 挂载 = MatchHudView.TryMount 链（同画布独立组件）；不响应 GameplayInputGate（保护信息常驻，
    /// CF 语义：菜单/聊天打开期间进度条不隐藏）。
    /// 文案沿用 HUD 英文约定（WeaponHudView/MatchHudView/MatchScoreboardView 全英文；内置
    /// LegacyRuntime.ttf 无 CJK 字形，中文会渲染为方块）。
    /// </summary>
    public sealed class RespawnProtectionHudView : MonoBehaviour
    {
        /// <summary>HUD 三态（internal 供 EditMode 纯函数断言）。</summary>
        internal enum Phase { Hidden, Respawning, Invincible }

        private GameObject _panel;
        private Image _back;
        private Image _fill;
        private Text _label;
        private NetworkCombatAuthority _local;
        private Phase _phase = Phase.Hidden;
        private FishNet.Managing.Timing.TimeManager _timeManager;

        // 视觉常量（HUD 内联色惯例，同 MatchScoreboardView）：灰白=复活、金黄=出生保护
        private static readonly Color BackColor = new(0f, 0f, 0f, 0.55f);
        private static readonly Color RespawningColor = new(0.85f, 0.87f, 0.90f, 0.95f);
        private static readonly Color InvincibleColor = new(1f, 0.80f, 0.25f, 0.98f);

        /// <summary>挂载入口（MatchHudView.TryMount 链调用）：幂等；锚点 = 战斗画布。</summary>
        public static void TryMount(Canvas canvas)
        {
            if (canvas == null) return;
            if (FindFirstObjectByType<RespawnProtectionHudView>() != null) return;
            var root = new GameObject("RespawnProtectionHud", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            var view = root.AddComponent<RespawnProtectionHudView>();
            view.Build();
        }

        private void OnEnable()
        {
            _timeManager = FishNet.InstanceFinder.TimeManager;
            if (_panel != null) _panel.SetActive(false);
        }

        private void Update()
        {
            ResolveLocal();
            int tickRate = _timeManager != null ? (int)_timeManager.TickRate : MatchRules.DefaultTickRate;
            uint now = _timeManager != null ? _timeManager.Tick : 0u;

            bool isDead = _local != null && _local.IsDead;
            uint respawnAtTick = _local != null ? _local.RespawnAtTick : 0u;
            uint invincibleUntilTick = _local != null ? _local.InvincibleUntilTick : 0u;

            var phase = ComputePhase(isDead, respawnAtTick, invincibleUntilTick, now, tickRate,
                out float remainingSeconds, out float fillAmount);
            ApplyPhase(phase, remainingSeconds, fillAmount);
        }

        /// <summary>
        /// 三态判定（internal 纯函数，EditMode 直驱）：死亡且已排队 → Respawning；
        /// 存活且保护窗未到期 → Invincible；其余 Hidden。剩余秒/填充量均本地钳制非负。
        /// 优先级：死亡展示恒优先于保护展示（重生瞬间两端状态可能短暂并存）。
        /// </summary>
        internal static Phase ComputePhase(bool isDead, uint respawnAtTick, uint invincibleUntilTick, uint now,
            int tickRate, out float remainingSeconds, out float fillAmount)
        {
            remainingSeconds = 0f;
            fillAmount = 0f;
            float tickDelta = MatchRules.TicksToSeconds(1u, tickRate);

            if (isDead && respawnAtTick != 0u)
            {
                float total = MatchRules.TicksToSeconds(
                    MatchRules.SecondsToTicks(MatchRules.RespawnDelaySeconds, tickRate), tickRate);
                remainingSeconds = TicksRemaining(respawnAtTick, now, tickDelta);
                fillAmount = total > 0f ? Mathf.Clamp01(remainingSeconds / total) : 0f;
                return Phase.Respawning;
            }
            if (!isDead && invincibleUntilTick != 0u && now < invincibleUntilTick)
            {
                float total = MatchRules.TicksToSeconds(
                    MatchRules.SecondsToTicks(MatchRules.SpawnProtectionSeconds, tickRate), tickRate);
                remainingSeconds = TicksRemaining(invincibleUntilTick, now, tickDelta);
                fillAmount = total > 0f ? Mathf.Clamp01(remainingSeconds / total) : 0f;
                return Phase.Invincible;
            }
            return Phase.Hidden;
        }

        /// <summary>targetTick − now 的有符号差值（防 uint 回绕：本地 tick 已越过 deadline 时钳 0）。</summary>
        private static float TicksRemaining(uint targetTick, uint now, float tickDelta)
        {
            long diff = (long)targetTick - now;
            return diff > 0 ? diff * tickDelta : 0f;
        }

        private void ResolveLocal()
        {
            if (_local != null) return;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (player.IsOwnerPlayer)
                {
                    _local = player;
                    return;
                }
            }
        }

        private void ApplyPhase(Phase phase, float remainingSeconds, float fillAmount)
        {
            if (phase == _phase && phase != Phase.Hidden && _fill != null)
            {
                // 同态刷新：仅更新进度与文本，避免每帧 SetActive
                _fill.fillAmount = fillAmount;
                _label.text = FormatLabel(phase, remainingSeconds);
                return;
            }
            if (phase == Phase.Hidden)
            {
                if (_panel.activeSelf) _panel.SetActive(false);
                _phase = phase;
                return;
            }
            if (!_panel.activeSelf) _panel.SetActive(true);
            bool respawning = phase == Phase.Respawning;
            _fill.color = respawning ? RespawningColor : InvincibleColor;
            _fill.fillAmount = fillAmount;
            _label.text = FormatLabel(phase, remainingSeconds);
            _phase = phase;
        }

        private static string FormatLabel(Phase phase, float remainingSeconds)
        {
            return phase == Phase.Respawning ? $"RESPAWNING {remainingSeconds:F1}s"
                : phase == Phase.Invincible ? $"SPAWN PROTECTION {remainingSeconds:F1}s"
                : string.Empty;
        }

        // ---- 纯代码 uGUI 构建（MatchHudView 同构：底部居中横条带） ----

        private static Font BuiltinFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private void Build()
        {
            var rootRect = (RectTransform)transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // 屏幕下方横条带（血条/比分条的对面侧；Scoreboard 锚区 0.15~0.85 之外，不重叠）
            _panel = new GameObject("Band", typeof(RectTransform));
            _panel.transform.SetParent(rootRect, false);
            var bandRect = (RectTransform)_panel.transform;
            bandRect.anchorMin = new Vector2(0.35f, 0.10f);
            bandRect.anchorMax = new Vector2(0.65f, 0.16f);
            bandRect.offsetMin = Vector2.zero;
            bandRect.offsetMax = Vector2.zero;
            _panel.SetActive(false);

            _back = CreateImage("Back", _panel.transform, BackColor);
            Stretch(_back.rectTransform, 0f, 1f, 0f, 1f);

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(_panel.transform, false);
            _fill = fillGo.GetComponent<Image>();
            _fill.color = RespawningColor;
            _fill.raycastTarget = false;
            Stretch(_fill.rectTransform, 0f, 1f, 0f, 1f);
            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Horizontal;

            _label = CreateText("Label", _panel.transform, 18, Color.white, TextAnchor.MiddleCenter);
            Stretch(_label.rectTransform, 0f, 1f, 0f, 1f);
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
