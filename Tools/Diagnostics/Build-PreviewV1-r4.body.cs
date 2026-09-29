UnityEditor.EditorApplication.CallbackFunction build = null;
build = delegate
{
    UnityEditor.EditorApplication.update -= build;
    var status = "Logs/PreviewV1-r4/build.status";
    var settings = System.IO.Directory.GetFiles("ProjectSettings").ToDictionary(p => p, System.IO.File.ReadAllBytes);
    string originalVersion = UnityEditor.PlayerSettings.bundleVersion;
    var originalHttp = UnityEditor.PlayerSettings.insecureHttpOption;
    try
    {
        var config = UnityEngine.JsonUtility.FromJson<Game.Core.ClientReleaseEnvironment>(System.IO.File.ReadAllText("Logs/PreviewV1/client-environment.json"));
        string error;
        if (config == null || !config.TryValidate(out error) || config.releaseId != "PreviewV1") throw new System.InvalidOperationException("Preview environment invalid");
        UnityEditor.PlayerSettings.bundleVersion = "PreviewV1";
        UnityEditor.PlayerSettings.insecureHttpOption = UnityEditor.InsecureHttpOption.AlwaysAllowed;
        UnityEditor.AssetDatabase.SaveAssets();
        System.IO.File.WriteAllText(status, "hashing PreviewV1 inputs");
        var inputPaths = System.IO.Directory.GetFiles("Assets", "*", System.IO.SearchOption.AllDirectories).Concat(System.IO.Directory.GetFiles("ProjectSettings")).Concat(new[] { "Packages/manifest.json", "Packages/packages-lock.json" }).ToArray();
        System.Func<string, string> hashFile = path => { using (var hash = System.Security.Cryptography.SHA256.Create()) using (var stream = System.IO.File.OpenRead(path)) return System.BitConverter.ToString(hash.ComputeHash(stream)); };
        var fileHashes = inputPaths.ToDictionary(path => path, hashFile);
        System.IO.File.WriteAllText("Logs/PreviewV1-r4/inputs-before.json", Newtonsoft.Json.JsonConvert.SerializeObject(fileHashes));
        var digest = Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest;
        System.IO.File.WriteAllText("Logs/PreviewV1-r4/input-digest.txt", digest);
        for (int i = 0; i < 2; i++)
        {
            bool server = i % 2 == 0; bool audit = i >= 2;
            string role = server ? "Server" : "Client";
            string output = (audit ? "Builds/PreviewV1-r2-Audit/" : "Builds/Distributions/PreviewV1/PreviewV1-Host-Win64-r4/") + role;
            System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(status, "building PreviewV1 r4 " + (audit ? "Audit " : "Release ") + role);
            var scenes = server ? new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" }
                .Select(n => "Assets/_Project/Scenes/" + n + ".unity").ToArray()
                : UnityEditor.EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var report = UnityEditor.BuildPipeline.BuildPlayer(new UnityEditor.BuildPlayerOptions {
                scenes = scenes, target = UnityEditor.BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? UnityEditor.StandaloneBuildSubtarget.Server : UnityEditor.StandaloneBuildSubtarget.Player),
                locationPathName = output + (server ? "/UnityFpsDedicatedServer.exe" : "/UnityFpsClient.exe"),
                options = UnityEditor.BuildOptions.None,
                extraScriptingDefines = (server ? new[] { "FPS_PREVIEW_TELEMETRY" } : new[] { "FPS_PREVIEW_TELEMETRY", "PRIVATE_INVITE_TEST" }).Concat(audit ? new[] { "FPS_RUNTIME_AUDIT" } : new string[0]).ToArray()
            });
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new System.InvalidOperationException(role + " build failed");
            Game.EditorTools.BuildManifestWriter.WriteManifest(output, server ? "PreviewV1Server" : "PrivateInvitationPlayer");
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(output + "/build-manifest.json"));
            if (manifest["inputDigest"].ToString() != digest) { System.IO.File.WriteAllLines("Logs/PreviewV1-r4/input-drift.txt", inputPaths.Where(path => hashFile(path) != fileHashes[path]).ToArray()); throw new System.InvalidOperationException("Inputs changed during " + role + " build"); }
            manifest["packageRevision"] = "r4"; manifest["releaseId"] = "PreviewV1"; manifest["telemetryDefaultEnabled"] = true;
            System.IO.File.WriteAllText(output + "/build-manifest.json", manifest.ToString());
            if (!server) System.IO.File.WriteAllText(output + "/client-environment.json", UnityEngine.JsonUtility.ToJson(config, true));
        }
        System.IO.File.WriteAllText(status, "succeeded PreviewV1 r4 " + System.DateTime.UtcNow.ToString("O"));
    }
    catch (System.Exception exception) { System.IO.File.WriteAllText(status, "failed " + exception); UnityEngine.Debug.LogException(exception); }
    finally
    {
        UnityEditor.PlayerSettings.bundleVersion = originalVersion;
        UnityEditor.PlayerSettings.insecureHttpOption = originalHttp;
        foreach (var pair in settings) System.IO.File.WriteAllBytes(pair.Key, pair.Value);
    }
};
UnityEditor.EditorApplication.update += build;
UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
return "PreviewV1 client/server build scheduled; no gameplay test scheduled.";





