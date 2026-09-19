using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

/// <summary>每玩家设置偏好（键位/音量/灵敏度等，键值对开放扩展）。</summary>
[ApiController, Authorize, Route("api/settings")]
public sealed class SettingsController(UserSettingsService settings) : ControllerBase
{
    [HttpGet]
    public Task<UserSettingsDto> Get(CancellationToken cancellationToken) =>
        settings.GetAsync(AuthService.GetUserId(User), cancellationToken);

    [HttpPut]
    public Task<UserSettingsDto> Put(SaveSettingsRequest request, CancellationToken cancellationToken) =>
        settings.SaveAsync(AuthService.GetUserId(User), request.Values, cancellationToken);
}
