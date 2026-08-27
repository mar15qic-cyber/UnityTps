using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/matches")]
public sealed class MatchesController(MatchService matches) : ControllerBase
{
    [HttpPost]
    public Task<MatchResultDto> Post(MatchSubmissionRequest request, CancellationToken cancellationToken) => matches.SubmitAsync(AuthService.GetUserId(User), request, cancellationToken);
}
