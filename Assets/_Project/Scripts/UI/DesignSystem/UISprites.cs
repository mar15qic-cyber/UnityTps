using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Procedurally generated sprites (rounded rect 9-slice, circle) so the code-generated UI
    /// can have POLYGON-style rounded corners without importing third-party art.
    /// Textures are created once and cached for the process lifetime.
    /// </summary>
    public static class UISprites
    {
        private const int AtlasSize = 64;
        private static readonly Dictionary<string, Sprite> cache = new();

        public static Sprite EdgeShade()
        {
            if (cache.TryGetValue("edgeShade", out var sprite) && sprite != null) return sprite;
            var texture = new Texture2D(256, 2, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            for (var x = 0; x < 256; x++)
            {
                var distance = Mathf.Abs(x / 255f - 0.5f) * 2f;
                var color = new Color(0.03f, 0.06f, 0.06f, Mathf.SmoothStep(0, 0.87f, distance * distance));
                texture.SetPixel(x, 0, color); texture.SetPixel(x, 1, color);
            }
            texture.Apply();
            sprite = Sprite.Create(texture, new Rect(0, 0, 256, 2), new Vector2(0.5f, 0.5f));
            cache["edgeShade"] = sprite;
            return sprite;
        }

        /// <summary>White rounded-rect sprite with 9-slice border; tint via Image.color.</summary>
        public static Sprite RoundedRect(float radius)
        {
            var key = "rr_" + Mathf.RoundToInt(radius);
            if (cache.TryGetValue(key, out var existing) && existing != null) return existing;

            var radiusPx = Mathf.Clamp(Mathf.RoundToInt(radius), 1, AtlasSize / 2 - 1);
            var tex = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false);
            for (var y = 0; y < AtlasSize; y++)
            {
                for (var x = 0; x < AtlasSize; x++)
                {
                    // Distance to the rounded-rect interior: circle corners at (r, r) etc.
                    var cx = Mathf.Clamp(x, radiusPx, AtlasSize - 1 - radiusPx);
                    var cy = Mathf.Clamp(y, radiusPx, AtlasSize - 1 - radiusPx);
                    var dx = x - cx;
                    var dy = y - cy;
                    var inside = dx * dx + dy * dy <= radiusPx * radiusPx;
                    tex.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            tex.name = key;

            var border = new Vector4(radiusPx, radiusPx, radiusPx, radiusPx);
            var sprite = Sprite.Create(tex, new Rect(0, 0, AtlasSize, AtlasSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sprite.name = key;
            cache[key] = sprite;
            return sprite;
        }

        /// <summary>Solid white circle sprite for badges/spinners.</summary>
        public static Sprite Circle()
        {
            const string key = "circle";
            if (cache.TryGetValue(key, out var existing) && existing != null) return existing;

            var tex = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false);
            var center = (AtlasSize - 1) * 0.5f;
            var radius = AtlasSize * 0.5f - 1f;
            for (var y = 0; y < AtlasSize; y++)
            {
                for (var x = 0; x < AtlasSize; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    tex.SetPixel(x, y, dx * dx + dy * dy <= radius * radius ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            tex.name = key;

            var sprite = Sprite.Create(tex, new Rect(0, 0, AtlasSize, AtlasSize), new Vector2(0.5f, 0.5f));
            sprite.name = key;
            cache[key] = sprite;
            return sprite;
        }

        /// <summary>Vertical gradient sprite (top=topColor, bottom=bottomColor); used as overlay/backdrop fallback.</summary>
        public static Sprite GradientVertical(Color topColor, Color bottomColor)
        {
            var key = $"grad_{ColorUtility.ToHtmlStringRGBA(topColor)}_{ColorUtility.ToHtmlStringRGBA(bottomColor)}";
            if (cache.TryGetValue(key, out var existing) && existing != null) return existing;

            const int height = 128;
            var tex = new Texture2D(1, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (var y = 0; y < height; y++)
                tex.SetPixel(0, y, Color.Lerp(bottomColor, topColor, y / (float)(height - 1)));
            tex.Apply();
            tex.name = key;

            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, height), new Vector2(0.5f, 0.5f));
            sprite.name = key;
            cache[key] = sprite;
            return sprite;
        }

        public static void ClearCache()
        {
            cache.Clear();
        }
    }
}
