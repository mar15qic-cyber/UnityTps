using UnityEngine;

namespace Game.Debugging
{
    /// <summary>
    /// 瞄具光轴校准的屏幕 overlay（OpticAimCalibrationWindow 配套，Play Mode 用）：
    /// 屏幕中心画品红十字（=弹着点权威），绿色方框标当前眼点投影——
    /// 人微调眼点直到网格自带准星、绿框、红十字三者重合即校准完成。
    /// 纯调试组件，不进构建；静态字段由校准窗口每帧喂值。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OpticAimCalibrationOverlay : MonoBehaviour
    {
        /// <summary>眼点投影到屏幕的像素坐标（null=不显示）。</summary>
        public static Vector2? EyeScreenPosition;
        /// <summary>眼点与屏幕中心的像素误差（&lt;0=不显示）。</summary>
        public static float EyePixelError = -1f;
        /// <summary>状态行文本（当前编辑的组合/模式等）。</summary>
        public static string StatusText = string.Empty;

        private GUIStyle _labelStyle;

        private void OnGUI()
        {
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            // 中心十字（品红，12px 臂长，2px 粗）
            DrawRect(new Rect(center.x - 12f, center.y - 1f, 24f, 2f), Color.magenta);
            DrawRect(new Rect(center.x - 1f, center.y - 12f, 2f, 24f), Color.magenta);

            if (EyeScreenPosition.HasValue)
            {
                var p = EyeScreenPosition.Value;
                DrawRect(new Rect(p.x - 5f, p.y - 5f, 10f, 1.5f), Color.green);
                DrawRect(new Rect(p.x - 5f, p.y + 3.5f, 10f, 1.5f), Color.green);
                DrawRect(new Rect(p.x - 5f, p.y - 5f, 1.5f, 10f), Color.green);
                DrawRect(new Rect(p.x + 3.5f, p.y - 5f, 1.5f, 10f), Color.green);
            }

            if (_labelStyle == null)
                _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            if (EyePixelError >= 0f)
                GUI.Label(new Rect(center.x + 18f, center.y - 10f, 400f, 22f),
                    $"<color=#00ff00>眼点误差 {EyePixelError:F1}px</color>", _labelStyle);
            if (!string.IsNullOrEmpty(StatusText))
                GUI.Label(new Rect(12f, Screen.height - 28f, 900f, 22f),
                    $"<color=#ffff88>{StatusText}</color>", _labelStyle);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private void OnDisable()
        {
            EyeScreenPosition = null;
            EyePixelError = -1f;
            StatusText = string.Empty;
        }
    }
}
