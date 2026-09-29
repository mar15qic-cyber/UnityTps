// Execute once through the existing editor bridge. Schedules work and immediately returns.
UnityEditor.EditorApplication.CallbackFunction runBuild = null;
runBuild = delegate
{
    UnityEditor.EditorApplication.update -= runBuild;
    var status = "Logs/SystemAudit0927/build.status";
    var settings = System.IO.Directory.GetFiles("ProjectSettings").ToDictionary(p => p, System.IO.File.ReadAllBytes);
    try
    {
        System.IO.Directory.CreateDirectory("Logs/SystemAudit0927");
        var before = Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest;
        System.IO.File.WriteAllText("Logs/SystemAudit0927/input-digest-before.txt", before);
        var clientScenes = UnityEditor.EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var serverScenes = new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" }
            .Select(s => "Assets/_Project/Scenes/" + s + ".unity").ToArray();
        for (int i = 0; i < 4; i++)
        {
            bool server = (i % 2) == 0;
            bool audit = i >= 2;
            string label = server ? "Server" : "Client";
            string output = audit ? "Builds/SystemAudit0927/" + label : server ? "Builds/Server" : "Builds/ReleaseClient";
            if (Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest != before)
                throw new System.InvalidOperationException("Source drift before " + output);
            System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(status, "building " + output + " " + System.DateTime.UtcNow.ToString("O"));
            var options = new UnityEditor.BuildPlayerOptions
            {
                scenes = server ? serverScenes : clientScenes,
                target = UnityEditor.BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? UnityEditor.StandaloneBuildSubtarget.Server : UnityEditor.StandaloneBuildSubtarget.Player),
                locationPathName = output + (server ? "/UnityFpsDedicatedServer.exe" : "/UnityFpsClient.exe"),
                options = UnityEditor.BuildOptions.None,
                extraScriptingDefines = audit ? new[] { "FPS_RUNTIME_AUDIT" } : new string[0],
            };
            var report = UnityEditor.BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new System.InvalidOperationException(output + " failed errors=" + report.summary.totalErrors);
            if (Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest != before)
                throw new System.InvalidOperationException("Source drift during " + output);
            Game.EditorTools.BuildManifestWriter.WriteManifest(output, audit ? label + "-RuntimeAudit" : label);
            System.IO.File.Copy(output + "/build-manifest.json", "Logs/SystemAudit0927/" + (audit ? "audit-" : "release-") + label + "-manifest.json", true);
        }
        System.IO.File.WriteAllText(status, "succeeded " + System.DateTime.UtcNow.ToString("O"));
    }
    catch (System.Exception exception)
    {
        System.IO.File.WriteAllText(status, "failed " + exception);
        UnityEngine.Debug.LogException(exception);
    }
    finally
    {
        foreach (var pair in settings) System.IO.File.WriteAllBytes(pair.Key, pair.Value);
    }
};
UnityEditor.EditorApplication.update += runBuild;
UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
return "Scheduled production and audit client/server pairs; inspect Logs/SystemAudit0927/build.status";
