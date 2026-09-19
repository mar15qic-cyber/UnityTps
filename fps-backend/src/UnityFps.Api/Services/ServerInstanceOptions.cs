using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UnityFps.Api.Common;

namespace UnityFps.Api.Services;

/// <summary>
/// Dedicated Server 控制面配置（Docs/27 §2.2）。密钥只允许来自环境变量
/// （ServerInstances__ServerKey）或命令行参数；不得写入仓库、客户端资源或日志。
/// 未配置密钥时控制面端点整体 fail closed。
/// </summary>
public sealed class ServerInstanceOptions
{
    /// <summary>服务端密钥（X-Server-Key 比对值）；空 = 拒绝一切控制面调用。</summary>
    public string? ServerKey { get; set; }

    /// <summary>期望心跳间隔（秒），register 响应回告服务器。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>实例心跳 TTL（秒）：超时不再可租，绑定房间随之懒清理。</summary>
    public int InstanceTtlSeconds { get; set; } = 45;

    /// <summary>加入票据 TTL 秒数（Docs/27 §2.3 冻结 90 秒；下限 1 供测试缩短）。</summary>
    public int TicketTtlSeconds { get; set; } = 90;

    // ---- CF 等待房间/生命周期参数（Docs/27 v1 §3/§12；数值可调，语义冻结）----

    /// <summary>Waiting 成员心跳过期秒数（超时移除成员；初值 60，客户端心跳间隔 15s）。</summary>
    public int MemberStaleSeconds { get; set; } = 60;

    /// <summary>Starting 超时秒数：超时回 Waiting、释放实例、清准备（必须大于 DS 60s 加载门；初值 90）。</summary>
    public int StartingTimeoutSeconds { get; set; } = 90;

    /// <summary>Returning→Waiting 宽限秒数（全员 ack 或超时；初值 45）。</summary>
    public int ReturningTimeoutSeconds { get; set; } = 45;

    /// <summary>聊天令牌桶容量（Docs/27 §8.2 冻结 3；参数化供测试收紧）。</summary>
    public int ChatTokenCapacity { get; set; } = 3;

    /// <summary>聊天令牌回填秒数（每 capacity 桶回 1 个额度；Docs/27 §8.2 冻结 2s）。</summary>
    public int ChatTokenRefillSeconds { get; set; } = 2;

    /// <summary>每房间环形缓冲容量（R05 参数化；默认 100，测试可收紧验证去重独立于 Ring）。</summary>
    public int ChatRingCapacity { get; set; } = 100;

    /// <summary>每用户去重记录容量（R05 有界 per-user；FIFO 淘汰，默认 64）。</summary>
    public int ChatDedupCapacityPerUser { get; set; } = 64;

    public TimeSpan MemberTtl => TimeSpan.FromSeconds(Math.Clamp(MemberStaleSeconds, 1, 3600));
    public TimeSpan StartingTimeout => TimeSpan.FromSeconds(Math.Clamp(StartingTimeoutSeconds, 1, 3600));
    public TimeSpan ReturningTimeout => TimeSpan.FromSeconds(Math.Clamp(ReturningTimeoutSeconds, 1, 3600));
}

/// <summary>
/// 控制面端点鉴权（Docs/27 §2.2）：X-Server-Key 缺失 401 / 不匹配 403；
/// 比对使用常数时间比较，防时序侧信道；任何分支不回显期望值。
/// </summary>
public sealed class RequireServerKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<ServerInstanceOptions>>().Value;
        var provided = context.HttpContext.Request.Headers["X-Server-Key"].ToString();
        var expected = options.ServerKey;

        context.Result = string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)
            ? KeyProblem(StatusCodes.Status401Unauthorized, "缺少有效的服务器密钥")
            : FixedTimeEquals(expected, provided)
                ? null
                : KeyProblem(StatusCodes.Status403Forbidden, "服务器密钥无效");
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static ObjectResult KeyProblem(int status, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = "请求失败",
            Detail = detail
        };
        problem.Extensions["code"] = ApiErrorCodes.ServerKeyInvalid;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
