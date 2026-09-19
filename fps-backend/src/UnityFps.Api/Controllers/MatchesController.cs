using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnityFps.Api.Features;
using UnityFps.Api.Services;

namespace UnityFps.Api.Controllers;

[ApiController, Authorize, Route("api/matches")]
public sealed class MatchesController(MatchService matches) : ControllerBase
{
    /// <summary>玩家提交入口（复审 R02：TDM 自报拒绝、对局内降级绕过封死；服务器结算走内部入口）。</summary>
    [HttpPost]
    public Task<MatchResultDto> Post(MatchSubmissionRequest request, CancellationToken cancellationToken) => matches.SubmitForPlayerAsync(AuthService.GetUserId(User), request, cancellationToken);

    /// <summary>战绩历史（热更试点 P3）：本人视角分页 + 生涯汇总——大厅战绩页数据源。</summary>
    [HttpGet("history")]
    public Task<MatchHistoryPageDto> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        => matches.GetHistoryAsync(AuthService.GetUserId(User), page, pageSize, HttpContext.RequestAborted);
}
