using System;
using System.IO;
using System.Linq;
using Game.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools
{
    public static class InvitationClientBuild
    {
        [MenuItem("Tools/Client/Build Invitation Client")]
        public static void Build()
        {
            var path = Environment.GetEnvironmentVariable("FPS_RELEASE_CONFIG");
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Set FPS_RELEASE_CONFIG to the reviewed environment JSON before starting Unity.");
            BuildFromConfig(path);
        }

        public static void BuildFromConfig(string path)
        {
            var json = File.ReadAllText(path);
            var config = JsonUtility.FromJson<ClientReleaseEnvironment>(json);
            if (config == null || !config.TryValidate(out _) || !config.inviteOnly)
                throw new InvalidOperationException("Invalid public invitation environment");
            var output = config.IsPrivateOverlay ? "Builds/PrivateInvitationClient" : "Builds/InvitationClient";
            Directory.CreateDirectory(output);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = output + "/UnityFpsClient.exe",
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                extraScriptingDefines = new[] { config.IsPrivateOverlay ? "PRIVATE_INVITE_TEST" : "PUBLIC_INVITE_TEST" },
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Invitation build failed");
            // Serialize only public fields, never copy arbitrary properties from an operator's input file.
            File.WriteAllText(output + "/client-environment.json", JsonUtility.ToJson(config, true));
            BuildManifestWriter.WriteManifest(output, config.IsPrivateOverlay ? "PrivateInvitationPlayer" : "InvitationPlayer");
        }
    }
}
