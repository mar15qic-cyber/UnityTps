using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Common;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/loadout")]
public sealed class LoadoutController(LoadoutService loadouts) : ControllerBase
{
    /// <summary>背包参数（1..3，缺省 1）→ 内部下标；越界 400。</summary>
    private static int BackpackIndex(int? backpack)
    {
        if (!BackpackPolicy.TryNormalizeParam(backpack, out var index))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "背包参数必须为 1-3");
        return index;
    }

    /// <summary>三背包全集（大厅/仓库一次拉取）。</summary>
    [HttpGet("backpacks")]
    public Task<BackpackSetDto> GetBackpacks(CancellationToken cancellationToken) =>
        loadouts.GetBackpackSetAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpGet]
    public Task<LoadoutDto> Get([FromQuery] int? backpack, CancellationToken cancellationToken) =>
        loadouts.GetAsync(AuthService.GetUserId(User), BackpackIndex(backpack), cancellationToken);

    [HttpPut]
    public Task<LoadoutDto> Put(LoadoutRequest request, [FromQuery] int? backpack, CancellationToken cancellationToken) =>
        loadouts.UpdateAsync(AuthService.GetUserId(User), BackpackIndex(backpack), request, cancellationToken);

    [HttpGet("attachments")]
    public Task<LoadoutAttachmentsDto> GetAttachments([FromQuery] int? backpack, CancellationToken cancellationToken) =>
        loadouts.GetAttachmentsAsync(AuthService.GetUserId(User), BackpackIndex(backpack), cancellationToken);

    [HttpPut("attachments")]
    public Task<LoadoutAttachmentsDto> PutAttachments(LoadoutAttachmentsRequest request, [FromQuery] int? backpack, CancellationToken cancellationToken) =>
        loadouts.UpdateAttachmentsAsync(AuthService.GetUserId(User), BackpackIndex(backpack), request, cancellationToken);

    [HttpGet("compatibility")]
    public Task<AttachmentCompatibilityDto[]> Compatibility(CancellationToken cancellationToken) =>
        loadouts.GetCompatibilityAsync(AuthService.GetUserId(User), cancellationToken);
}
