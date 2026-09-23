using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>Build local verification binaries while preserving the user's serialized project settings.</summary>
    public static class PublicTestValidationBuild
    {
        [MenuItem("Tools/Public Test/Build Local Validation Pair")]
        public static void Build()
        {
            var backups = new Dictionary<string, byte[]>();
            foreach (var path in Directory.GetFiles("ProjectSettings", "*", SearchOption.TopDirectoryOnly))
                backups[path] = File.ReadAllBytes(path);
            var status = "Logs/PublicTestBuild.status";
            Directory.CreateDirectory("Logs");
            File.WriteAllText(status, "running");
            try
            {
                DedicatedServerBuild.BuildWindowsServerRelease();
                ClientBuild.BuildWindowsClient();
                RequireCurrentManifest("Builds/Server/build-manifest.json");
                RequireCurrentManifest("Builds/ReleaseClient/build-manifest.json");
                // Existing bundle builder clears this exact generated directory. Verify the target before calling it.
                var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var bundles = Path.GetFullPath(Path.Combine(project, "Logs/HotUpdate/bundles"));
                if (!bundles.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bundle path escaped project");
                HotUpdateBundleBuild.BuildMaps();
                File.WriteAllText(status, "succeeded-local-validation-only");
            }
            catch (Exception e)
            {
                File.WriteAllText(status, "failed: " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                foreach (var backup in backups) File.WriteAllBytes(backup.Key, backup.Value);
                AssetDatabase.Refresh();
            }
        }

        private static void RequireCurrentManifest(string path)
        {
            var manifest = JsonUtility.FromJson<Game.Gameplay.Network.DeployedBuildManifest>(File.ReadAllText(path));
            if (manifest.protocolId != Game.Gameplay.Network.GameProtocolIdentity.ProtocolId
                || manifest.inputDigest != BuildManifestWriter.ComputeInputDigest().Digest)
                throw new InvalidOperationException("Build missing or stale: " + path);
        }
    }
}
