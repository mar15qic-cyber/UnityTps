using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/profile")]
public sealed class ProfileController(ProfileService profiles) : ControllerBase
{
    [HttpGet]
    public Task<PlayerProfileDto> Get(CancellationToken cancellationToken) => profiles.GetAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPut("upgrades")]
    public Task<PlayerProfileDto> Upgrade(UpgradeRequest request, CancellationToken cancellationToken) => profiles.UpgradeAsync(AuthService.GetUserId(User), request, cancellationToken);
}
