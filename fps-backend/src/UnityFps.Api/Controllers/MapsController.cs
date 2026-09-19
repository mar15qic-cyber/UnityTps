using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;

namespace UnityFps.Api.Controllers;

/// <summary>地图目录（Docs/27 §4）：服务端常量白名单投影，客户端 UI/白名单提示数据源。</summary>
[ApiController, Authorize, Route("api/maps")]
public sealed class MapsController : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<MapCatalogDto> List() => MapCatalog.All;
}
