var result = new System.Collections.Generic.List<object>();
var cecil = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Mono.Cecil").GetType("Mono.Cecil.AssemblyDefinition");
System.Func<object, string, object> get = (o, n) => o.GetType().GetProperty(n).GetValue(o, null);
System.Func<object, System.Collections.Generic.IEnumerable<object>> items = o => ((System.Collections.IEnumerable)o).Cast<object>();
foreach (var role in new[] { "Server", "Client" })
{
    var dataName = role == "Server" ? "UnityFpsDedicatedServer_Data" : "UnityFpsClient_Data";
    var path = "Builds/PrivateInvitations/PreviewV1/" + role + "/" + dataName + "/Managed/Game.Gameplay.dll";
    using (var assembly = (System.IDisposable)cecil.GetMethod("ReadAssembly", new[] { typeof(string) }).Invoke(null, new object[] { path }))
    {
        var types = items(get(get(assembly, "MainModule"), "Types"));
        var telemetry = types.Single(t => get(t, "FullName").ToString() == "Game.Gameplay.Network.PublicTestTelemetry");
        var init = items(get(telemetry, "Methods")).Single(m => get(m, "Name").ToString() == ".cctor");
        var enabled = items(get(get(init, "Body"), "Instructions")).Single(i => i.ToString().Contains("stsfld") && i.ToString().Contains("<Enabled>"));
        var alwaysOn = get(get(get(enabled, "Previous"), "OpCode"), "Code").ToString() == "Ldc_I4_1";
        var recorder = types.Single(t => get(t, "Name").ToString() == "PreviewNetworkRecorder");
        var boot = items(get(recorder, "Methods")).Single(m => get(m, "Name").ToString() == "Boot");
        var autoStart = items(get(boot, "CustomAttributes")).Any(a => get(get(a, "AttributeType"), "Name").ToString() == "RuntimeInitializeOnLoadMethodAttribute");
        var update = items(get(recorder, "Methods")).Single(m => get(m, "Name").ToString() == "Update");
        var instructions = items(get(get(update, "Body"), "Instructions")).Select(i => i.ToString()).ToArray();
        var silentLookup = instructions.Any(i => i.Contains("NetworkManager::get_Instances")) && !instructions.Any(i => i.Contains("InstanceFinder::get_NetworkManager"));
        var write = items(get(telemetry, "Methods")).Single(m => get(m, "Name").ToString() == "Write");
        var expectedRole = role.ToLowerInvariant();
        var correctRole = items(get(get(write, "Body"), "Instructions")).Any(i => get(get(i, "OpCode"), "Code").ToString() == "Ldstr" && get(i, "Operand").ToString() == expectedRole);
        if (!alwaysOn || !autoStart || !silentLookup || !correctRole) throw new System.InvalidOperationException("Telemetry binary verification failed: " + role);
        result.Add(new { role, telemetryAlwaysOn = alwaysOn, recorderAutoStart = autoStart, silentLookup, correctRole });
    }
}
var json = Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented);
System.IO.File.WriteAllText("Logs/PreviewV1/binary-verification.json", json);
return json;
