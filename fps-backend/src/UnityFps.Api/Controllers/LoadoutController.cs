using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/loadout")]
public sealed class LoadoutController(LoadoutService loadouts) : ControllerBase
{
    [HttpGet]
    public Task<LoadoutDto> Get(CancellationToken cancellationToken) => loadouts.GetAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPut]
    public Task<LoadoutDto> Put(LoadoutRequest request, CancellationToken cancellationToken) => loadouts.UpdateAsync(AuthService.GetUserId(User), request, cancellationToken);
}
