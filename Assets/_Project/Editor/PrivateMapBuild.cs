using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    public static class PrivateMapBuild
    {
        [Serializable] public sealed class Request
        {
            public string mapId;
            public string scenePath;
            public string version;
            public string displayName;
            public string[] modes;
            public int maxCapacity;
        }
        public static void Build(string requestPath)
        {
            var r = JsonUtility.FromJson<Request>(File.ReadAllText(requestPath));
            if (!System.Text.RegularExpressions.Regex.IsMatch(r.mapId ?? "", "^[a-z0-9_]{1,32}$")
                || !long.TryParse(r.version, out var v) || v <= 0 || !r.scenePath.StartsWith("Assets/_Project/Scenes/", StringComparison.Ordinal)
                || r.scenePath.Contains("..") || !r.scenePath.EndsWith(".unity") || !File.Exists(r.scenePath)) throw new InvalidOperationException("Invalid map request");
            if (r.modes != null && (r.modes.Length == 0 || r.modes.Any(m => m != "TDM" && m != "KillRace"))
                || r.maxCapacity != 0 && r.maxCapacity != 2 && r.maxCapacity != 4 && r.maxCapacity != 8
                    && r.maxCapacity != 12 && r.maxCapacity != 16)
                throw new InvalidOperationException("Invalid map mode or capacity");
            var sceneName = Path.GetFileNameWithoutExtension(r.scenePath);
            if (!System.Text.RegularExpressions.Regex.IsMatch(sceneName, "^[a-zA-Z0-9_]{1,80}$")) throw new InvalidOperationException("Unsafe scene name");
            var output = "Builds/PrivateMaps/" + r.mapId + "/" + r.version;
            if (Directory.Exists(output)) throw new InvalidOperationException("Immutable map version exists");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            if (setup.Any(s => UnityEngine.SceneManagement.SceneManager.GetSceneByPath(s.path).isDirty)) throw new InvalidOperationException("Save scene edits first");
            try
            {
                var scene = EditorSceneManager.OpenScene(r.scenePath, OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                // Existing gameplay derives teams from PlayerSpawner's base points, not named scene groups.
                var spawner = roots.SelectMany(o => o.GetComponentsInChildren<FishNet.Component.Spawning.PlayerSpawner>(true)).SingleOrDefault();
                if (spawner == null || spawner.Spawns == null || spawner.Spawns.Length < 2 || spawner.Spawns.Any(s => s == null))
                    throw new InvalidOperationException("Map needs PlayerSpawner with at least two valid spawn points");
                if (spawner.Spawns.Max(s => s.position.x) - spawner.Spawns.Min(s => s.position.x) < 1f)
                    throw new InvalidOperationException("Spawn points do not form separate team regions");
                if (roots.SelectMany(o => o.GetComponentsInChildren<MonoBehaviour>(true)).Any(m => m == null)) throw new InvalidOperationException("Missing script in map");
                Directory.CreateDirectory(output + "/Content");
                var bundle = HotUpdateBundleBuild.BuildClientMaps(output + "/Content", new[] { new AssetBundleBuild {
                    assetBundleName = "maps/" + sceneName.ToLowerInvariant() + ".bundle", assetNames = new[] { r.scenePath } } });
                if (bundle == null) throw new InvalidOperationException("Bundle build failed");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { r.scenePath },
                    locationPathName = output + "/Server/UnityFpsDedicatedServer.exe", target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Server, options = BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Map DS build failed");
                BuildManifestWriter.WriteManifest(output + "/Server", "Server");
                File.WriteAllText(output + "/map-request.json", JsonUtility.ToJson(r, true));
            }
            finally
            {
                EditorUserBuildSettings.standaloneBuildSubtarget = previousSubtarget;
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }
    }
}
