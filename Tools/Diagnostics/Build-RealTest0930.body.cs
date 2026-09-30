// Execute once through the existing editor bridge. Schedules work and immediately returns.
System.IO.Directory.CreateDirectory("Logs/RealTest0930");
System.IO.File.WriteAllText("Logs/RealTest0930/build.status", "scheduled " + System.DateTime.UtcNow.ToString("O"));
UnityEditor.EditorApplication.CallbackFunction runBuild = null;
runBuild = delegate
{
    if(UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating) return;
    UnityEditor.EditorApplication.update -= runBuild;
    System.IO.File.WriteAllText("Logs/RealTest0930/build.status", "saving assets " + System.DateTime.UtcNow.ToString("O"));
    foreach(var generated in new[]{"Assets/Resources/PerformanceTestRunInfo.json","Assets/Resources/PerformanceTestRunInfo.json.meta","Assets/Resources/PerformanceTestRunSettings.json","Assets/Resources/PerformanceTestRunSettings.json.meta"}) if(System.IO.File.Exists(generated)) System.IO.File.Delete(generated);
    UnityEditor.AssetDatabase.SaveAssets();
    UnityEditor.AssetDatabase.DisallowAutoRefresh();
    var status = "Logs/RealTest0930/build.status";
    var settings = System.IO.Directory.GetFiles("ProjectSettings").Concat(new[]{"Assets/Settings/PC_RPAsset.asset","Assets/Settings/UniversalRenderPipelineGlobalSettings.asset","Assets/FishNet.Config.XML"}).Where(System.IO.File.Exists).ToDictionary(p => p, System.IO.File.ReadAllBytes);
    try
    {
        System.IO.Directory.CreateDirectory("Logs/RealTest0930");
        var filesBefore = System.IO.Directory.GetFiles("Assets","*",System.IO.SearchOption.AllDirectories).Concat(System.IO.Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"}).ToDictionary(p=>p,p=>{using(var sha=System.Security.Cryptography.SHA256.Create())using(var file=System.IO.File.OpenRead(p))return System.BitConverter.ToString(sha.ComputeHash(file));});
        System.IO.File.WriteAllLines("Logs/RealTest0930/input-files-before.txt",filesBefore.Select(p=>p.Key+"="+p.Value).ToArray());
        var before = Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest;
        System.IO.File.WriteAllText("Logs/RealTest0930/input-digest-before.txt", before);
        var clientScenes = UnityEditor.EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var serverScenes = new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" }
            .Select(s => "Assets/_Project/Scenes/" + s + ".unity").ToArray();
        for (int i = 0; i < 4; i++)
        {
            bool server = (i % 2) == 0;
            bool audit = i >= 2;
            string label = server ? "Server" : "Client";
            string output = "Builds/RealTest0930/" + (audit ? "Audit/" : "") + label;
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
                throw new System.InvalidOperationException(output + " failed errors=" + report.summary.totalErrors+" messages="+string.Join(";",report.steps.SelectMany(step=>step.messages).Where(m=>m.type==UnityEngine.LogType.Error || m.type==UnityEngine.LogType.Exception).Select(m=>m.content).ToArray()));
            foreach(var pair in settings) System.IO.File.WriteAllBytes(pair.Key,pair.Value);
            foreach(var generated in new[]{"Assets/Resources/PerformanceTestRunInfo.json","Assets/Resources/PerformanceTestRunInfo.json.meta","Assets/Resources/PerformanceTestRunSettings.json","Assets/Resources/PerformanceTestRunSettings.json.meta"}) if(System.IO.File.Exists(generated)) System.IO.File.Delete(generated);
            if (Game.EditorTools.BuildManifestWriter.ComputeInputDigest().Digest != before)
                {
                var changed=filesBefore.Keys.Where(p=>!System.IO.File.Exists(p)|| filesBefore[p]!=System.BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(System.IO.File.ReadAllBytes(p))));
                var added=System.IO.Directory.GetFiles("Assets","*",System.IO.SearchOption.AllDirectories).Where(p=>!filesBefore.ContainsKey(p));
                throw new System.InvalidOperationException("Source drift during " + output+" files="+string.Join(";",changed.Concat(added).ToArray()));
            }
            Game.EditorTools.BuildManifestWriter.WriteManifest(output, audit ? label + "-RuntimeAudit" : label);
            System.IO.File.Copy(output + "/build-manifest.json", "Logs/RealTest0930/" + (audit ? "audit-" : "release-") + label + "-manifest.json", true);
        }
        Game.EditorTools.HotUpdateBundleBuild.BuildMaps();
        foreach(var pair in settings) System.IO.File.WriteAllBytes(pair.Key,pair.Value);
            foreach(var generated in new[]{"Assets/Resources/PerformanceTestRunInfo.json","Assets/Resources/PerformanceTestRunInfo.json.meta","Assets/Resources/PerformanceTestRunSettings.json","Assets/Resources/PerformanceTestRunSettings.json.meta"}) if(System.IO.File.Exists(generated)) System.IO.File.Delete(generated);
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
        UnityEditor.AssetDatabase.AllowAutoRefresh();
    }
};
UnityEditor.EditorApplication.update += runBuild;
UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
return "Scheduled production and audit client/server pairs; inspect Logs/RealTest0930/build.status";
