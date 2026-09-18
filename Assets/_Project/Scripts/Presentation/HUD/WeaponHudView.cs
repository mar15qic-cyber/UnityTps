using Game.Gameplay.Weapon;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 武器 HUD 总控（Docs/13 检查点 5，§9-12 拍板：弹药与操作提示随准心一并迁出 OnGUI）：
    /// 订阅 WeaponController/Arsenal 事件 → 只写 uGUI Text（TMP 包未装，文本全英文用内置
    /// ugui 足够；Canvas 层级由编辑器脚本 CP5_HudBuilder 一次性构建）。
    /// 2026-09-18 实机问题9：弹药/武器名升级为运行时 TMP 簇（右下大弹药计数+武器名，
    /// 主流 FPS 简洁风，覆盖全部地图场景内建旧版文本——旧对象仅禁用不删，随时可回退）。
    /// </summary>
    public sealed class WeaponHudView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Text ammoText;
        [SerializeField] private Text weaponText;
        [SerializeField] private Text hintText;

        // Docs/23 P0-3（G1c）：本地玩家（Owner）的网络武器状态——在线时弹药显示读服务器权威 SyncVar
        private Game.Gameplay.Network.NetworkWeaponState _netWeaponState;
        private float _netScanTimer;

        // 2026-09-18 问题9：运行时 TMP 弹药簇（主题令牌同源镜像；Presentation 不引 Game.UI）
        private TMP_Text _ammoLine;
        private TMP_Text _weaponNameText;
        private TMP_Text _buildLabel;
        private static readonly Color TextMuted = new Color32(0x9A, 0xA7, 0xB8, 0xFF);
        private static readonly Color AccentWarning = new Color32(0xFF, 0xA4, 0x1B, 0xFF);
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

        private void Awake()
        {
            if (controller == null) controller = FindObjectOfType<WeaponController>();
            if (arsenal == null) arsenal = FindObjectOfType<Arsenal>();
            EnsureRuntimeCluster();
            EnsureBuildLabel();
        }

        /// <summary>右下弹药簇（2026-09-18 问题9）：武器名（小字 muted）+ 大弹药计数（弹匣大号加粗 /
        /// 备弹小号 muted / 换弹进度琥珀色）。运行时构建=所有地图共用；场景内建旧版文本仅禁用。</summary>
        private void EnsureRuntimeCluster()
        {
            if (_ammoLine != null) return;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var cluster = new GameObject("WeaponHudCluster", typeof(RectTransform));
            cluster.transform.SetParent(canvas.transform, false);
            var clusterRect = (RectTransform)cluster.transform;
            Stretch(clusterRect, 0.74f, 0.985f, 0.02f, 0.135f);

            _weaponNameText = CreateTmpText("WeaponName", cluster.transform, "", 18, TextMuted,
                TextAlignmentOptions.BottomRight);
            Stretch(_weaponNameText.rectTransform, 0f, 1f, 0.66f, 1f);
            _ammoLine = CreateTmpText("AmmoLine", cluster.transform, "", 46, Color.white,
                TextAlignmentOptions.BottomRight);
            // 右边界留 6% 内缩：贴边时 TMP 的最后一字形会被画布边缘切掉（实机 00:55 帧复现）
            Stretch(_ammoLine.rectTransform, 0f, 0.94f, 0f, 0.70f);

            if (ammoText != null) ammoText.gameObject.SetActive(false);
            if (weaponText != null) weaponText.gameObject.SetActive(false);
            LayoutHintLine();
        }

        /// <summary>按键提示行重排（2026-09-18 实机 00:55：顶部文字互相压字）。
        /// authored HintText 与 MatchHudView 的 ScoreBar（y 0.94..0.985）同带 → 提示串叠在
        /// "RED xx / 08:22" 上。这里把它钉到比分行正下方的专用带，并关换行避免二次折行。
        /// 与弹药簇同一运行时入口做，所有地图共用、不改场景文件。</summary>
        private void LayoutHintLine()
        {
            if (hintText == null) return;
            var rt = hintText.rectTransform;
            rt.anchorMin = new Vector2(0.25f, 0.905f);
            rt.anchorMax = new Vector2(0.75f, 0.935f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            hintText.alignment = TextAnchor.MiddleCenter;
            hintText.horizontalOverflow = HorizontalWrapMode.Overflow;
            hintText.verticalOverflow = VerticalWrapMode.Truncate;
        }

        /// <summary>构建身份标签（2026-09-19 测试收口）：左上角常驻小字，显示本进程
        /// build-manifest.json 的身份（buildId 前 8 位 + 本地构建时刻），数据源与启动日志
        /// [AppBoot] APP_PROTOCOL 行同源（GameProtocolIdentity.TryReadDeployedManifest）——
        /// 解决"不知道这一轮实测的是哪个构建"的实机痛点（连续三轮测到旧构建的教训）。
        /// 编辑器内无清单 → 显示 "build editor"；读取失败不弹错、不影响 HUD 其余部分。</summary>
        private void EnsureBuildLabel()
        {
            if (_buildLabel != null) return;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            _buildLabel = CreateTmpText("BuildIdentityLabel", canvas.transform, "", 14, TextMuted,
                TextAlignmentOptions.TopLeft);
            Stretch(_buildLabel.rectTransform, 0.012f, 0.30f, 0.962f, 0.992f);

            var manifest = Game.Gameplay.Network.GameProtocolIdentity.TryReadDeployedManifest();
            if (manifest == null)
            {
                _buildLabel.text = "build editor";
                return;
            }
            string id = string.IsNullOrEmpty(manifest.buildId) ? "?" : manifest.buildId.Substring(0,
                System.Math.Min(8, manifest.buildId.Length));
            string when = "?";
            if (System.DateTime.TryParse(manifest.builtAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var utc))
                when = utc.ToLocalTime().ToString("MM-dd HH:mm:ss");
            _buildLabel.text = $"build {id} · {when}";
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

        private static void Stretch(RectTransform rt, float xMin, float xMax, float yMin, float yMax)
        {
            rt.anchorMin = new Vector2(xMin, yMin);
            rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (controller == null) return;
            controller.OnAmmoChanged += HandleAmmo;
            controller.OnWeaponEquipped += HandleWeapon;
            controller.OnReloadCompleted += HandleReloadEnd;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
            if (arsenal != null) arsenal.OnActiveWeaponChanged += HandleWeapon;
        }

        private void Unsubscribe()
        {
            if (controller == null) return;
            controller.OnAmmoChanged -= HandleAmmo;
            controller.OnWeaponEquipped -= HandleWeapon;
            controller.OnReloadCompleted -= HandleReloadEnd;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
            if (arsenal != null) arsenal.OnActiveWeaponChanged -= HandleWeapon;
        }

        private void Start() => Refresh();

        private void Update()
        {
            // 换弹进度（原 OnGUI 的 RELOAD % 行；本地 Runtime 为预测值，进度条属表现）
            if (controller != null && controller.Runtime != null
                && controller.Runtime.State == WeaponRuntimeState.Reloading)
                SetAmmo(controller.Runtime.CurrentAmmo, controller.Runtime.ReserveAmmo, true);
            else if (TryGetAuthoritativeAmmo(out int current, out int reserve))
                SetAmmo(current, reserve, false); // 在线：服务器权威弹药（Docs/23 P0-3）
        }

        /// <summary>HUD 弹药数据源判定（纯函数，可测；Docs/23 离线回归修复）：
        /// 网络已启动且已定位本地玩家网络状态 → 读服务器权威值；否则回退本地事件路径。
        /// 网络未启动时即使引用残留也必须回本地——authored player 直接读 FishNet 所有权会 NRE。</summary>
        public static bool ShouldReadAuthoritativeAmmo(bool networkActive, bool hasOwnerNetworkState)
            => networkActive && hasOwnerNetworkState;

        /// <summary>查找本地玩家（Owner）的 NetworkWeaponState 并读权威弹药。找不到（离线/未生成）
        /// 时低频重扫避免每帧开销；断线后组件随网络对象销毁，引用失效自动回退本地事件路径（F3 语义）。
        /// 所有权判定一律走 NetworkWeaponState.IsOwnerPlayerSafe（生命周期安全）。</summary>
        private bool TryGetAuthoritativeAmmo(out int current, out int reserve)
        {
            if (_netWeaponState == null)
            {
                _netScanTimer -= Time.unscaledDeltaTime;
                if (_netScanTimer <= 0f)
                {
                    _netScanTimer = 0.5f;
                    foreach (var state in FindObjectsByType<Game.Gameplay.Network.NetworkWeaponState>(FindObjectsSortMode.None))
                    {
                        if (state.IsOwnerPlayerSafe)
                        {
                            _netWeaponState = state;
                            RebindToOwnerPlayer(state);
                            break;
                        }
                    }
                }
            }
            // 数据源门（纯函数）：离线/网络未启动 → 不读网络状态（哪怕引用残留），走本地路径
            if (!ShouldReadAuthoritativeAmmo(
                    Game.Gameplay.Network.FishNetLifecycleGuard.IsNetworkActive(),
                    _netWeaponState != null))
            {
                current = 0;
                reserve = 0;
                return false;
            }
            current = _netWeaponState.CurrentAmmo;
            reserve = _netWeaponState.ReserveAmmo;
            return true;
        }

        /// <summary>Owner 网络玩家重绑（2026-09-08 追加 P0 §6 二.4，审计 §5.1 缺口 2）：
        /// 在线模式场景预置玩家已被模式门整树禁用——本视图 Awake 的 FindObjectOfType 绑定
        /// 落空（或旧顺序绑到 authored），HUD 武器名/弹药事件源必须重绑到联网 Owner 玩家的
        /// WeaponController/Arsenal（六处一致的 HUD 侧）。离线：authored 绑定保持不动。</summary>
        private void RebindToOwnerPlayer(Game.Gameplay.Network.NetworkWeaponState ownerState)
        {
            var ownerController = ownerState.GetComponentInParent<WeaponController>();
            if (ownerController == null || ownerController == controller) return;
            var ownerArsenal = ownerState.GetComponentInParent<Arsenal>();
            Unsubscribe();
            controller = ownerController;
            arsenal = ownerArsenal;
            Subscribe();
            Refresh();
            Debug.Log("[WeaponHudView] HUD 已重绑到联网 Owner 玩家（在线模式 authored 玩家不参与）");
        }

        private void HandleAmmo(int current, int reserve)
        {
            // 在线时弹药显示由 Update 轮询服务器权威值驱动；本地事件值仅离线路径消费
            if (_netWeaponState != null) return;
            SetAmmo(current, reserve, false);
        }
        private void HandleReloadEnd() => Refresh();
        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _) => Refresh();
        private void HandleWeapon(WeaponDefinition _) => Refresh();

        private void Refresh()
        {
            if (controller == null || !controller.IsInitialized) return;
            if (weaponText != null && controller.Definition != null)
                weaponText.text = controller.Definition.DisplayName;
            if (_weaponNameText != null && controller.Definition != null)
                _weaponNameText.text = controller.Definition.DisplayName;
            SetAmmo(controller.Runtime.CurrentAmmo, controller.Runtime.ReserveAmmo,
                controller.Runtime.State == WeaponRuntimeState.Reloading);
            if (hintText != null)
            {
                // 权威两槽（在线账号配装）：只提示合法槽位切换；十槽调试 Arsenal 保留旧提示
                string switchHint = arsenal != null && arsenal.SlotCount <= 2
                    ? "1/2/Q SWITCH" : "0-9/WHEEL/Q SWITCH";
                hintText.text = "LMB FIRE    RMB ADS    R RELOAD    WASD MOVE    SHIFT SPRINT    " + switchHint;
            }
        }

        private void SetAmmo(int current, int reserve, bool reloading)
        {
            // 2026-09-18 问题9：运行时 TMP 簇优先（主流 FPS 大弹药计数）；场景内建旧版仅作回退
            if (_ammoLine != null)
            {
                string reloadText = reloading
                    // 必须自带 <size>：本串拼在 </size> 之后，否则继承组件基准 46px →
                    // "RELOAD 98%" 按 46px 渲染会撑破 470px 的簇并被屏幕右缘切掉（实机 00:55 帧）
                    ? $"   <size=22><color=#{ColorUtility.ToHtmlStringRGB(AccentWarning)}>RELOAD {controller.Actions.NormalizedProgress * 100f:0}%</color></size>"
                    : string.Empty;
                _ammoLine.text = $"<b><size=46>{current:00}</size></b> " +
                    $"<size=24><color=#{ColorUtility.ToHtmlStringRGB(TextMuted)}>/ {reserve:000}</color></size>{reloadText}";
                return;
            }
            if (ammoText == null) return;
            string pct = reloading
                ? $"  RELOAD {controller.Actions.NormalizedProgress * 100f:0}%"
                : "";
            ammoText.text = $"{current:00} / {reserve:000}{pct}";
        }
    }
}
