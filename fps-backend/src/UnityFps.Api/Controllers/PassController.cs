using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/pass")]
public sealed class PassController(PassService pass) : ControllerBase
{
    [HttpGet]
    public Task<PassDto> Get(CancellationToken cancellationToken) =>
        pass.GetPassAsync(AuthService.GetUserId(User), cancellationToken);
}

[ApiController, Authorize, Route("api/achievements")]
public sealed class AchievementsController(PassService pass) : ControllerBase
{
    [HttpGet]
    public Task<AchievementDto[]> Get(CancellationToken cancellationToken) =>
        pass.GetAchievementsAsync(AuthService.GetUserId(User), cancellationToken);
}
