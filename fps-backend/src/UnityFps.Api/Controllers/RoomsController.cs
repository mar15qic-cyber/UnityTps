using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/rooms")]
public sealed class RoomsController(RoomService rooms) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<GameRoomDto>> Create(CreateRoomRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await rooms.CreateAsync(AuthService.GetUserId(User), request, cancellationToken));

    [HttpGet]
    public Task<IReadOnlyList<GameRoomDto>> List(CancellationToken cancellationToken) => rooms.ListAsync(cancellationToken);

    [HttpPost("{roomCode}/join")]
    public Task<GameRoomDto> Join(string roomCode, CancellationToken cancellationToken) =>
        rooms.JoinAsync(AuthService.GetUserId(User), roomCode, cancellationToken);

    [HttpPost("heartbeat")]
    public Task Heartbeat(CancellationToken cancellationToken) =>
        rooms.HeartbeatAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPost("leave")]
    public Task Leave(CancellationToken cancellationToken) =>
        rooms.LeaveAsync(AuthService.GetUserId(User), cancellationToken);
}
