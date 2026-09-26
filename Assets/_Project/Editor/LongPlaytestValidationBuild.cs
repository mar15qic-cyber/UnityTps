using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Game.EditorTools
{
    public static class LongPlaytestValidationBuild
    {
        public static void Build()
        {
            var settings = Directory.GetFiles("ProjectSettings").ToDictionary(p => p, File.ReadAllBytes);
            var status = "Logs/LongPlaytestValidationBuild.status";
            File.WriteAllText(status, "running");
            try
            {
                var clients = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                var servers = new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" }
                    .Select(s => "Assets/_Project/Scenes/" + s + ".unity").ToArray();
                BuildOne(servers, StandaloneBuildSubtarget.Server, "Server", "UnityFpsDedicatedServer.exe");
                BuildOne(clients, StandaloneBuildSubtarget.Player, "Client", "UnityFpsClient.exe");
                File.WriteAllText(status, "succeeded " + DateTime.UtcNow.ToString("O"));
            }
            catch (Exception e) { File.WriteAllText(status, "failed " + e); UnityEngine.Debug.LogException(e); }
            finally { foreach (var p in settings) File.WriteAllBytes(p.Key, p.Value); AssetDatabase.Refresh(); }
        }
        private static void BuildOne(string[] scenes, StandaloneBuildSubtarget target, string label, string exe)
        {
            var path = "Builds/LongPlaytestFix/" + label;
            Directory.CreateDirectory(path);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, target = BuildTarget.StandaloneWindows64,
                subtarget = (int)target, locationPathName = path + "/" + exe, options = BuildOptions.None });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception(label + " build failed: " + report.summary.totalErrors);
            BuildManifestWriter.WriteManifest(path, label);
        }
    }
}

