using System.Text.Json;

namespace UnityFps.Api.Common;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await WriteProblemAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Errors);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled API exception for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, ApiErrorCodes.ServerError, "服务器暂时不可用");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://httpstatuses.com/" + status,
            ["title"] = "请求失败",
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = code,
            ["traceId"] = context.TraceIdentifier
        };
        if (errors is not null) body["errors"] = errors;
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
