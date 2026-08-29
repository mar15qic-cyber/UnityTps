using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/shop")]
public sealed class ShopController(CommerceService commerce) : ControllerBase
{
    [HttpGet("catalog")]
    public Task<ShopCatalogDto> Catalog(CancellationToken cancellationToken) =>
        commerce.GetCatalogAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPost("purchases")]
    public Task<PurchaseResultDto> Purchase(PurchaseRequest request, CancellationToken cancellationToken) =>
        commerce.PurchaseAsync(AuthService.GetUserId(User), request, cancellationToken);
}
