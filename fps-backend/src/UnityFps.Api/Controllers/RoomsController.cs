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
    public Task<IReadOnlyList<GameRoomDto>> List(CancellationToken cancellationToken) => rooms.ListAsync(cancellationToken);

    [HttpGet("{roomCode}")]
    public Task<RoomSnapshotDto> Detail(string roomCode, CancellationToken cancellationToken) =>
        rooms.GetDetailAsync(AuthService.GetUserId(User), roomCode, cancellationToken);

    [HttpPost("{roomCode}/join")]
    public Task<RoomSnapshotDto> Join(string roomCode, [FromBody] RoomJoinRequest? request, CancellationToken cancellationToken) =>
        rooms.JoinAsync(AuthService.GetUserId(User), roomCode, request?.TeamId, request?.ClientProtocolId, cancellationToken);

    [HttpPost("{roomCode}/team")]
    public Task<RoomSnapshotDto> SetTeam(string roomCode, RoomTeamRequest request, CancellationToken cancellationToken) =>
        rooms.SetTeamAsync(AuthService.GetUserId(User), roomCode, request.TeamId?.Trim(), cancellationToken);

    [HttpPost("{roomCode}/ready")]
    public Task<RoomSnapshotDto> SetReady(string roomCode, RoomReadyRequest request, CancellationToken cancellationToken) =>
        rooms.SetReadyAsync(AuthService.GetUserId(User), roomCode, request.IsReady, cancellationToken);

    [HttpPost("{roomCode}/settings")]
    public Task<RoomSnapshotDto> UpdateSettings(string roomCode, RoomSettingsRequest request, CancellationToken cancellationToken) =>
        rooms.UpdateSettingsAsync(AuthService.GetUserId(User), roomCode, request, cancellationToken);

    [HttpPost("{roomCode}/start")]
    public Task<StartMatchDto> Start(string roomCode, CancellationToken cancellationToken) =>
        rooms.StartAsync(AuthService.GetUserId(User), roomCode, cancellationToken);

    /// <summary>返房 ack（Docs/27 §5.7）：退战斗不退房；Returning 后幂等返回快照。</summary>
    [HttpPost("{roomCode}/return")]
    public Task<RoomSnapshotDto> Return(string roomCode, RoomReturnRequest request, CancellationToken cancellationToken) =>
        rooms.ReturnAsync(AuthService.GetUserId(User), roomCode, request.MatchId, cancellationToken);

    /// <summary>终局结果查询（Docs/27 §7.3）：TDM 权威快照 / KillRace 聚合 / Pending。</summary>
    [HttpGet("{roomCode}/match-result")]
    public Task<RoomMatchResultViewDto> MatchResult(string roomCode, [FromQuery] string matchId, CancellationToken cancellationToken) =>
        rooms.GetMatchResultAsync(AuthService.GetUserId(User), roomCode, matchId, cancellationToken);

    /// <summary>发送聊天（Docs/27 §8.4）：等待房间/启动/返房走鉴权 HTTP；局内仅 Owner RPC（InMatch 调用被拒）。</summary>
    [HttpPost("{roomCode}/chat")]
    public async Task<ActionResult<ChatMessageDto>> SendChat(string roomCode, ChatSendRequest request, CancellationToken cancellationToken)
    {
        var (room, member) = await rooms.GetChatContextAsync(AuthService.GetUserId(User), roomCode, allowSend: true, cancellationToken);
        return Ok(chat.Send(room, member, request));
    }

    /// <summary>增量拉取聊天（seq 游标）；队聊在投递层过滤（Docs/27 §8.3）。</summary>
    [HttpGet("{roomCode}/chat")]
    public async Task<ActionResult<ChatFeedDto>> FetchChat(string roomCode, [FromQuery] ulong after = 0, CancellationToken cancellationToken = default)
    {
        var (room, member) = await rooms.GetChatContextAsync(AuthService.GetUserId(User), roomCode, allowSend: false, cancellationToken);
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
