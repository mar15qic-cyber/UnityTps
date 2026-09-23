using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

/// <summary>
/// 好友端点（2026-09-20 需求2）：列表（含在线状态/双向申请）+ 按 用户名#编码 发送申请 + 同意/拒绝/撤销/删除。
/// GET /api/friends 兼作在场心跳（节流刷新本人 LastSeenUtc）。
/// </summary>
[ApiController, Authorize, Route("api/friends")]
public sealed class FriendsController(FriendsService friends) : ControllerBase
{
    [HttpGet]
    public Task<FriendListDto> List(CancellationToken cancellationToken) =>
        friends.ListAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPost("requests")]
    public Task<FriendRequestEntryDto> Send(FriendSendRequest request, CancellationToken cancellationToken) =>
        friends.SendAsync(AuthService.GetUserId(User), request.Query, cancellationToken);

    [HttpPost("requests/{requestId}/accept")]
    public async Task<IActionResult> Accept(long requestId, CancellationToken cancellationToken)
    {
        await friends.AcceptAsync(AuthService.GetUserId(User), requestId, cancellationToken);
        return NoContent();
    }

    /// <summary>收件人=拒绝、发件人=撤销（同一删除语义）。</summary>
    [HttpDelete("requests/{requestId}")]
    public async Task<IActionResult> RemoveRequest(long requestId, CancellationToken cancellationToken)
    {
        await friends.RemoveRequestAsync(AuthService.GetUserId(User), requestId, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{friendUserId:long}")]
    public async Task<IActionResult> RemoveFriend(long friendUserId, CancellationToken cancellationToken)
    {
        await friends.RemoveFriendAsync(AuthService.GetUserId(User), friendUserId, cancellationToken);
        return NoContent();
    }
}
