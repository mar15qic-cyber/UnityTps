using System;
using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// Dedicated Server 命令行参数与运行模式判定（Docs/27 Day1 §4.1 C1）。
    /// 解析/判定全部为纯逻辑，EditMode 直接锁定语义：
    /// ① 服务器模式进入通道（按优先级）：UNITY_SERVER 编译宏 > 显式 -dedicatedServer >
    ///    batch 模式且携带服务器参数组（instanceId+serverKey）。普通客户端（含批处理跑测试）
    ///    绝不能误入服务器路径——批处理但不带服务器参数的一律判定为非服务器。
    /// ② serverKey 只在服务器模式下可读；非服务器进程 get 返回 null，且无任何公开 setter。
    /// ③ -allowUnsafeLocalDebugAuth 仅非 Release 编译可生效（Release 一律拒绝，fail closed）。
    /// ④ 缺 instanceId/port/backendUrl/serverKey 任一项 → IsValid=false，调用方不得启动网络。
    /// </summary>
    public sealed class DedicatedServerOptions
    {
        public const string ArgDedicatedServer = "-dedicatedServer";
        public const string ArgInstanceId = "-instanceId";
        public const string ArgPort = "-port";
        public const string ArgPublicAddress = "-publicAddress";
        public const string ArgBackendUrl = "-backendUrl";
        public const string ArgServerKey = "-serverKey";
        public const string ArgBuildVersion = "-buildVersion";
        public const string ArgCapacity = "-capacity";
        public const string ArgRemoteClientTimeout = "-remoteClientTimeout";
        public const string ArgAllowUnsafeLocalDebugAuth = "-allowUnsafeLocalDebugAuth";
        public const string ArgMapId = "-mapId";

        /// <summary>Phase 8：本实例绑定的地图（Docs/27 §4 目录键）。可选参数，缺省 arena——
        /// 决定启动加载的目标场景（经 Unity 侧地图目录解析出 SceneName），并随注册/心跳上报，
        /// 后端租用查询按房间 mapId 匹配（不匹配实例不被租用）。</summary>
        public string MapId { get; private set; } = "arena";

        /// <summary>
        /// 远端客户端无数据超时默认值（秒，Day2 F9：FishNet Tugboat 默认 1800s=30 分钟，
        /// 被杀客户端的连接会在服务器连接表滞留同时长）。取目标区间 15~30s 的弱网容忍上限；
        /// 可经 -remoteClientTimeout 覆盖（客户端进程同值用于死服务器检测）。
        /// </summary>
        public const double DefaultRemoteClientTimeoutSeconds = 30.0;

        /// <summary>-remoteClientTimeout 允许范围（秒）：下限避开 Wi-Fi/弱网抖动误杀，上限防僵尸连接回潮。</summary>
        public const double MinRemoteClientTimeoutSeconds = 5.0;
        public const double MaxRemoteClientTimeoutSeconds = 300.0;

        /// <summary>进入服务器模式的判定通道（日志/测试用）。</summary>
        public string DetectionReason { get; private set; } = string.Empty;

        public bool IsDedicatedServer { get; private set; }

        public string InstanceId { get; private set; } = string.Empty;
        public ushort Port { get; private set; }
        public string PublicAddress { get; private set; } = "127.0.0.1";
        public string BackendUrl { get; private set; } = string.Empty;
        public string BuildVersion { get; private set; } = "dev";
        public int Capacity { get; private set; } = 4;
        public bool AllowUnsafeLocalDebugAuth { get; private set; }

        /// <summary>
        /// 远端客户端无数据超时（秒）。可选参数 -remoteClientTimeout；未提供或非法值时取
        /// DefaultRemoteClientTimeoutSeconds（不参与 IsValid——缺失不必填，非法静默回退默认并记入解析注释）。
        /// </summary>
        public double RemoteClientTimeoutSeconds { get; private set; } = DefaultRemoteClientTimeoutSeconds;

        /// <summary>服务器模式且四项必填参数齐备时为 true；false 时调用方必须拒绝启动网络。</summary>
        public bool IsValid { get; private set; }

        /// <summary>IsValid=false 时的原因（含缺失参数名），供日志一次性输出。</summary>
        public string ValidationError { get; private set; } = string.Empty;

        private string _serverKey = string.Empty;

        /// <summary>服务端密钥：仅服务器模式可读；非服务器进程恒为 null（不落入客户端内存可见面）。</summary>
        public string ServerKey => IsDedicatedServer ? _serverKey : null;

        /// <summary>
        /// 从命令行参数解析。isUnityServerDefine 对应 UNITY_SERVER 宏（服务器构建目标自动定义）；
        /// isReleaseBuild 运行时取 !Debug.isDebugBuild（Editor/Development 为 false）。
        /// </summary>
        public static DedicatedServerOptions Parse(
            IReadOnlyList<string> args, bool isBatchMode, bool isUnityServerDefine, bool isReleaseBuild)
        {
            var result = new DedicatedServerOptions();
            if (args == null || args.Count == 0)
                return result;

            bool hasDedicatedArg = TryGetFlag(args, ArgDedicatedServer);
            string instanceId = GetValue(args, ArgInstanceId) ?? string.Empty;
            string serverKey = GetValue(args, ArgServerKey) ?? string.Empty;

            // ① 模式判定（三通道，按优先级短路）
            if (isUnityServerDefine)
            {
                result.IsDedicatedServer = true;
                result.DetectionReason = "UNITY_SERVER define";
            }
            else if (hasDedicatedArg)
            {
                result.IsDedicatedServer = true;
                result.DetectionReason = ArgDedicatedServer;
            }
            else if (isBatchMode && !string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(serverKey))
            {
                // 批处理且同时带 instanceId+serverKey 才视为服务器：纯跑测试/纯批处理客户端不会误入
                result.IsDedicatedServer = true;
                result.DetectionReason = "batch + server args";
            }
            else
            {
                result.IsDedicatedServer = false;
                result.DetectionReason = string.Empty;
                return result;
            }

            // ② 参数填充（仅在服务器模式下才有意义）
            result.InstanceId = instanceId;
            result._serverKey = serverKey;

            string portText = GetValue(args, ArgPort);
            ushort.TryParse(portText, out ushort port);
            result.Port = port;

            result.PublicAddress = string.IsNullOrEmpty(GetValue(args, ArgPublicAddress))
                ? "127.0.0.1"
                : GetValue(args, ArgPublicAddress);
            result.BackendUrl = GetValue(args, ArgBackendUrl) ?? string.Empty;
            result.BuildVersion = string.IsNullOrEmpty(GetValue(args, ArgBuildVersion))
                ? "dev"
                : GetValue(args, ArgBuildVersion);
            // Phase 8：地图绑定（可选，缺省 arena；非法值不 fail 启动——后端租用匹配会把
            // 未知地图实例闲置，日志可见，避免把参数错误升级成"服务器完全不可用"）
            result.MapId = string.IsNullOrEmpty(GetValue(args, ArgMapId))
                ? "arena"
                : GetValue(args, ArgMapId).Trim();

            if (int.TryParse(GetValue(args, ArgCapacity), out int capacity) && capacity > 0)
                result.Capacity = capacity;

            // 可选：远端客户端超时（Day2 F9）。非法/越界一律回退默认——不 fail 启动（超时非安全边界，
            // 只影响清理收敛速度；让非法值把整个服务器拒之门外得不偿失）
            if (double.TryParse(GetValue(args, ArgRemoteClientTimeout), out double timeoutSeconds)
                && timeoutSeconds >= MinRemoteClientTimeoutSeconds
                && timeoutSeconds <= MaxRemoteClientTimeoutSeconds)
            {
                result.RemoteClientTimeoutSeconds = timeoutSeconds;
            }

            // ③ unsafe debug 通道：参数显式请求 + 非 Release 编译，二者缺一即拒绝
            bool unsafeRequested = TryGetFlag(args, ArgAllowUnsafeLocalDebugAuth);
            result.AllowUnsafeLocalDebugAuth = unsafeRequested && !isReleaseBuild;

            // ④ 必填项校验（缺任一即拒绝启动网络）
            var missing = new List<string>();
            if (string.IsNullOrEmpty(result.InstanceId)) missing.Add(ArgInstanceId);
            if (result.Port == 0) missing.Add(ArgPort);
            if (string.IsNullOrEmpty(result.BackendUrl)) missing.Add(ArgBackendUrl);
            if (string.IsNullOrEmpty(result._serverKey)) missing.Add(ArgServerKey);

            result.IsValid = missing.Count == 0;
            result.ValidationError = missing.Count == 0
                ? string.Empty
                : "missing/invalid: " + string.Join(" ", missing);
            return result;
        }

        /// <summary>运行时入口：读本进程命令行 + batch 状态 + UNITY_SERVER 宏。</summary>
        public static DedicatedServerOptions ParseFromCommandLine()
        {
#if UNITY_SERVER
            const bool unityServerDefine = true;
#else
            const bool unityServerDefine = false;
#endif
            return Parse(
                Environment.GetCommandLineArgs(),
                UnityEngine.Application.isBatchMode,
                unityServerDefine,
                !UnityEngine.Debug.isDebugBuild);
        }

        private static bool TryGetFlag(IReadOnlyList<string> args, string name)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetValue(IReadOnlyList<string> args, string name)
        {
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
