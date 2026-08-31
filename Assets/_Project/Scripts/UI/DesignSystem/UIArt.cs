using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Registry for AI-generated key visuals (Docs/20 §4). Loads sprites from asset paths in the
    /// editor and from Resources in builds; when an asset is missing, returns a procedural
    /// gradient fallback so pages always render (offline/CI-safe).
    /// </summary>
    public static class UIArt
    {
        public sealed class Entry
        {
            public string Key;
            public string EditorPath;
            public string ResourcesPath;
            public Color FallbackTop;
            public Color FallbackBottom;
        }

        public const string KeyBackgroundLogin = "bg.login";
        public const string KeyBackgroundLobby = "bg.lobby";
        public const string KeyLogo = "logo";

        private static readonly Entry[] entries =
        {
            new()
            {
                Key = KeyBackgroundLogin,
                EditorPath = "Assets/_Project/Art/UI/Backgrounds/LoginBackground.png",
                ResourcesPath = "UI/Backgrounds/LoginBackground",
                FallbackTop = UITheme.Hex("#3A4A5F"),
                FallbackBottom = UITheme.BackgroundDeep,
            },
            new()
            {
                Key = KeyBackgroundLobby,
                EditorPath = "Assets/_Project/Art/UI/Backgrounds/LobbyBackground.png",
                ResourcesPath = "UI/Backgrounds/LobbyBackground",
                FallbackTop = UITheme.Hex("#2E3D52"),
                FallbackBottom = UITheme.BackgroundDeep,
            },
            new()
            {
                Key = KeyLogo,
                EditorPath = "Assets/_Project/Art/UI/GameLogo.png",
                ResourcesPath = "UI/GameLogo",
                FallbackTop = UITheme.AccentPrimary,
                FallbackBottom = UITheme.AccentWarning,
            },
        };

        private static readonly Dictionary<string, Sprite> resolved = new();

        public static IReadOnlyList<Entry> Entries => entries;

        /// <summary>True when the real (non-fallback) sprite for the key exists on disk / in Resources.</summary>
        public static bool HasRealAsset(string key)
        {
            return LoadRealSprite(key) != null;
        }

        /// <summary>Sprite for the key; procedural gradient fallback when the asset is missing. Never null.</summary>
        public static Sprite Get(string key)
        {
            if (resolved.TryGetValue(key, out var cached) && cached != null) return cached;
            var sprite = LoadRealSprite(key);
            if (sprite == null)
            {
                var entry = Find(key);
                sprite = UISprites.GradientVertical(
                    entry != null ? entry.FallbackTop : UITheme.BackgroundPanel,
                    entry != null ? entry.FallbackBottom : UITheme.BackgroundDeep);
            }
            resolved[key] = sprite;
            return sprite;
        }

        private static Sprite LoadRealSprite(string key)
        {
            var entry = Find(key);
            if (entry == null) return null;
#if UNITY_EDITOR
            var sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(entry.EditorPath);
            if (sprite != null) return sprite;
#endif
            return Resources.Load<Sprite>(entry.ResourcesPath);
        }

        private static Entry Find(string key)
        {
            foreach (var entry in entries)
                if (entry.Key == key) return entry;
            return null;
        }

        public static void ResetCache() => resolved.Clear();
    }
}
