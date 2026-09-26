using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    /// <summary>Resolves the live gameplay scene's authored Spawn_0..N markers.
    /// A persistent FishNet PlayerSpawner can retain references from an unloaded map,
    /// so respawn must not rely on its serialized array alone.</summary>
    public static class SceneSpawnPoints
    {
        public static Transform[] Current()
        {
            string requested = Environment.GetEnvironmentVariable("FPS_MAP_SCENE");
            Scene scene = !string.IsNullOrWhiteSpace(requested)
                ? SceneManager.GetSceneByName(requested) : default;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = SceneManager.GetActiveScene();
                if (!GameMapCatalog.IsGameplayScene(scene.name))
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var candidate = SceneManager.GetSceneAt(i);
                        if (candidate.isLoaded && GameMapCatalog.IsGameplayScene(candidate.name))
                        { scene = candidate; break; }
                    }
            }
            if (!scene.IsValid() || !scene.isLoaded || !GameMapCatalog.IsGameplayScene(scene.name))
                return Array.Empty<Transform>();
            var result = new List<Transform>(16);
            foreach (var root in scene.GetRootGameObjects()) Collect(root.transform, result);
            result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return result.ToArray();
        }

        private static void Collect(Transform node, List<Transform> result)
        {
            if (node.name.StartsWith("Spawn_", StringComparison.Ordinal)
                && int.TryParse(node.name.Substring(6), out _))
                result.Add(node);
            for (int i = 0; i < node.childCount; i++) Collect(node.GetChild(i), result);
        }
    }
}
