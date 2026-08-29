using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Draws the four shop-facing weapon dimensions: damage, RPM, magazine and recoil.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class WeaponRadarChart : MaskableGraphic
    {
        private const float MaxDamage = 120f;
        private const float MaxRoundsPerMinute = 900f;
        private const float MaxMagazine = 40f;
        private const float MaxRecoil = 4f;
        private readonly float[] values = new float[4];

        [SerializeField, Min(0.5f)] private float lineThickness = 2f;
        [SerializeField] private Color gridColor = new(0.25f, 0.7f, 1f, 0.32f);
        [SerializeField] private Color fillColor = new(0.1f, 0.83f, 0.72f, 0.42f);
        [SerializeField] private Color outlineColor = new(0.1f, 0.83f, 0.72f, 1f);

        public void SetStats(WeaponUiStats stats)
        {
            if (stats == null) stats = new WeaponUiStats(0f, 0f, 0f, 0f);
            values[0] = Mathf.Clamp01(stats.damage / MaxDamage);
            values[1] = Mathf.Clamp01(stats.roundsPerMinute / MaxRoundsPerMinute);
            values[2] = Mathf.Clamp01(stats.magazineSize / MaxMagazine);
            values[3] = Mathf.Clamp01(stats.recoil / MaxRecoil);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var rect = rectTransform.rect;
            var center = rect.center;
            var radius = Mathf.Min(rect.width, rect.height) * 0.38f;
            for (var ring = 1; ring <= 4; ring++)
            {
                var ringPoints = Points(center, radius * ring / 4f, 1f);
                for (var i = 0; i < ringPoints.Length; i++) AddLine(vh, ringPoints[i], ringPoints[(i + 1) % ringPoints.Length], gridColor, lineThickness);
            }

            var axisPoints = Points(center, radius, 1f);
            foreach (var point in axisPoints) AddLine(vh, center, point, gridColor, lineThickness);

            var dataPoints = Points(center, radius, 0f);
            AddPolygon(vh, center, dataPoints, fillColor);
            for (var i = 0; i < dataPoints.Length; i++) AddLine(vh, dataPoints[i], dataPoints[(i + 1) % dataPoints.Length], outlineColor, lineThickness + 1f);
        }

        private Vector2[] Points(Vector2 center, float radius, float valueScale)
        {
            var points = new Vector2[4];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = (90f - i * 90f) * Mathf.Deg2Rad;
                var value = valueScale <= 0f ? values[i] : 1f;
                points[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * value;
            }
            return points;
        }

        private static void AddPolygon(VertexHelper vh, Vector2 center, Vector2[] points, Color color)
        {
            for (var i = 0; i < points.Length; i++)
            {
                var start = vh.currentVertCount;
                AddVertex(vh, center, color);
                AddVertex(vh, points[i], color);
                AddVertex(vh, points[(i + 1) % points.Length], color);
                vh.AddTriangle(start, start + 1, start + 2);
            }
        }

        private static void AddLine(VertexHelper vh, Vector2 from, Vector2 to, Color color, float thickness)
        {
            var direction = (to - from).normalized;
            var normal = new Vector2(-direction.y, direction.x) * thickness * 0.5f;
            var start = vh.currentVertCount;
            AddVertex(vh, from - normal, color);
            AddVertex(vh, from + normal, color);
            AddVertex(vh, to + normal, color);
            AddVertex(vh, to - normal, color);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddVertex(VertexHelper vh, Vector2 position, Color color)
        {
            var vertex = UIVertex.simpleVert;
            vertex.position = position;
            vertex.color = color;
            vh.AddVert(vertex);
        }
    }
}
