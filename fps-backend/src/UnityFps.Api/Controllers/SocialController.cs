using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/social")]
public sealed class SocialController(SocialService social) : ControllerBase
{
    private long UserId => AuthService.GetUserId(User);
    [HttpGet("inbox")]
    public Task<SocialInboxDto> Inbox(CancellationToken ct) => social.Inbox(UserId, ct);
    [HttpGet("messages/{peerId:long}")]
    public Task<DirectMessagePageDto> History(long peerId, CancellationToken ct, long before = 0, long after = 0) => social.History(UserId, peerId, before, after, ct);
    [HttpPost("messages/{peerId:long}")]
    public Task<DirectMessageDto> Send(long peerId, SendDirectMessageRequest request, CancellationToken ct) => social.Send(UserId, peerId, request, ct);
    [HttpPost("messages/{peerId:long}/read")]
    public async Task<IActionResult> Read(long peerId, ReadDirectMessageRequest request, CancellationToken ct) { await social.MarkRead(UserId, peerId, request.LastReadId, ct); return NoContent(); }
    [HttpPost("invitations")]
    public async Task<IActionResult> Invite(SendRoomInvitationRequest request, CancellationToken ct) { await social.Invite(UserId, request, ct); return NoContent(); }
    [HttpPost("invitations/{id:long}/accept")]
    public Task<RoomSnapshotDto> Accept(long id, AcceptRoomInvitationRequest request, CancellationToken ct) => social.Accept(UserId, id, request.ClientProtocolId, ct);
    [HttpPost("invitations/{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, CancellationToken ct) { await social.Reject(UserId, id, ct); return NoContent(); }
}
