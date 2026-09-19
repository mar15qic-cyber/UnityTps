using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.Presentation.Weapon;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    internal static class OpticAdsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AttachToHud()
        {
            var hud = GameObject.Find("WeaponHudCanvas");
            if (hud != null && hud.GetComponent<OpticAdsView>() == null)
                hud.AddComponent<OpticAdsView>();
        }
    }

    /// <summary>
    /// 本地第一人称真开镜表现：1x 瞄具只显示准直分划，低/高倍显示圆形镜内视野。
    /// 世界相机 FOV 仍由 PlayerAimState/FPCameraRig 驱动；本组件不创建第二台相机。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class OpticAdsView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private FPWeaponRig weaponRig;
        [SerializeField] private float physicalReticleStart = .55f;
        [SerializeField] private float physicalReticleFull = .90f;
        [SerializeField] private float overlayStart = .70f;
        [SerializeField] private float overlayFull = .95f;
        [SerializeField] private float hideViewmodelAt = .90f;
        [SerializeField] private float restoreViewmodelAt = .85f;

        private CanvasGroup _scopeGroup;
        private CanvasGroup _reticleGroup;
        private RectTransform _reticleFrame;
        private OpticVignetteGraphic _vignette;
        private OpticLensGraphic _lens;
        private TacticalOpticReticleGraphic _tacticalReticle;
        private string _opticId;
        private OpticPresentationMode _mode;
        private bool _hasProfile;
        private bool _viewmodelHidden;
        private static readonly HashSet<string> ProfileWarnings = new();

        // ---- A2 分划-镜窗统一（2026-09-19 ADS 审计）：分划位置跟随真实镜窗中心的
        //      FP 相机投影（与 FPWeaponMotion 光轴解共用 OpticAimGeometry 语义），
        //      过渡/后坐期间分划留在镜窗内；无校准数据时保持旧居中行为。 ----
        private OpticAimLocal _aimLocal;
        private bool _aimResolved;
        private string _aimKey;
        private bool _reticleOnScreen = true;
        private UnityEngine.Camera _fpCamera;

        private void Awake()
        {
            if (controller == null) controller = FindObjectOfType<WeaponController>();
            if (aimState == null) aimState = FindObjectOfType<PlayerAimState>();
            if (weaponRig == null) weaponRig = FindObjectOfType<FPWeaponRig>();
            BuildRuntimeUi();
        }

        private void OnEnable()
        {
            if (controller != null) controller.OnAttachmentsChanged += HandleOpticChanged;
            if (weaponRig != null) weaponRig.OnActiveViewChanged += HandleViewChanged;
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnAttachmentsChanged -= HandleOpticChanged;
            if (weaponRig != null) weaponRig.OnActiveViewChanged -= HandleViewChanged;
            RestoreViewmodel();
        }

        private void HandleOpticChanged(OpticAimContext _) => RefreshProfile();
        private void HandleViewChanged(GameObject _) { _aimKey = null; RestoreViewmodel(); }

        private void Update()
        {
            if (controller == null || aimState == null || !controller.IsInitialized)
            {
                SetAlpha(0f, 0f);
                RestoreViewmodel();
                return;
            }

            // P4 实体镜（I4b）接管：镜内 RT/分划/独立倍率由 PhysicalScopeView 渲染，
            // 画布 overlay 与藏枪让位（构建失败时 HandlingOpticId 为空，既有路径原样接管）
            if (!string.IsNullOrEmpty(Game.Presentation.Camera.PhysicalScopeView.HandlingOpticId)
                && Game.Presentation.Camera.PhysicalScopeView.HandlingOpticId == controller.CurrentOpticAim.ItemId)
            {
                SetAlpha(0f, 0f);
                RestoreViewmodel();
                return;
            }

            if (_opticId == null || _opticId != controller.CurrentOpticAim.ItemId)
                RefreshProfile();
            RefreshAimBinding();

            float ads = Mathf.Clamp01(aimState.Ads01);
            if (_viewmodelHidden && weaponRig != null && !weaponRig.IsScopedViewmodelHidden)
                _viewmodelHidden = false; // rig was disabled/re-enabled by death/menu/network recovery
            if (!_hasProfile || controller.CurrentOpticAim.Tier == OpticAimTier.None)
            {
                SetAlpha(0f, 0f);
                RestoreViewmodel();
                return;
            }

            if (_mode == OpticPresentationMode.Physical1x)
            {
                SetAlpha(0f, Fade(ads, physicalReticleStart, physicalReticleFull));
                RestoreViewmodel();
            }
            else
            {
                SetAlpha(Fade(ads, overlayStart, overlayFull), Fade(ads, overlayStart, overlayFull));
                if (!_viewmodelHidden && ads >= hideViewmodelAt) HideViewmodel();
                else if (_viewmodelHidden && ads <= restoreViewmodelAt) RestoreViewmodel();
            }

            // 分划跟随真实镜窗投影（A2）：在 alpha 淡入后应用，出镜窗时隐去而不是硬贴屏幕内
            UpdateReticleBinding();
            if (_reticleGroup != null && !_reticleOnScreen)
                _reticleGroup.alpha = 0f;
        }

        /// <summary>解析当前瞄具的挂点局部光轴数据（opticId/视图变化时重解析；含镜窗包围盒近似）。</summary>
        private void RefreshAimBinding()
        {
            if (controller == null || !controller.IsInitialized) { _aimResolved = false; return; }
            string opticId = controller.CurrentOpticAim.ItemId;
            var view = weaponRig != null && weaponRig.ActiveView != null
                ? weaponRig.ActiveView.GetComponent<WeaponView>()
                : null;
            string key = (opticId ?? string.Empty) + "#" + (view != null ? view.GetInstanceID() : 0);
            if (_aimKey == key && _aimResolved) return;
            _aimKey = key;
            _aimResolved = false;
            _aimLocal = default;
            if (opticId != null && view != null
                && OpticAimGeometry.TryResolveLocal(controller, view, out var local))
            {
                _aimLocal = local;
                _aimResolved = true;
            }
        }

        /// <summary>把分划容器锚到真实镜窗中心的 FP 相机投影位置（画布局部坐标）；
        /// 无校准数据/相机不可用时保持居中（旧行为）。</summary>
        private void UpdateReticleBinding()
        {
            if (_reticleFrame == null) return;
            var rootRect = ((RectTransform)transform).rect;
            _reticleFrame.sizeDelta = new Vector2(rootRect.width, rootRect.height);
            _reticleOnScreen = true;

            Vector2 canvasPosition = Vector2.zero;
            bool bound = false;
            if (_aimResolved && controller != null && controller.IsInitialized
                && weaponRig != null && weaponRig.ActiveView != null)
            {
                var attachments = weaponRig.ActiveView.GetComponent<WeaponAttachmentView>();
                var socket = attachments != null ? attachments.GetSocketTransform(AttachmentSlotType.Optic) : null;
                var camera = ResolveFpCamera();
                if (socket != null && camera != null)
                {
                    var frameData = OpticAimGeometry.Evaluate(_aimLocal, socket);
                    Vector3 viewport = camera.WorldToViewportPoint(frameData.WindowCenterWorld);
                    if (viewport.z > 0f)
                    {
                        canvasPosition = new Vector2(
                            (viewport.x - 0.5f) * rootRect.width,
                            (viewport.y - 0.5f) * rootRect.height);
                        bound = true;
                        // 镜窗中心出画面 12% 边距 → 分划隐去（不硬贴屏内掩盖对位错误）
                        float excessX = Mathf.Abs(canvasPosition.x) - rootRect.width * 0.62f;
                        float excessY = Mathf.Abs(canvasPosition.y) - rootRect.height * 0.62f;
                        _reticleOnScreen = excessX <= 0f && excessY <= 0f;
                    }
                }
            }
            _reticleFrame.anchoredPosition = bound ? canvasPosition : Vector2.zero;
        }

        private UnityEngine.Camera ResolveFpCamera()
        {
            if (_fpCamera != null) return _fpCamera;
            if (controller == null) return null;
            var cameras = controller.GetComponentsInChildren<UnityEngine.Camera>(true);
            foreach (var candidate in cameras)
            {
                if (candidate.name == "FP View Camera") { _fpCamera = candidate; break; }
            }
            if (_fpCamera == null && cameras.Length > 0) _fpCamera = cameras[cameras.Length - 1];
            return _fpCamera;
        }

        private void RefreshProfile()
        {
            _opticId = controller != null ? controller.CurrentOpticAim.ItemId : null;
            _hasProfile = false;
            if (controller == null || _opticId == null) { SetAlpha(0f, 0f); return; }
            var ctx = controller.CurrentOpticAim;
            _hasProfile = OpticViewCatalog.LoadOrDefault().TryGet(_opticId, ctx.Tier, out var profile);
            if (!_hasProfile || profile == null)
            {
                WarnMissingProfile(ctx);
                SetAlpha(0f, 0f);
                return;
            }
            _mode = profile.mode;
            if (_vignette != null)
            {
                _vignette.ApertureRadius = profile.apertureRadius;
                _vignette.Color = new Color(0f, 0f, 0f, profile.vignetteAlpha);
            }
            if (_lens != null)
            {
                _lens.ApertureRadius = profile.apertureRadius;
                _lens.Tint = ctx.Tier == OpticAimTier.HighZoom
                    ? new Color(.08f, .11f, .12f, .09f)
                    : new Color(.10f, .13f, .12f, .055f);
                _lens.SetVerticesDirty();
            }
            if (_tacticalReticle != null)
            {
                _tacticalReticle.Style = ToReticleStyle(ctx.Tier);
                _tacticalReticle.Illumination = profile.reticleColor;
                _tacticalReticle.SetVerticesDirty();
            }
        }

        private void WarnMissingProfile(OpticAimContext ctx)
        {
            string key = (ctx.ItemId ?? "<none>") + "|" + ctx.Tier;
            if (!ProfileWarnings.Add(key)) return;
            Debug.LogWarning($"[OpticAdsView] 缺少瞄具表现 Profile，已降级为普通 ADS：{key}", this);
        }

        private void BuildRuntimeUi()
        {
            var scopeGo = new GameObject("OpticScopeVignette", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(CanvasGroup), typeof(OpticVignetteGraphic));
            scopeGo.transform.SetParent(transform, false);
            _scopeGroup = scopeGo.GetComponent<CanvasGroup>();
            _vignette = scopeGo.GetComponent<OpticVignetteGraphic>();
            _scopeGroup.interactable = false;
            _scopeGroup.blocksRaycasts = false;
            _vignette.raycastTarget = false;
            Stretch(scopeGo.GetComponent<RectTransform>());

            var lensGo = new GameObject("Lens", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(OpticLensGraphic));
            lensGo.transform.SetParent(scopeGo.transform, false);
            Stretch(lensGo.GetComponent<RectTransform>());
            _lens = lensGo.GetComponent<OpticLensGraphic>();
            _lens.raycastTarget = false;

            // Unity UI permits one Graphic per GameObject. Keep the tactical reticle
            // on its own renderer so construction remains valid on every entry path.
            var reticleGo = new GameObject("OpticReticle", typeof(RectTransform), typeof(CanvasGroup));
            reticleGo.transform.SetParent(transform, false);
            _reticleGroup = reticleGo.GetComponent<CanvasGroup>();
            _reticleGroup.interactable = false;
            _reticleGroup.blocksRaycasts = false;
            var rt = reticleGo.GetComponent<RectTransform>();
            Stretch(rt);

            // A2：分划容器——位置每帧锚到真实镜窗中心的投影；无绑定数据时归零=旧居中行为。
            var frameGo = new GameObject("ReticleFrame", typeof(RectTransform));
            frameGo.transform.SetParent(reticleGo.transform, false);
            _reticleFrame = frameGo.GetComponent<RectTransform>();
            _reticleFrame.anchorMin = _reticleFrame.anchorMax = _reticleFrame.pivot = new Vector2(0.5f, 0.5f);
            _reticleFrame.anchoredPosition = Vector2.zero;
            _reticleFrame.sizeDelta = Vector2.zero;

            var tacticalGo = new GameObject("Tactical", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(TacticalOpticReticleGraphic));
            tacticalGo.transform.SetParent(_reticleFrame, false);
            Stretch(tacticalGo.GetComponent<RectTransform>());
            _tacticalReticle = tacticalGo.GetComponent<TacticalOpticReticleGraphic>();
            _tacticalReticle.raycastTarget = false;

            scopeGo.transform.SetAsFirstSibling(); // 遮罩在准星和普通 HUD 下方
            reticleGo.transform.SetAsLastSibling();
            SetAlpha(0f, 0f);
            RefreshProfile();
        }

        private void HideViewmodel()
        {
            if (_viewmodelHidden || weaponRig == null) return;
            // 审计 2026-09-16 §3.2：镜内遮罩只持自己的 ScopeOverlay 原因位——不再复用"任意隐藏"开关，
            // 退出 ADS 不会释放死亡隐藏，复活也不会释放镜内隐藏
            weaponRig.SetScopeOverlayHidden(true);
            _viewmodelHidden = true;
        }

        private void RestoreViewmodel()
        {
            if (!_viewmodelHidden || weaponRig == null) return;
            weaponRig.SetScopeOverlayHidden(false);
            _viewmodelHidden = false;
        }

        private void SetAlpha(float scope, float reticle)
        {
            if (_scopeGroup != null) _scopeGroup.alpha = Mathf.Clamp01(scope);
            if (_reticleGroup != null) _reticleGroup.alpha = Mathf.Clamp01(reticle);
        }

        private static float Fade(float value, float start, float full)
        {
            if (full <= start) return value >= full ? 1f : 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, full, value));
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static TacticalReticleStyle ToReticleStyle(OpticAimTier tier)
        {
            return tier switch
            {
                OpticAimTier.RedDot => TacticalReticleStyle.RedDot,
                OpticAimTier.Holo => TacticalReticleStyle.Holographic,
                OpticAimTier.LowZoom => TacticalReticleStyle.ThreePower,
                OpticAimTier.HighZoom => TacticalReticleStyle.HighPower,
                _ => TacticalReticleStyle.RedDot
            };
        }

    }

    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class OpticVignetteGraphic : MaskableGraphic
    {
        [Range(.1f, .49f)] public float ApertureRadius = .43f;
        public Color Color = new(0f, 0f, 0f, .98f);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var rect = rectTransform.rect;
            float cx = rect.center.x, cy = rect.center.y;
            float outer = Mathf.Max(rect.width, rect.height) * 1.5f;
            float inner = Mathf.Min(rect.width, rect.height) * Mathf.Clamp(ApertureRadius, .1f, .49f);
            const int segments = 64;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                int n = vh.currentVertCount;
                vh.AddVert(new Vector3(cx + Mathf.Cos(a0) * inner, cy + Mathf.Sin(a0) * inner), Color, Vector2.zero);
                vh.AddVert(new Vector3(cx + Mathf.Cos(a1) * inner, cy + Mathf.Sin(a1) * inner), Color, Vector2.zero);
                vh.AddVert(new Vector3(cx + Mathf.Cos(a1) * outer, cy + Mathf.Sin(a1) * outer), Color, Vector2.zero);
                vh.AddVert(new Vector3(cx + Mathf.Cos(a0) * outer, cy + Mathf.Sin(a0) * outer), Color, Vector2.zero);
                vh.AddTriangle(n, n + 1, n + 2);
                vh.AddTriangle(n, n + 2, n + 3);
            }
        }
    }

    public enum TacticalReticleStyle
    {
        RedDot,
        Holographic,
        ThreePower,
        HighPower
    }

    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class OpticLensGraphic : MaskableGraphic
    {
        [Range(.1f, .49f)] public float ApertureRadius = .43f;
        public Color Tint = new(.10f, .13f, .12f, .055f);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var rect = rectTransform.rect;
            Vector2 center = rect.center;
            float unit = Mathf.Min(rect.width, rect.height);
            float radius = unit * Mathf.Clamp(ApertureRadius, .1f, .49f);
            AddDisc(vh, center, radius, Tint, 96);
            AddRing(vh, center, radius, Mathf.Max(3f, unit * .010f),
                new Color(.015f, .018f, .018f, .96f), 96);
            AddRing(vh, center, radius - Mathf.Max(3f, unit * .012f), Mathf.Max(1f, unit * .002f),
                new Color(.58f, .66f, .63f, .28f), 96);
        }

        private static void AddDisc(VertexHelper vh, Vector2 center, float radius, Color color, int segments)
        {
            int c = vh.currentVertCount;
            vh.AddVert(center, color, Vector2.zero);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, color, Vector2.zero);
                if (i > 0) vh.AddTriangle(c, c + i, c + i + 1);
            }
        }

        private static void AddRing(VertexHelper vh, Vector2 center, float radius, float thickness,
            Color color, int segments)
        {
            float inner = Mathf.Max(0f, radius - thickness);
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector2 d0 = new(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new(Mathf.Cos(a1), Mathf.Sin(a1));
                AddQuad(vh, center + d0 * inner, center + d0 * radius,
                    center + d1 * radius, center + d1 * inner, color);
            }
        }

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int n = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero); vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(n, n + 1, n + 2); vh.AddTriangle(n, n + 2, n + 3);
        }
    }

    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class TacticalOpticReticleGraphic : MaskableGraphic
    {
        public TacticalReticleStyle Style;
        public Color Illumination = new(1f, .18f, .06f, 1f);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var rect = rectTransform.rect;
            Vector2 center = rect.center;
            float unit = Mathf.Min(rect.width, rect.height);
            switch (Style)
            {
                case TacticalReticleStyle.RedDot: DrawRedDot(vh, center, unit); break;
                case TacticalReticleStyle.Holographic: DrawHolo(vh, center, unit); break;
                case TacticalReticleStyle.ThreePower: DrawThreePower(vh, center, unit); break;
                case TacticalReticleStyle.HighPower: DrawHighPower(vh, center, unit); break;
            }
        }

        private void DrawRedDot(VertexHelper vh, Vector2 c, float u)
        {
            Color glow = Illumination; glow.a = .08f;
            AddDisc(vh, c, Mathf.Max(9f, u * .010f), glow, 32);
            glow.a = .22f;
            AddDisc(vh, c, Mathf.Max(4.5f, u * .005f), glow, 24);
            AddDisc(vh, c, Mathf.Max(2f, u * .0022f), Illumination, 20);
        }

        private void DrawHolo(VertexHelper vh, Vector2 c, float u)
        {
            float radius = Mathf.Max(18f, u * .022f);
            float thin = Mathf.Max(2f, u * .0018f);
            Color glow = Illumination; glow.a = .13f;
            AddRing(vh, c, radius + thin * 2f, thin * 3f, glow, 48);
            AddRing(vh, c, radius, thin, Illumination, 48);
            float tick = radius * .28f;
            AddLine(vh, c + Vector2.up * radius, c + Vector2.up * (radius + tick), thin, Illumination);
            AddLine(vh, c - Vector2.up * radius, c - Vector2.up * (radius + tick), thin, Illumination);
            AddLine(vh, c + Vector2.right * radius, c + Vector2.right * (radius + tick), thin, Illumination);
            AddLine(vh, c - Vector2.right * radius, c - Vector2.right * (radius + tick), thin, Illumination);
            AddDisc(vh, c, Mathf.Max(2f, u * .002f), Illumination, 20);
        }

        private void DrawThreePower(VertexHelper vh, Vector2 c, float u)
        {
            Color ink = new(.035f, .035f, .03f, .96f);
            Color lit = new(1f, .22f, .055f, 1f);
            float thin = Mathf.Max(1.35f, u * .00145f);
            float gap = u * .014f;
            float span = u * .225f;
            AddLine(vh, c + Vector2.left * span, c + Vector2.left * gap, thin, ink);
            AddLine(vh, c + Vector2.right * gap, c + Vector2.right * span, thin, ink);
            AddLine(vh, c + Vector2.up * (u * .17f), c + Vector2.up * gap, thin, ink);
            AddLine(vh, c - Vector2.up * gap, c - Vector2.up * (u * .245f), thin, ink);
            for (int i = 1; i <= 5; i++)
            {
                float x = u * .038f * i;
                float h = u * (i % 2 == 0 ? .016f : .010f);
                AddLine(vh, c + new Vector2(x, -h), c + new Vector2(x, h), thin, ink);
                AddLine(vh, c + new Vector2(-x, -h), c + new Vector2(-x, h), thin, ink);
            }
            AddChevron(vh, c + Vector2.up * (u * .003f), u * .020f, thin * 2f, lit);
            for (int i = 1; i <= 4; i++)
            {
                float y = -u * (.040f + (i - 1) * .043f);
                float half = u * (.014f + i * .008f);
                AddLine(vh, c + new Vector2(-half, y), c + new Vector2(half, y), thin, ink);
            }
        }

        private void DrawHighPower(VertexHelper vh, Vector2 c, float u)
        {
            Color ink = new(.025f, .025f, .022f, .97f);
            Color lit = new(.92f, .12f, .035f, 1f);
            float thin = Mathf.Max(1.1f, u * .00115f);
            float thick = thin * 3.2f;
            float gap = u * .010f;
            float inner = u * .105f;
            float outer = u * .34f;
            AddLine(vh, c + Vector2.left * outer, c + Vector2.left * inner, thick, ink);
            AddLine(vh, c + Vector2.left * inner, c + Vector2.left * gap, thin, ink);
            AddLine(vh, c + Vector2.right * gap, c + Vector2.right * inner, thin, ink);
            AddLine(vh, c + Vector2.right * inner, c + Vector2.right * outer, thick, ink);
            AddLine(vh, c + Vector2.up * outer, c + Vector2.up * inner, thick, ink);
            AddLine(vh, c + Vector2.up * inner, c + Vector2.up * gap, thin, ink);
            AddLine(vh, c - Vector2.up * gap, c - Vector2.up * inner, thin, ink);
            AddLine(vh, c - Vector2.up * inner, c - Vector2.up * outer, thick, ink);
            for (int i = 1; i <= 4; i++)
            {
                float d = u * .033f * i;
                float dot = Mathf.Max(1.5f, thin * 1.35f);
                AddDisc(vh, c + Vector2.right * d, dot, ink, 16);
                AddDisc(vh, c + Vector2.left * d, dot, ink, 16);
                AddDisc(vh, c + Vector2.up * d, dot, ink, 16);
                AddDisc(vh, c - Vector2.up * d, dot, ink, 16);
            }
            AddDisc(vh, c, Mathf.Max(1.6f, thin * 1.4f), lit, 18);
            for (int i = 1; i <= 5; i++)
            {
                float y = -u * (.050f + i * .038f);
                float half = u * (.010f + i * .007f);
                AddLine(vh, c + new Vector2(-half, y), c + new Vector2(half, y), thin, ink);
            }
        }

        private static void AddChevron(VertexHelper vh, Vector2 tip, float size, float thickness, Color color)
        {
            AddLine(vh, tip, tip + new Vector2(-size, size * .72f), thickness, color);
            AddLine(vh, tip, tip + new Vector2(size, size * .72f), thickness, color);
        }

        private static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 delta = b - a;
            if (delta.sqrMagnitude < .0001f) return;
            Vector2 normal = new Vector2(-delta.y, delta.x).normalized * (thickness * .5f);
            AddQuad(vh, a - normal, a + normal, b + normal, b - normal, color);
        }

        private static void AddDisc(VertexHelper vh, Vector2 center, float radius, Color color, int segments)
        {
            int n = vh.currentVertCount;
            vh.AddVert(center, color, Vector2.zero);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, color, Vector2.zero);
                if (i > 0) vh.AddTriangle(n, n + i, n + i + 1);
            }
        }

        private static void AddRing(VertexHelper vh, Vector2 center, float radius, float thickness,
            Color color, int segments)
        {
            float inner = Mathf.Max(0f, radius - thickness);
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector2 d0 = new(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new(Mathf.Cos(a1), Mathf.Sin(a1));
                AddQuad(vh, center + d0 * inner, center + d0 * radius,
                    center + d1 * radius, center + d1 * inner, color);
            }
        }

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int n = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero); vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(n, n + 1, n + 2); vh.AddTriangle(n, n + 2, n + 3);
        }
    }

}
