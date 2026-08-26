using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 准心 View（uGUI，Docs/13 §5.2）：只读 Model 驱动 RectTransform（四线 + 可选中心点 +
    /// 命中标记）。不引用任何 Gameplay 类型（MVC 边界：表现只消费 Model）。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class CrosshairView : MonoBehaviour
    {
        [SerializeField] private CrosshairPresenter presenter;
        [SerializeField] private RectTransform top, bottom, left, right;
        [SerializeField] private RectTransform centerDot;
        [SerializeField] private RectTransform hitMarker;
        [SerializeField] private CrosshairConfig config;

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

            float gap = m.CurrentGap;
            float len = config.LineLength;

            // anchoredPosition 在以中心为 pivot 的 RectTransform 局部系
            if (top != null) top.anchoredPosition = new Vector2(0f, gap + len * 0.5f);
            if (bottom != null) bottom.anchoredPosition = new Vector2(0f, -(gap + len * 0.5f));
            if (left != null) left.anchoredPosition = new Vector2(-(gap + len * 0.5f), 0f);
            if (right != null) right.anchoredPosition = new Vector2(gap + len * 0.5f, 0f);
        }
    }
}
