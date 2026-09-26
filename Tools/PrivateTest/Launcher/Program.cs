using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text.Json;

internal static class Program
{
    internal static readonly string Root = AppContext.BaseDirectory;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(Root, "client-environment.json")), Json)!;
            config.Validate();
            if (args.Length == 1 && args[0] == "--join") { Join(config); return; }
            using var mutex = new Mutex(true, "Local\\UnityFpsPrivateLauncher", out var first);
            if (!first) { MessageBox.Show("启动助手已经打开。", "邀请测试"); return; }
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("请以普通用户双击开始游戏；仅组网助手需要管理员权限。");
            Application.Run(new Launcher(config));
        }
        catch (Exception e) { MessageBox.Show(e is InvalidOperationException ? e.Message : "配置或启动文件缺失，请重新解压完整测试包。", "无法启动"); }
    }
    internal static string? Cli => new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
        .Select(p => Path.Combine(p, "ZeroTier", "One", "zerotier-cli.bat")).FirstOrDefault(File.Exists);
    internal static async Task<string> RunCli(string arguments)
    {
        if (Cli == null) throw new InvalidOperationException("未安装 ZeroTier");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = "/d /s /c \"\"" + Cli + "\" " + arguments + "\"";
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await p.WaitForExitAsync(timeout.Token); } catch { if (!p.HasExited) p.Kill(true); throw; }
        var result = await output; await error;
        if (p.ExitCode != 0) throw new InvalidOperationException("组网状态需要管理员检查");
        return result;
    }
    private static void Join(Config c)
    {
        if (Cli == null) throw new InvalidOperationException("请先安装官方 ZeroTier，再重新开始。");
        using (var service = Process.Start(new ProcessStartInfo("sc.exe", "start ZeroTierOneService") { UseShellExecute = false, CreateNoWindow = true })) service?.WaitForExit(8000);
        // Network id was strictly validated; never accept arbitrary CLI operations from configuration.
        RunCli("join " + c.ZeroTierNetworkId).GetAwaiter().GetResult();
        try
        {
            using var info = JsonDocument.Parse(RunCli("-j info").GetAwaiter().GetResult());
            var id = info.RootElement.GetProperty("address").GetString();
            if (!string.IsNullOrEmpty(id))
            {
                Clipboard.SetText(id);
                MessageBox.Show("设备编号：" + id + "\n已复制到剪贴板，请发给主机批准。批准后回到启动助手点击开始游戏。", "等待设备授权");
            }
        }
        catch { MessageBox.Show("已申请加入。请在 ZeroTier 托盘菜单复制设备编号，发给主机批准。", "组网申请"); }
    }
}
internal sealed class Config
{
    public string NetworkMode { get; set; } = "";
    public string ZeroTierNetworkId { get; set; } = "";
    public string HostOverlayAddress { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string HotUpdateBaseUrl { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public bool InviteOnly { get; set; }
    public void Validate()
    {
        if (NetworkMode != "private-overlay" || !System.Text.RegularExpressions.Regex.IsMatch(ZeroTierNetworkId, "^[a-fA-F0-9]{16}$")) throw new InvalidOperationException("私有测试配置无效。");
        if (!IPAddress.TryParse(HostOverlayAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new InvalidOperationException("组网地址无效。");
        var b = ip.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168)) throw new InvalidOperationException("必须使用私有组网地址。");
        foreach (var value in new[] { ApiBaseUrl, HotUpdateBaseUrl })
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != HostOverlayAddress || uri.Scheme is not ("http" or "https") || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") throw new InvalidOperationException("连接地址不属于本次测试主机。");
    }
}
internal sealed class Launcher : Form
{
    readonly Config config;
    readonly Label state = new() { AutoSize = false, Height = 150, Dock = DockStyle.Top, Text = "首次需要安装组网工具并由主机批准设备。" };
    readonly Button start = new() { Text = "开始游戏", Width = 150, Height = 45 };
    readonly Button cancel = new() { Text = "取消检查", Width = 110, Height = 45 };
    CancellationTokenSource? operation;
    public Launcher(Config c)
    {
        config = c; Text = "邀请测试 · 开始游戏"; Width = 660; Height = 340; Font = new Font("Microsoft YaHei UI", 11); Padding = new Padding(24);
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var diagnostic = new Button { Text = "连接诊断", Width = 110, Height = 45 };
        var export = new Button { Text = "导出测试记录", Width = 145, Height = 45 };
        row.Controls.AddRange(new Control[] { start, diagnostic, export, cancel }); Controls.Add(row); Controls.Add(state);
        start.Click += async (_, _) => await Check(true); diagnostic.Click += async (_, _) => await Check(false);
        cancel.Click += (_, _) => operation?.Cancel();
        export.Click += (_, _) => { try { Export(); } catch { state.Text = "导出未完成，请确认日志目录存在并重新选择保存位置。"; } }; FormClosing += (_, _) => operation?.Cancel();
    }
    async Task Check(bool play)
    {
        if (operation != null) return;
        operation = new CancellationTokenSource(TimeSpan.FromSeconds(45)); start.Enabled = false;
        try
        {
            if (Program.Cli == null)
            {
                state.Text = "未安装 ZeroTier：请完成官方安装，然后再次点击开始游戏。";
                Process.Start(new ProcessStartInfo("https://www.zerotier.com/download/") { UseShellExecute = true }); return;
            }
            state.Text = "正在检查组网…";
            // Readiness also verifies host identity. If CLI requires elevation, only the bounded join helper elevates.
            bool joined = false;
            try
            {
                using var doc = JsonDocument.Parse(await Program.RunCli("-j listnetworks"));
                foreach (var n in doc.RootElement.EnumerateArray())
                    if (n.GetProperty("nwid").GetString() == config.ZeroTierNetworkId)
                    { joined = true; state.Text = "组网状态：" + n.GetProperty("status").GetString(); }
            }
            catch { /* standard users may not read ZeroTier's local control token */ }
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
            string? statusJson = null;
            try { statusJson = await http.GetStringAsync(config.ApiBaseUrl.TrimEnd('/') + "/api/test-status", operation.Token); } catch (HttpRequestException) { } catch (TaskCanceledException) when (!operation.IsCancellationRequested) { }
            if (statusJson == null && !joined)
            {
                state.Text = "需要加入测试网络；请允许组网助手的管理员请求。";
                try
                {
                    using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--join") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                    if (helper != null) await helper.WaitForExitAsync(operation.Token);
                }
                catch (System.ComponentModel.Win32Exception) { state.Text = "已取消管理员操作。可稍后重试。"; return; }
                state.Text = "已申请加入网络。请在 ZeroTier 托盘菜单复制设备编号发给主机批准，然后再次点击开始游戏。"; return;
            }
            if (statusJson == null) { state.Text += "\n尚不能访问主机：请确认设备已批准、组网在线且主机已开服。连接路径：未知。"; return; }
            using var status = JsonDocument.Parse(statusJson);
            if (status.RootElement.GetProperty("releaseId").GetString() != config.ReleaseId) { state.Text = "版本不兼容，请向主机获取新的测试包。"; return; }
            if (!status.RootElement.GetProperty("ready").GetBoolean()) { state.Text = "主机正在准备地图，请稍后重试。"; return; }
            using var manifest = JsonDocument.Parse(await http.GetStringAsync(config.HotUpdateBaseUrl.TrimEnd('/') + "/manifest.json", operation.Token));
            if (manifest.RootElement.GetProperty("releaseId").GetString() != config.ReleaseId) { state.Text = "热更版本不兼容，请联系主机。"; return; }
            state.Text = "准备完成。游戏 UDP 接入将在进入房间后验证；直连/中继路径：未知。";
            if (play)
            {
                var game = Path.Combine(Program.Root, "UnityFpsClient.exe");
                if (Process.GetProcessesByName("UnityFpsClient").Any()) { state.Text = "游戏已经运行，请切换到游戏窗口。"; return; }
                Process.Start(new ProcessStartInfo(game, "-publicTestTelemetry") { WorkingDirectory = Program.Root, UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) { state.Text = "检查已取消或超时，可重试。"; }
        catch { state.Text = "连接检查失败，请确认测试包完整并联系主机。"; }
        finally { operation.Dispose(); operation = null; start.Enabled = true; }
    }
    void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "测试记录|*.zip", FileName = "测试记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var script = Path.Combine(Program.Root, "Export-PlayerEvidence.ps1");
        var evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "DefaultCompany", "UnityFps", "PublicTestEvidence");
        var sources = Directory.Exists(evidence) ? new[] { evidence } : Array.Empty<string>();
        if (sources.Length != 1) { state.Text = "无法唯一定位测试日志，请先完成一次游戏或联系主机。"; return; }
        var p = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-EvidenceDirectory", sources[0], "-OutputZip", dialog.FileName }) p.ArgumentList.Add(arg);
        Process.Start(p); state.Text = "正在导出脱敏记录，请等待 ZIP 文件生成后发送给主机。";
    }
}
