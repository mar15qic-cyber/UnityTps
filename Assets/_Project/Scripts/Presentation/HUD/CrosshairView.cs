using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心 View（uGUI，Docs/13 §5.2）：只读 Model 驱动 RectTransform（四线 + 可选中心点 +
    /// 命中标记）。不引用任何 Gameplay 类型（MVC 边界：表现只消费 Model）。
    /// Day4 实机审计 §2：① OnEnable fail-fast 校验（缺任一四线/Config/Presenter 引用即报错，
    /// 不再静默渲染残缺准心）；② Gap/线长/线宽统一按屏幕像素换算为 Canvas 单位。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class CrosshairView : MonoBehaviour
    {
        [SerializeField] private CrosshairPresenter presenter;
        [SerializeField] private RectTransform top, bottom, left, right;
        [SerializeField] private RectTransform centerDot;
        [SerializeField] private RectTransform hitMarker;
        [SerializeField] private CrosshairConfig config;

        private bool _validated;
        private Canvas _canvas;

        private void OnEnable() => ValidateReferences();

        /// <summary>fail-fast（审计 §2）：引用缺失/线条方向配错立即报错——静默只会产出
        /// "残缺准心"这类实机才可发现的回归。</summary>
        private void ValidateReferences()
        {
            if (_validated) return;
            _validated = true;
            if (presenter == null)
                Debug.LogError("[CrosshairView] presenter 引用缺失——准心不会驱动任何布局。", this);
            if (config == null)
                Debug.LogError("[CrosshairView] config 引用缺失——Gap/线长/线宽全部不可用。", this);
            if (top == null || bottom == null || left == null || right == null)
                Debug.LogError("[CrosshairView] 四线引用缺失：" +
                               $"top={top != null} bottom={bottom != null} left={left != null} right={right != null}——" +
                               "残缺十字的直接原因。请重跑 Tools/Build Weapon HUD。", this);
            // 线条方向校验：上下必须竖线（高>宽），左右必须横线（宽>高）
            if (top != null && bottom != null && top.sizeDelta.y <= top.sizeDelta.x)
                Debug.LogError("[CrosshairView] top/bottom 应为竖线（sizeDelta.y > x），当前 top=" +
                               $"{top.sizeDelta}——方向配置错误。", this);
            if (left != null && right != null && left.sizeDelta.x <= left.sizeDelta.y)
                Debug.LogError("[CrosshairView] left/right 应为横线（sizeDelta.x > y），当前 left=" +
                               $"{left.sizeDelta}——方向配置错误。", this);
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas == null)
                Debug.LogError("[CrosshairView] 找不到父 Canvas——无法将屏幕像素转换为 Canvas 单位。", this);
        }

        private void LateUpdate()
        {
            if (presenter == null || config == null) return;
            var m = presenter.Model;

            bool crosshairOn = m.Visible;
            if (top != null) top.gameObject.SetActive(crosshairOn);
            if (bottom != null) bottom.gameObject.SetActive(crosshairOn);
            if (left != null) left.gameObject.SetActive(crosshairOn);
            if (right != null) right.gameObject.SetActive(crosshairOn);
            if (centerDot != null) centerDot.gameObject.SetActive(crosshairOn && config.ShowCenterDot);
            if (hitMarker != null) hitMarker.gameObject.SetActive(m.HitMarkerRemaining > 0f);
            if (!crosshairOn) return;

            float canvasScale = _canvas != null ? Mathf.Max(0.0001f, _canvas.scaleFactor) : 1f;
            // Model/config 以实际屏幕像素为单位；CanvasScaler 后的局部坐标需除以 scaleFactor。
            float gap = m.CurrentGap / canvasScale;
            float len = Mathf.Max(0f, config.LineLength) / canvasScale;
            float thickness = Mathf.Max(0.5f, config.LineThickness) / canvasScale;

            if (top != null) top.sizeDelta = new Vector2(thickness, len);
            if (bottom != null) bottom.sizeDelta = new Vector2(thickness, len);
            if (left != null) left.sizeDelta = new Vector2(len, thickness);
            if (right != null) right.sizeDelta = new Vector2(len, thickness);
            if (centerDot != null) centerDot.sizeDelta = Vector2.one * (Mathf.Max(0f, config.DotSize) / canvasScale);

            // anchoredPosition 在以中心为 pivot 的 RectTransform 局部系（四线以中心镜像对称）
            if (top != null) top.anchoredPosition = new Vector2(0f, gap + len * 0.5f);
            if (bottom != null) bottom.anchoredPosition = new Vector2(0f, -(gap + len * 0.5f));
            if (left != null) left.anchoredPosition = new Vector2(-(gap + len * 0.5f), 0f);
            if (right != null) right.anchoredPosition = new Vector2(gap + len * 0.5f, 0f);
        }
    }
}
