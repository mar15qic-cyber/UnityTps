using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

/// <summary>
/// 玩家房间端点（Docs/27 v1 CF 等待房间契约）：创建/加入返回 RoomSnapshotDto（Waiting 不含票据），
/// 选边/准备/设置为成员操作，start 才租 DS/签票据；leave 幂等返回 204；heartbeat 为成员保活。
/// </summary>
[ApiController, Authorize, Route("api/rooms")]
public sealed class RoomsController(RoomService rooms, RoomChatService chat) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<RoomSnapshotDto>> Create(CreateRoomRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await rooms.CreateAsync(AuthService.GetUserId(User), request, cancellationToken));

    [HttpGet]
    public async Task<IReadOnlyList<GameRoomDto>> List(CancellationToken cancellationToken) => await rooms.ListAsync(cancellationToken);

    [HttpPost("join-by-code")]
    public Task<RoomSnapshotDto> JoinByCode(JoinRoomByCodeRequest request, CancellationToken ct) =>
        rooms.JoinAsync(AuthService.GetUserId(User), request.RoomCode, request.TeamId, request.ClientProtocolId, ct);

    [HttpGet("{roomId:long}")]
    public async Task<RoomSnapshotDto> Detail(long roomId, CancellationToken cancellationToken) =>
        await rooms.GetDetailAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), cancellationToken);

    [HttpPost("{roomId:long}/join")]
    public async Task<RoomSnapshotDto> Join(long roomId, [FromBody] RoomJoinRequest? request, CancellationToken cancellationToken) =>
        await rooms.JoinAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), request?.TeamId, request?.ClientProtocolId, cancellationToken);

    [HttpPost("{roomId:long}/team")]
    public async Task<RoomSnapshotDto> SetTeam(long roomId, RoomTeamRequest request, CancellationToken cancellationToken) =>
        await rooms.SetTeamAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), request.TeamId?.Trim(), cancellationToken);

    [HttpPost("{roomId:long}/ready")]
    public async Task<RoomSnapshotDto> SetReady(long roomId, RoomReadyRequest request, CancellationToken cancellationToken) =>
        await rooms.SetReadyAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), request.IsReady, cancellationToken);

    [HttpPost("{roomId:long}/settings")]
    public async Task<RoomSnapshotDto> UpdateSettings(long roomId, RoomSettingsRequest request, CancellationToken cancellationToken) =>
        await rooms.UpdateSettingsAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), request, cancellationToken);

    [HttpPost("{roomId:long}/start")]
    public async Task<StartMatchDto> Start(long roomId, CancellationToken cancellationToken) =>
        await rooms.StartAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), cancellationToken);

    /// <summary>返房 ack（Docs/27 §5.7）：退战斗不退房；Returning 后幂等返回快照。</summary>
    [HttpPost("{roomId:long}/return")]
    public async Task<RoomSnapshotDto> Return(long roomId, RoomReturnRequest request, CancellationToken cancellationToken) =>
        await rooms.ReturnAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), request.MatchId, cancellationToken);

    /// <summary>终局结果查询（Docs/27 §7.3）：TDM 权威快照 / KillRace 聚合 / Pending。</summary>
    [HttpGet("{roomId:long}/match-result")]
    public async Task<RoomMatchResultViewDto> MatchResult(long roomId, [FromQuery] string matchId, CancellationToken cancellationToken) =>
        await rooms.GetMatchResultAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), matchId, cancellationToken);

    /// <summary>发送聊天（Docs/27 §8.4）：等待房间/启动/返房走鉴权 HTTP；局内仅 Owner RPC（InMatch 调用被拒）。</summary>
    [HttpPost("{roomId:long}/chat")]
    public async Task<ActionResult<ChatMessageDto>> SendChat(long roomId, ChatSendRequest request, CancellationToken cancellationToken)
    {
        var (room, member) = await rooms.GetChatContextAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), allowSend: true, cancellationToken);
        return Ok(chat.Send(room, member, request));
    }

    /// <summary>增量拉取聊天（seq 游标）；队聊在投递层过滤（Docs/27 §8.3）。</summary>
    [HttpGet("{roomId:long}/chat")]
    public async Task<ActionResult<ChatFeedDto>> FetchChat(long roomId, [FromQuery] ulong after = 0, CancellationToken cancellationToken = default)
    {
        var (room, member) = await rooms.GetChatContextAsync(AuthService.GetUserId(User), await rooms.ResolveInternalCodeAsync(roomId, cancellationToken), allowSend: false, cancellationToken);
        return Ok(chat.Fetch(room, member, after));
    }

    /// <summary>成员保活心跳（Waiting 不连 DS，房间活性由成员心跳 + 实例心跳共同决定）。</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(CancellationToken cancellationToken)
    {
        await rooms.HeartbeatAsync(AuthService.GetUserId(User), cancellationToken);
        return NoContent();
    }

    [HttpPost("leave")]
    public async Task<IActionResult> Leave(CancellationToken cancellationToken)
    {
        await rooms.LeaveAsync(AuthService.GetUserId(User), cancellationToken);
        return NoContent();
    }
}
