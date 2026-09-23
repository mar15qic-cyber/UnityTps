using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

/// <summary>
/// Dedicated Server 控制面端点（Docs/27 §2.2）：仅服务器进程调用（X-Server-Key），
/// 与玩家 JWT 完全无关；密钥错误整体 fail closed。
/// </summary>
[ApiController, AllowAnonymous, RequireServerKey, Route("api/server-instances")]
public sealed class ServerInstancesController(ServerInstanceService instances, RoomService rooms) : ControllerBase
{
    [HttpPost("{instanceId}/maintenance")]
    public async Task<IActionResult> Maintenance(string instanceId, CancellationToken cancellationToken)
    {
        await instances.EnterMaintenanceAsync(instanceId, cancellationToken);
        return NoContent();
    }
    [HttpPost("register")]
    public Task<ServerInstanceRegisterDto> Register(ServerInstanceRegisterRequest request, CancellationToken cancellationToken) =>
        instances.RegisterAsync(request, cancellationToken);

    [HttpPost("{instanceId}/heartbeat")]
    public async Task<IActionResult> Heartbeat(string instanceId, ServerInstanceHeartbeatRequest request, CancellationToken cancellationToken)
    {
        await instances.HeartbeatAsync(instanceId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("tickets/consume")]
    public Task<JoinTicketConsumeDto> ConsumeTicket(JoinTicketConsumeRequest request, CancellationToken cancellationToken) =>
        instances.ConsumeTicketAsync(request, cancellationToken);

    /// <summary>
    /// 实例池状态查询（P0 开发诊断，2026-09-08）：RequireServerKey 保护（开发环境以
    /// curl -H "X-Server-Key: ..." /api/server-instances/pool?requestedCapacity=8 查询）；
    /// 返回汇总（readyFresh/readyStale/reserved/inMatch/offline/stale/容量不足）与逐实例行
    /// （状态/绑定房间码/人数/容量/心跳年龄/新鲜度）。不含密钥/票据/连接串。
    /// </summary>
    [HttpGet("pool")]
    public Task<ServerInstancePoolDto> GetPool([FromQuery] int? requestedCapacity, CancellationToken cancellationToken) =>
        instances.GetPoolDiagnosticsAsync(requestedCapacity ?? 0, cancellationToken);

    /// <summary>
    /// 服务器上报玩家掉线（Day2 掉线成员清理 + P0 实例租约闭环 2026-09-08）：DS 在远端连接停止
    /// （主动退出/超时/被踢）后调用；幂等——真实移除与 no-op 一律返回 200 + 权威事实
    /// { roomCode, remainingPlayers, instanceState }（DS 仅在 instanceState=Ready 且
    /// remainingPlayers=0 时清空本地绑定转 Ready，404/409 不得猜 Ready）；
    /// 实例未知 404；实例未绑定/绑定其他房间 409。
    /// 鉴权沿用类级 RequireServerKey（与 register/heartbeat/consume 完全一致，玩家 JWT 不被接受）。
    /// </summary>
    [HttpPost("{instanceId}/players/disconnect")]
    public Task<ServerPlayerDisconnectReportDto> ReportPlayerDisconnect(string instanceId, ServerPlayerDisconnectReportRequest request, CancellationToken cancellationToken)
        => rooms.ReportPlayerDisconnectAsync(instanceId, request, cancellationToken: cancellationToken);

    /// <summary>
    /// DS 权威终局上报（Docs/27 §7.2）：TDM 结果登记 + InMatch→Returning + 释放实例 +
    /// 对 rewardEligible 玩家逐个结算（matchId 幂等，重复上报零重复奖励）。
    /// </summary>
    [HttpPost("{instanceId}/match-result")]
    public Task<MatchResultReportDto> ReportMatchResult(string instanceId, MatchResultReportRequest request, CancellationToken cancellationToken)
        => rooms.ReportMatchResultAsync(instanceId, request, cancellationToken);
}
