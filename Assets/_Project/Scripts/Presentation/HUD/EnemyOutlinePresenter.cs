using System.Collections.Generic;
using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>Local team-aware silhouette pass. ZTest keeps outlines behind walls hidden.</summary>
    public sealed class EnemyOutlinePresenter : MonoBehaviour
    {
        private readonly Dictionary<SkinnedMeshRenderer, Material[]> _original = new();
        private Material _outline;
        private float _nextRefresh;

        private void OnEnable()
        {
            _outline = Resources.Load<Material>("EnemyOutline");
        }

        private void Update()
        {
            if (_outline == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + .5f;
            NetworkCombatAuthority local = null;
            var players = FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None);
            foreach (var player in players) if (player.IsOwnerPlayer) { local = player; break; }
            var wanted = new HashSet<SkinnedMeshRenderer>();
            if (local != null)
                foreach (var player in players)
                {
                    if (player == null || player == local || player.IsDead) continue;
                    bool teammate = MatchLifecycle.CurrentMode == MatchRules.ModeTdm
                        && local.TeamId != MatchRules.TeamNone && local.TeamId == player.TeamId;
                    if (teammate) continue;
                    var model = player.transform.Find("TP_Model");
                    if (model == null) continue;
                    foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                            wanted.Add(renderer);
                }
            foreach (var pair in new List<KeyValuePair<SkinnedMeshRenderer, Material[]>>(_original))
                if (!wanted.Contains(pair.Key)) Remove(pair.Key, pair.Value);
            foreach (var renderer in wanted)
            {
                if (_original.ContainsKey(renderer)) continue;
                var materials = renderer.sharedMaterials;
                _original[renderer] = materials;
                var extended = new Material[materials.Length + 1];
                materials.CopyTo(extended, 0);
                extended[materials.Length] = _outline;
                renderer.sharedMaterials = extended;
            }
        }

        private void Remove(SkinnedMeshRenderer renderer, Material[] original)
        {
            if (renderer != null)
            {
                var current = renderer.sharedMaterials;
                if (current.Length > 0 && current[current.Length - 1] == _outline)
                    renderer.sharedMaterials = original;
            }
            _original.Remove(renderer);
        }

        private void OnDisable()
        {
            foreach (var pair in new List<KeyValuePair<SkinnedMeshRenderer, Material[]>>(_original))
                Remove(pair.Key, pair.Value);
            _outline = null;
        }
    }
}
