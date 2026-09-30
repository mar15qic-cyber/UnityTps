using System.Collections.Generic;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace Game.Presentation.HUD
{
    /// <summary>North-up cached map. Enemy markers come only from server-validated team sightings.</summary>
    public sealed class TacticalMinimapView : MonoBehaviour
    {
        private const float SpotLifetimeSeconds = 2.5f;
        private readonly Dictionary<int, Spot> _spots = new();
        private readonly List<int> _expired = new();
        private NetworkCombatAuthority _owner;
        private MapRadarCatalog.Entry _mapEntry;
        private RawImage _mapImage;
        private RectTransform _mapRect;
        private RectTransform _playerDot;
        private float _nextScan;
        private bool _mapReady;
        private float _captureAt;
        private readonly Dictionary<int, RectTransform> _allies = new();

        private sealed class Spot
        {
            public Vector3 Position;
            public float ExpiresAt;
            public RectTransform Dot;
        }

        public static void TryMount(Canvas canvas)
        {
            if (canvas == null || canvas.GetComponentInChildren<TacticalMinimapView>(true) != null) return;
            var root = new GameObject("TacticalMinimap", typeof(RectTransform), typeof(TacticalMinimapView));
            root.transform.SetParent(canvas.transform, false);
        }

        private void Awake()
        {
            var rect = (RectTransform)transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = new Vector2(-24f, -92f);
            rect.sizeDelta = new Vector2(324f, 216f);

            var panel = new GameObject("RadarPanel", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            panel.transform.SetParent(transform, false);
            Stretch((RectTransform)panel.transform);
            panel.GetComponent<Image>().color = new Color(0.025f, 0.055f, 0.07f, 0.95f);
            panel.GetComponent<Image>().raycastTarget = false;

            var map = new GameObject("MapImage", typeof(RectTransform), typeof(RawImage));
            map.transform.SetParent(panel.transform, false);
            _mapRect = (RectTransform)map.transform;
            Stretch(_mapRect);
            var image = map.GetComponent<RawImage>();
            image.raycastTarget = false;
            image.color = Color.white;
            _mapImage = image;
            var fit = map.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = 16f / 9f;

            var label = new GameObject("RadarTitle", typeof(RectTransform), typeof(Text));
            label.transform.SetParent(panel.transform, false);
            var title = label.GetComponent<Text>();
            title.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            title.text = "RADAR";
            title.fontSize = 14;
            title.color = Color.white;
            title.alignment = TextAnchor.MiddleLeft;
            title.raycastTarget = false;
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = new Vector2(0.04f, 0.90f);
            labelRect.anchorMax = new Vector2(0.6f, 0.99f);
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            _playerDot = CreateDot("PlayerDot", _mapRect, new Color32(85, 231, 107, 255), 9f);
        }

        private void OnEnable() { NetworkCombatAuthority.OnTeamRadarSpot += HandleSpot; SceneManager.sceneLoaded += HandleSceneLoaded; _captureAt = Time.unscaledTime + 0.5f; }
        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode) { _mapReady = false; _captureAt = Time.unscaledTime + 0.5f; ClearSpots(); }
        private void ClearSpots() { foreach (var spot in _spots.Values) if (spot.Dot != null) Destroy(spot.Dot.gameObject); _spots.Clear(); }
        private void OnDisable() { NetworkCombatAuthority.OnTeamRadarSpot -= HandleSpot; SceneManager.sceneLoaded -= HandleSceneLoaded; ClearSpots(); }

        private void Update()
        {
            ResolveOwner();
            if (_owner == null)
            {
                _playerDot.gameObject.SetActive(false);
                return;
            }
            if (!_mapReady && Time.unscaledTime >= _captureAt) CaptureMap();
            _playerDot.gameObject.SetActive(true);
            PlaceDot(_playerDot, _owner.transform.position);
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.2f;
                ScanVisibleEnemies();
                UpdateAllies();
            }
            UpdateSpots();
        }

        private void ResolveOwner()
        {
            if (_owner != null) return;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                if (player.IsOwnerPlayer && player.gameObject.activeInHierarchy)
                { _owner = player; return; }
            if (!FishNetLifecycleGuard.IsNetworkActive())
                _owner = FindFirstObjectByType<WeaponController>()?.GetComponentInParent<NetworkCombatAuthority>();
        }

        private void ScanVisibleEnemies()
        {
            if (_owner.IsDead || (_owner.TeamId != MatchRules.TeamRed && _owner.TeamId != MatchRules.TeamBlue)) return;
            var view = UnityEngine.Camera.main;
            if (view == null) return;
            foreach (var enemy in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (enemy == _owner || enemy.IsDead || enemy.TeamId == _owner.TeamId
                    || enemy.TeamId == MatchRules.TeamNone) continue;
                var eye = enemy.transform.position + Vector3.up * 1.2f;
                var viewport = view.WorldToViewportPoint(eye);
                if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) continue;
                Vector3 delta = eye - view.transform.position;
                float distance = delta.magnitude;
                if (distance > 85f) continue;
                bool blocked = false;
                foreach (var hit in Physics.RaycastAll(view.transform.position, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore))
                {
                    var hitPlayer = hit.collider.GetComponentInParent<NetworkCombatAuthority>();
                    if (hitPlayer == _owner || hitPlayer == enemy) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) _owner.ReportRadarSighting(enemy);
            }
        }

        private void HandleSpot(int id, Vector3 position)
        {
            if (_owner == null) return;
            if (!_spots.TryGetValue(id, out var spot))
            {
                spot = new Spot { Dot = CreateDot("Enemy_" + id, _mapRect, new Color32(246, 75, 75, 255), 10f) };
                _spots.Add(id, spot);
            }
            spot.Position = position;
            spot.ExpiresAt = Time.unscaledTime + SpotLifetimeSeconds;
        }

        private void UpdateSpots()
        {
            _expired.Clear();
            foreach (var pair in _spots)
            {
                var spot = pair.Value;
                if (Time.unscaledTime >= spot.ExpiresAt) { _expired.Add(pair.Key); continue; }
                var uv = _mapEntry != null ? _mapEntry.WorldToUv(spot.Position) : new Vector2(-1, -1);
                bool inRange = uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
                spot.Dot.gameObject.SetActive(inRange);
                if (inRange) PlaceDot(spot.Dot, spot.Position);
            }
            foreach (int id in _expired)
            {
                if (_spots[id].Dot != null) Destroy(_spots[id].Dot.gameObject);
                _spots.Remove(id);
            }
        }

        private void UpdateAllies()
        {
            foreach (var dot in _allies.Values) if (dot != null) dot.gameObject.SetActive(false);
            if (!MatchLifecycle.IsTeamMatch() || _owner.TeamId == MatchRules.TeamNone) return;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (player == _owner || player.IsDead || player.TeamId != _owner.TeamId) continue;
                int id = player.GetInstanceID();
                if (!_allies.TryGetValue(id, out var dot) || dot == null)
                    _allies[id] = dot = CreateDot("Ally_" + id, _mapRect, new Color32(85, 190, 245, 255), 8f);
                dot.gameObject.SetActive(true); PlaceDot(dot, player.transform.position);
            }
        }

        private void PlaceDot(RectTransform dot, Vector3 position)
        {
            if (_mapEntry == null) { dot.gameObject.SetActive(false); return; }
            var uv = _mapEntry.WorldToUv(position);
            dot.anchoredPosition = new Vector2((uv.x - .5f) * _mapRect.rect.width, (uv.y - .5f) * _mapRect.rect.height);
        }

        private void CaptureMap()
        {
            _captureAt = Time.unscaledTime + 1f;
            var catalog = Resources.Load<MapRadarCatalog>("MapRadarCatalog");
            var scene = _owner != null ? _owner.gameObject.scene.name : SceneManager.GetActiveScene().name;
            _mapEntry = catalog != null ? catalog.Find(scene) : null;
            if (_mapEntry?.image == null) { _mapImage.enabled = false; _mapReady = false; return; }
            _mapImage.texture = _mapEntry.image.texture;
            var rect = _mapEntry.image.textureRect;
            var texture = _mapEntry.image.texture;
            _mapImage.uvRect = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
            _mapImage.GetComponent<AspectRatioFitter>().aspectRatio = rect.width / rect.height;
            _mapImage.enabled = true;
            _mapReady = true;
        }

        private static RectTransform CreateDot(string name, Transform parent, Color color, float size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.sprite = RadarDotSprite.Get();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static class RadarDotSprite
        {
            private static Sprite _sprite;
            public static Sprite Get()
            {
                if (_sprite != null) return _sprite;
                var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        texture.SetPixel(x, y, (new Vector2(x - 7.5f, y - 7.5f)).sqrMagnitude <= 49f
                            ? Color.white : Color.clear);
                texture.Apply();
                _sprite = Sprite.Create(texture, new Rect(0, 0, 16, 16), new Vector2(0.5f, 0.5f));
                return _sprite;
            }
        }
    }
}
