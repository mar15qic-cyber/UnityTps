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

    [HttpGet("attachments")]
    public Task<LoadoutAttachmentsDto> GetAttachments(CancellationToken cancellationToken) => loadouts.GetAttachmentsAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPut("attachments")]
    public Task<LoadoutAttachmentsDto> PutAttachments(LoadoutAttachmentsRequest request, CancellationToken cancellationToken) => loadouts.UpdateAttachmentsAsync(AuthService.GetUserId(User), request, cancellationToken);

    [HttpGet("compatibility")]
    public Task<AttachmentCompatibilityDto[]> Compatibility(CancellationToken cancellationToken) => loadouts.GetCompatibilityAsync(AuthService.GetUserId(User), cancellationToken);
}
