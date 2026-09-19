using System;
using System.Collections.Generic;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/27 Day1 §4.3 用例 1/2/6/7 锁定：DedicatedServerOptions 参数解析与运行模式判定。
    /// ① 缺 instanceId/port/backendUrl/serverKey 任一 → IsValid=false 且 ValidationError 点名缺失参数
    ///   ——引导层据此拒绝启动网络（DedicatedServerBootstrap.InitializeOnLoad 同判定）。
    /// ② 服务器模式三通道：UNITY_SERVER 宏 / 显式 -dedicatedServer / batch+服务器参数组；
    ///   普通客户端（含批处理跑测试进程）绝不能误入服务器路径。
    /// ③ 非服务器进程拿不到 serverKey（用例 6）。
    /// ④ -allowUnsafeLocalDebugAuth 在 Release 编译一律拒绝（用例 7）。
    /// </summary>
    public sealed class DedicatedServerOptionsTests
    {
        private static IReadOnlyList<string> ServerArgs(
            bool dedicatedFlag = true, string instanceId = "arena-01", string port = "7770",
            string backendUrl = "http://127.0.0.1:5080", string serverKey = "unit-test-server-key")
        {
            var args = new List<string>();
            if (dedicatedFlag) args.Add(DedicatedServerOptions.ArgDedicatedServer);
            if (instanceId != null) args.AddRange(new[] { DedicatedServerOptions.ArgInstanceId, instanceId });
            if (port != null) args.AddRange(new[] { DedicatedServerOptions.ArgPort, port });
            if (backendUrl != null) args.AddRange(new[] { DedicatedServerOptions.ArgBackendUrl, backendUrl });
            if (serverKey != null) args.AddRange(new[] { DedicatedServerOptions.ArgServerKey, serverKey });
            return args;
        }

        // ---- 用例 1：缺参明确失败 ----

        [Test]
        public void MissingInstanceId_IsInvalid_AndNamesTheArg()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(instanceId: null), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.True, "显式 -dedicatedServer 应进入服务器模式");
            Assert.That(options.IsValid, Is.False, "缺 instanceId 必须拒绝启动网络");
            Assert.That(options.ValidationError, Does.Contain(DedicatedServerOptions.ArgInstanceId));
        }

        [Test]
        public void MissingPort_IsInvalid_AndNamesTheArg()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(port: null), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsValid, Is.False, "缺 -port 必须拒绝启动网络");
            Assert.That(options.ValidationError, Does.Contain(DedicatedServerOptions.ArgPort));
        }

        [Test]
        public void NonNumericPort_IsInvalid()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(port: "not-a-port"), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsValid, Is.False, "非数字端口必须拒绝启动网络");
            Assert.That(options.ValidationError, Does.Contain(DedicatedServerOptions.ArgPort));
        }

        [Test]
        public void MissingBackendUrl_IsInvalid_AndNamesTheArg()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(backendUrl: null), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsValid, Is.False, "缺 -backendUrl 必须拒绝启动网络");
            Assert.That(options.ValidationError, Does.Contain(DedicatedServerOptions.ArgBackendUrl));
        }

        [Test]
        public void MissingServerKey_IsInvalid_AndNamesTheArg()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(serverKey: null), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsValid, Is.False, "缺 -serverKey 必须拒绝启动网络");
            Assert.That(options.ValidationError, Does.Contain(DedicatedServerOptions.ArgServerKey));
        }

        // ---- 用例 2：模式判定三通道 + 客户端防误入 ----

        [Test]
        public void FullServerArgs_AreValid_AndFieldsParsed()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(), isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.True);
            Assert.That(options.IsValid, Is.True, $"完整参数必须可启动：{options.ValidationError}");
            Assert.That(options.InstanceId, Is.EqualTo("arena-01"));
            Assert.That(options.Port, Is.EqualTo(7770));
            Assert.That(options.BackendUrl, Is.EqualTo("http://127.0.0.1:5080"));
            Assert.That(options.ServerKey, Is.EqualTo("unit-test-server-key"), "服务器模式应能读到 serverKey");
            Assert.That(options.DetectionReason, Is.EqualTo(DedicatedServerOptions.ArgDedicatedServer));
        }

        [Test]
        public void UnityServerDefine_EntersServerMode_EvenWithoutFlag()
        {
            // 服务器构建（StandaloneBuildSubtarget.Server 自动定义 UNITY_SERVER）无需 -dedicatedServer
            var options = DedicatedServerOptions.Parse(ServerArgs(dedicatedFlag: false), isBatchMode: false,
                isUnityServerDefine: true, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.True);
            Assert.That(options.DetectionReason, Is.EqualTo("UNITY_SERVER define"));
        }

        [Test]
        public void BatchMode_WithServerArgs_EntersServerMode()
        {
            // 批处理 + 服务器参数组（instanceId+serverKey）→ 服务器；这是批处理启动的兜底通道
            var options = DedicatedServerOptions.Parse(ServerArgs(dedicatedFlag: false), isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.True);
            Assert.That(options.DetectionReason, Is.EqualTo("batch + server args"));
        }

        [Test]
        public void BatchMode_WithoutServerArgs_NeverEntersServerMode()
        {
            // 批处理跑 EditMode 测试（-batchmode -runTests …）绝不能误入服务器路径
            var args = new[] { "Unity.exe", "-batchmode", "-runTests", "-projectPath", "E:\\somewhere" };
            var options = DedicatedServerOptions.Parse(args, isBatchMode: true,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.False, "无服务器参数组的批处理进程必须保持普通客户端");
            Assert.That(options.IsValid, Is.False);
            Assert.That(options.ServerKey, Is.Null, "非服务器进程绝不能拿到 serverKey（即使命令行带了别的密钥也轮不到）");
        }

        [Test]
        public void PlainEditorPlay_NeverEntersServerMode()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(), isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            // 上一个用例已验证带 -dedicatedServer 时进入服务器模式；此处验证不带标志的普通编辑器启动
            var plain = DedicatedServerOptions.Parse(new[] { "Unity.exe", "-projectPath", "E:\\UnityProject\\UnityFpsLowPoly" },
                isBatchMode: false, isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(plain.IsDedicatedServer, Is.False, "普通编辑器进程必须保持客户端");
            Assert.That(options.IsDedicatedServer, Is.True, "对照：显式标志才进入服务器模式");
        }

        // ---- 用例 6：非服务器进程拿不到 serverKey ----

        [Test]
        public void NonServerProcess_CannotReadServerKey_EvenWhenArgPresent()
        {
            // 命令行里带了 -serverKey 但没有任何服务器模式触发条件 → 模式=false → getter 返回 null
            var args = new[] { "Game.exe", DedicatedServerOptions.ArgServerKey, "leaked-key-should-be-invisible" };
            var options = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.False);
            Assert.That(options.ServerKey, Is.Null, "非服务器进程读取 serverKey 必须得到 null（密钥不落入客户端可见面）");
        }

        // ---- 用例 7：Release 编译拒绝 unsafe debug 通道 ----

        [Test]
        public void UnsafeDebugAuth_RejectedInReleaseBuild()
        {
            var args = ServerArgs();
            var release = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: true);
            Assert.That(release.AllowUnsafeLocalDebugAuth, Is.False, "Release 编译必须拒绝 -allowUnsafeLocalDebugAuth");

            var debuggable = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(debuggable.AllowUnsafeLocalDebugAuth, Is.False, "未请求该开关时（Editor/Development）也必须为 false");
        }

        [Test]
        public void UnsafeDebugAuth_AllowedOnlyWhenRequestedAndNotRelease()
        {
            var args = new List<string>(ServerArgs()) { DedicatedServerOptions.ArgAllowUnsafeLocalDebugAuth };
            var dev = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(dev.AllowUnsafeLocalDebugAuth, Is.True, "Editor/Development + 显式请求 = 允许（本地调试通道）");

            var release = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: true);
            Assert.That(release.AllowUnsafeLocalDebugAuth, Is.False, "Release 编译即使显式请求也必须拒绝");
        }

        // ---- Day2 F9：远端客户端超时（可选参数，非法回退默认） ----

        [Test]
        public void RemoteClientTimeout_DefaultsTo30s_WhenAbsent()
        {
            var options = DedicatedServerOptions.Parse(ServerArgs(), isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.RemoteClientTimeoutSeconds,
                Is.EqualTo(DedicatedServerOptions.DefaultRemoteClientTimeoutSeconds),
                "未提供 -remoteClientTimeout 时必须取弱网容忍默认 30s（F9：FishNet 默认 1800s 不可接受）");
            Assert.That(options.IsValid, Is.True, "可选参数缺失绝不影响启动有效性");
        }

        [Test]
        public void RemoteClientTimeout_ExplicitValue_IsApplied()
        {
            var args = new List<string>(ServerArgs())
            {
                DedicatedServerOptions.ArgRemoteClientTimeout, "15",
            };
            var options = DedicatedServerOptions.Parse(args, isBatchMode: false,
                isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.RemoteClientTimeoutSeconds, Is.EqualTo(15d).Within(0.001),
                "显式 -remoteClientTimeout 15 必须生效（目标区间 15~30s 下限）");
        }

        [Test]
        public void RemoteClientTimeout_InvalidOrOutOfBounds_FallsBackToDefault()
        {
            foreach (var value in new[] { "not-a-number", "0", "-5", "2", "9999" })
            {
                var args = new List<string>(ServerArgs())
                {
                    DedicatedServerOptions.ArgRemoteClientTimeout, value,
                };
                var options = DedicatedServerOptions.Parse(args, isBatchMode: false,
                    isUnityServerDefine: false, isReleaseBuild: false);
                Assert.That(options.RemoteClientTimeoutSeconds,
                    Is.EqualTo(DedicatedServerOptions.DefaultRemoteClientTimeoutSeconds),
                    $"非法/越界值（{value}）必须静默回退默认——超时非安全边界，不因它拒绝启动");
                Assert.That(options.IsValid, Is.True, $"非法可选参数（{value}）不得令启动失效");
            }
        }
    }
}
