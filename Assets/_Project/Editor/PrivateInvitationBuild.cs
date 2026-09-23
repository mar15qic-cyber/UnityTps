using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools
{
    public static class PrivateInvitationBuild
    {
        [MenuItem("Tools/Private Test/Build Configured Pair")]
        public static void Build()
        {
            var backups = Directory.GetFiles("ProjectSettings").ToDictionary(p => p, File.ReadAllBytes);
            Directory.CreateDirectory("Logs"); File.WriteAllText("Logs/PrivateInvitationBuild.status", "running");
            try
            {
                var scenes = new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard" }
                    .Select(n => "Assets/_Project/Scenes/" + n + ".unity").ToArray();
                var output = "Builds/PrivateInvitationServer";
                Directory.CreateDirectory(output);
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes,
                    locationPathName = output + "/UnityFpsDedicatedServer.exe", target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Server, options = BuildOptions.None });
                if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Private DS build failed");
                BuildManifestWriter.WriteManifest(output, "Server");
                InvitationClientBuild.BuildFromConfig("Tools/PrivateTest/.runtime/client-environment.json");
                HotUpdateBundleBuild.BuildMaps();
                File.WriteAllText("Logs/PrivateInvitationBuild.status", "succeeded");
            }
            catch (Exception e) { File.WriteAllText("Logs/PrivateInvitationBuild.status", "failed: " + e.Message); throw; }
            finally { foreach (var pair in backups) File.WriteAllBytes(pair.Key, pair.Value); AssetDatabase.Refresh(); }
        }
    }
}
