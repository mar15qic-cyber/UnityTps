using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/inventory")]
public sealed class InventoryController(CommerceService commerce) : ControllerBase
{
    [HttpGet]
    public Task<InventoryDto> Get(CancellationToken cancellationToken) =>
        commerce.GetInventoryAsync(AuthService.GetUserId(User), cancellationToken);
}
