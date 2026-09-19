using UnityEngine;
using Game.Account;
using Game.Gameplay.Settings;

namespace Game.UI
{

public sealed class AppRoot : MonoBehaviour
{
    public static AppRoot Instance { get; private set; }
    public IApiClient ApiClient { get; private set; }
    public AccountSession Session { get; private set; }

    [SerializeField] private ApiClientConfig apiConfig;

    public static AppRoot Ensure()
    {
        if (Instance != null) return Instance;
        var root = new GameObject("AppRoot");
        return root.AddComponent<AppRoot>();
    }

            private void Awake()
            {
                if (Instance != null && Instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
                Instance = this;
                DontDestroyOnLoad(gameObject);
                // 双开兜底独立日志（2026-09-16 实测事故）：默认 Player.log 被两个客户端进程共用，
                // 各自的文件偏移互相覆盖——同一文件两次读取内容都不同，整段取证不可恢复。
                // 唯一测试入口=项目根 启动客户端.cmd（每实例独立 -logFile 到 Tools\Client\Logs\）；
                // 本兜底只覆盖"直接双击 exe"的误用路径（无 -logFile 参数时启用）。
                InstallUniqueLogFallback();
                // 启动统一应用设置（Master/Music/SFX 音量 + 灵敏度实时层 + 锁帧/分辨率）：
                // 大厅与 Arena 共用同一持久值；直连 Arena 场景由 SettingsRuntime 的
                // RuntimeInitializeOnLoadMethod 兜底，二者幂等。
                SettingsRuntime.Initialize();
                if (apiConfig == null)
                {
                    apiConfig = ScriptableObject.CreateInstance<ApiClientConfig>();
                    apiConfig.name = "RuntimeApiClientConfig";
                }
                Session = new AccountSession();
                ApiClient = new ApiClient(apiConfig);
                // P0-A/P1 部署证据：客户端启动即打印协议代际 + 部署清单 + PID（双客户端唯一 -logFile
                // 由启动器分配——本行是"哪份二进制在跑"的进程内证据）
                var manifest = Game.Gameplay.Network.GameProtocolIdentity.TryReadDeployedManifest();
                UnityEngine.Debug.Log($"[AppBoot] APP_PROTOCOL id={Game.Gameplay.Network.GameProtocolIdentity.ProtocolId}"
                    + $" pid={System.Diagnostics.Process.GetCurrentProcess().Id}"
                    + $" buildId={(manifest != null ? manifest.buildId : "<none>")}"
                    + $" builtAt={(manifest != null ? manifest.builtAtUtc : "<none>")}");
                // 每玩家设置偏好同步（2026-09-07）：本对象跨场景常驻且持有 ApiClient/Session，
                // 由它驱动拉取（登录后）与推送（任意设置页 ApplyAndPersist 之后）。
                SettingsDraft.Persisted += OnSettingsPersisted;
                // 热更新运行时（稳定 seam）：随 AppRoot 常驻；Boot 阶段即跑 Lua 引导注册热页
                //（早于 Lobby 场景的 LobbyPresenter.Initialize）；引导失败不阻断大厅（日志可见）。
                // DS 无 AppRoot → 服务器上不会初始化 Lua（红线：DS 不加载热更脚本）。
                gameObject.AddComponent<HotUpdateRuntime>();
            }

            private void OnDestroy()
            {
                if (Instance != this) return;
                SettingsDraft.Persisted -= OnSettingsPersisted;
                (ApiClient as System.IDisposable)?.Dispose();
                Instance = null;
            }

            /// <summary>
            /// 双开独立日志兜底（2026-09-16 实测）：命令行没有 -logFile 时，把本进程日志镜像到
            /// `LocalLow/.../Logs/client-self-<pid>-<stamp>.log`（首字节写 pid/protocol/buildId）。
            /// 为什么放这里：Player.log 的混写损坏发生在**写入端**，任何"事后解析"都救不回被覆盖的行；
            /// 只有每个进程自己留一份完整副本才是可靠取证。启动器（启动客户端.cmd）已带独立
            /// -logFile，此时本兜底自动跳过，不产生双份日志。
            /// </summary>
            private static void InstallUniqueLogFallback()
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], "-logFile", System.StringComparison.OrdinalIgnoreCase))
                        return; // 启动器路径：日志已由启动器独立分配

                try
                {
                    string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                    string dir = System.IO.Path.Combine(
                        UnityEngine.Application.persistentDataPath, "Logs");
                    System.IO.Directory.CreateDirectory(dir);
                    string path = System.IO.Path.Combine(
                        dir, $"client-self-{pid}-{stamp}.log");

                    var manifest = Game.Gameplay.Network.GameProtocolIdentity.TryReadDeployedManifest();
                    System.IO.File.WriteAllText(path,
                        $"[AppBoot] pid={pid}"
                        + $" protocol={Game.Gameplay.Network.GameProtocolIdentity.ProtocolId}"
                        + $" buildId={(manifest != null ? manifest.buildId : "<none>")}"
                        + $" (self-log fallback: launched without -logFile)\n");

                    var writer = new System.IO.StreamWriter(path, append: true)
                    {
                        AutoFlush = true
                    };
                    Application.logMessageReceived += (condition, stackTrace, type) =>
                    {
                        // 混写防护：时间戳 + pid 前缀，任何一行都能独立归属
                        writer.WriteLine(
                            $"{System.DateTime.Now:HH:mm:ss.fff} [{pid}] {type} {condition}");
                    };
                    UnityEngine.Debug.Log($"[AppBoot] SELF_LOG_ENABLED path={path}"
                        + "（未用 启动客户端.cmd 启动——请改用项目根 启动客户端.cmd 作为唯一测试入口）");
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[AppBoot] 自兜底日志创建失败（不影响游戏）：{e.Message}");
                }
            }

            /// <summary>登录/注册成功后调用：服务器偏好覆盖本地（跨设备跟随账号；离线/未认证为无操作）。</summary>
            public void PullUserSettingsFromServer()
            {
                if (Session == null || !Session.IsAuthenticated) return;
                _ = PullUserSettingsAsync();
            }

            private async System.Threading.Tasks.Task PullUserSettingsAsync()
            {
                try
                {
                    var result = await ApiClient.GetUserSettingsAsync();
                    if (result.Success && result.Data?.values != null)
                        UserSettingsSync.ApplyRemote(result.Data.values);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[AppRoot] 拉取用户设置失败（保留本地值）: {ex.Message}");
                }
            }

            private void OnSettingsPersisted()
            {
                if (Session == null || !Session.IsAuthenticated) return;
                _ = PushUserSettingsAsync();
            }

            private async System.Threading.Tasks.Task PushUserSettingsAsync()
            {
                try
                {
                    var request = new SaveSettingsRequest { values = UserSettingsSync.CapturePayload() };
                    var result = await ApiClient.PutUserSettingsAsync(request);
                    if (!result.Success)
                        Debug.LogWarning($"[AppRoot] 推送用户设置失败: {result.Code}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[AppRoot] 推送用户设置异常: {ex.Message}");
                }
            }
        }
    }
