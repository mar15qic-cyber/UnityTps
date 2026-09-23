using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Game.EditorTools
{
    public static class VideoFollowupValidationBuild
    {
        public static void Build()
            => BuildTo("Builds/VideoFollowup0923", "Logs/VideoFollowupValidationBuild.status");

        public static void BuildTo(string outputRoot, string status)
        {
            var settings = Directory.GetFiles("ProjectSettings").ToDictionary(p => p, File.ReadAllBytes);
            var pending = Path.Combine(outputRoot, "VALIDATION_PENDING.txt");
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(pending, "Build validation pending. Do not use this directory as a verified client/server pair.");
            File.WriteAllText(status, "running");
            try
            {
                var inputs = BuildManifestWriter.ComputeInputDigest().Digest;
                var clients = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                var servers = new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard" }
                    .Select(s => "Assets/_Project/Scenes/" + s + ".unity").ToArray();
                BuildOne(servers, StandaloneBuildSubtarget.Server, "Server", "UnityFpsDedicatedServer.exe", inputs, outputRoot);
                BuildOne(clients, StandaloneBuildSubtarget.Player, "Client", "UnityFpsClient.exe", inputs, outputRoot);
                File.WriteAllText(status, "succeeded " + DateTime.UtcNow.ToString("O"));
                File.Delete(pending);
            }
            catch (Exception e) { File.WriteAllText(status, "failed " + e); UnityEngine.Debug.LogException(e); }
            finally { foreach (var p in settings) File.WriteAllBytes(p.Key, p.Value); AssetDatabase.Refresh(); }
        }
        private static void BuildOne(string[] scenes, StandaloneBuildSubtarget target, string label, string exe, string inputs, string outputRoot)
        {
            RequireUnchangedInputs(inputs);
            var path = Path.Combine(outputRoot, label);
            Directory.CreateDirectory(path);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, target = BuildTarget.StandaloneWindows64,
                subtarget = (int)target, locationPathName = path + "/" + exe, options = BuildOptions.None });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception(label + " build failed: " + report.summary.totalErrors);
            RequireUnchangedInputs(inputs);
            BuildManifestWriter.WriteManifest(path, label);
        }

        private static void RequireUnchangedInputs(string expected)
        {
            if (BuildManifestWriter.ComputeInputDigest().Digest != expected)
                throw new InvalidOperationException("Build inputs changed during validation build. Wait for concurrent editing to finish before rebuilding both targets.");
        }
    }
}


