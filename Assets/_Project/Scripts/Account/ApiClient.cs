using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Core;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Account
{

public sealed class ApiClient : IApiClient, IDisposable
{
    private readonly string baseUrl;
    private readonly int timeoutSeconds;
    private readonly HashSet<string> inFlightOperations = new HashSet<string>(StringComparer.Ordinal);
    private readonly object operationGate = new object();
    private string token;
    private bool disposed;

    public ApiClient(ApiClientConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        baseUrl = ApiClientConfig.NormalizeBaseUrl(config.BaseUrl);
        timeoutSeconds = config.TimeoutSeconds;
        if (!config.TryValidate(out var validationError)) throw new ArgumentException(validationError, nameof(config));
    }

    public void SetToken(string value) => token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void ClearToken() => token = null;

    public Task<ApiResult<HealthDto>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HealthDto>("health", UnityWebRequest.kHttpVerbGET, "/health", null, cancellationToken);

    public Task<ApiResult<AuthSessionDto>> RegisterAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<AuthSessionDto>("register", UnityWebRequest.kHttpVerbPOST, "/api/auth/register", new RegisterRequest { username = username, password = password }, cancellationToken);

    public Task<ApiResult<AuthSessionDto>> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<AuthSessionDto>("login", UnityWebRequest.kHttpVerbPOST, "/api/auth/login", new LoginRequest { username = username, password = password }, cancellationToken);

    public Task<ApiResult<PlayerProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default) =>
        SendAsync<PlayerProfileDto>("profile-get", UnityWebRequest.kHttpVerbGET, "/api/profile", null, cancellationToken);

    public Task<ApiResult<PlayerProfileDto>> UpdateUpgradesAsync(UpgradeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PlayerProfileDto>("profile-upgrades", UnityWebRequest.kHttpVerbPUT, "/api/profile/upgrades", request, cancellationToken);

    public Task<ApiResult<LoadoutDto>> GetLoadoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutDto>("loadout-get", UnityWebRequest.kHttpVerbGET, "/api/loadout", null, cancellationToken);

    public Task<ApiResult<LoadoutDto>> UpdateLoadoutAsync(LoadoutRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutDto>("loadout-update", UnityWebRequest.kHttpVerbPUT, "/api/loadout", request, cancellationToken);

    public Task<ApiResult<LoadoutAttachmentsDto>> GetLoadoutAttachmentsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutAttachmentsDto>("loadout-attachments-get", UnityWebRequest.kHttpVerbGET, "/api/loadout/attachments", null, cancellationToken);

    public Task<ApiResult<LoadoutAttachmentsDto>> UpdateLoadoutAttachmentsAsync(LoadoutAttachmentsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutAttachmentsDto>("loadout-attachments-update", UnityWebRequest.kHttpVerbPUT, "/api/loadout/attachments", request, cancellationToken);

    public Task<ApiResult<AttachmentCompatibilityDto[]>> GetAttachmentCompatibilityAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AttachmentCompatibilityDto[]>("attachment-compatibility-get", UnityWebRequest.kHttpVerbGET, "/api/loadout/compatibility", null, cancellationToken);

    public Task<ApiResult<ShopCatalogDto>> GetShopCatalogAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ShopCatalogDto>("shop-catalog-get", UnityWebRequest.kHttpVerbGET, "/api/shop/catalog", null, cancellationToken);

    public Task<ApiResult<InventoryDto>> GetInventoryAsync(CancellationToken cancellationToken = default) =>
        SendAsync<InventoryDto>("inventory-get", UnityWebRequest.kHttpVerbGET, "/api/inventory", null, cancellationToken);

    public Task<ApiResult<PurchaseResultDto>> PurchaseAsync(PurchaseRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PurchaseResultDto>("purchase-" + (request?.idempotencyKey ?? string.Empty), UnityWebRequest.kHttpVerbPOST, "/api/shop/purchases", request, cancellationToken);

    public Task<ApiResult<MatchResultDto>> SubmitMatchAsync(MatchSubmissionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MatchResultDto>("match-submit", UnityWebRequest.kHttpVerbPOST, "/api/matches", request, cancellationToken);

    // ---- 房间注册表（Phase E：/api/rooms 五端点） ----

    public Task<ApiResult<GameRoomDto>> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<GameRoomDto>("room-create", UnityWebRequest.kHttpVerbPOST, "/api/rooms", request, cancellationToken);

    public Task<ApiResult<GameRoomDto[]>> ListRoomsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<GameRoomDto[]>("room-list", UnityWebRequest.kHttpVerbGET, "/api/rooms", null, cancellationToken);

    public Task<ApiResult<GameRoomDto>> JoinRoomAsync(string roomCode, CancellationToken cancellationToken = default) =>
        SendAsync<GameRoomDto>("room-join-" + (roomCode ?? string.Empty).ToUpperInvariant(),
            UnityWebRequest.kHttpVerbPOST, "/api/rooms/" + UnityWebRequest.EscapeURL(roomCode ?? string.Empty) + "/join", null, cancellationToken);

    public Task<ApiResult<object>> HeartbeatRoomAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>("room-heartbeat", UnityWebRequest.kHttpVerbPOST, "/api/rooms/heartbeat", null, cancellationToken);

    public Task<ApiResult<object>> LeaveRoomAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>("room-leave", UnityWebRequest.kHttpVerbPOST, "/api/rooms/leave", null, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(string operationKey, string method, string path, object payload, CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ApiClient));
        lock (operationGate)
        {
            if (!inFlightOperations.Add(operationKey))
                return ApiResult<T>.Fail(0, "CLIENT_DUPLICATE_REQUEST", "相同操作正在处理中，请稍候");
        }

        try
        {
            var targetUrl = baseUrl + path;
            var stopwatch = Stopwatch.StartNew();
            using var request = new UnityWebRequest(targetUrl, method)
            {
                // Unity's native timeout remains a last line of defence. The
                // explicit timer below tells us whether it was our timeout,
                // instead of guessing from responseCode == 0.
                timeout = timeoutSeconds,
                downloadHandler = new DownloadHandlerBuffer()
            };
            if (payload != null)
            {
                var json = JsonConvert.SerializeObject(payload);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.uploadHandler.contentType = "application/json";
                request.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrWhiteSpace(token)) request.SetRequestHeader("Authorization", "Bearer " + token);

            var completion = new TaskCompletionSource<UnityWebRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                request.Abort();
                ReleaseOperation(operationKey);
                cancellationCompletion.TrySetResult(true);
            });
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), timeoutCancellation.Token);
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
                operation.completed += _ => completion.TrySetResult(request);
            }
            catch (Exception ex)
            {
                timeoutCancellation.Cancel();
                LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, UnityWebRequest.Result.ConnectionError, 0, ApiClientErrorCodes.Request, ex.Message);
                return ApiResult<T>.Fail(0, ApiClientErrorCodes.Request, "请求无法启动");
            }

            var winner = await Task.WhenAny(completion.Task, timeoutTask, cancellationCompletion.Task);
            if (winner == cancellationCompletion.Task || cancellationToken.IsCancellationRequested)
            {
                timeoutCancellation.Cancel();
                ReleaseOperation(operationKey);
                return ApiResult<T>.Fail(0, ApiClientErrorCodes.Cancelled, "请求已取消");
            }

            if (winner == timeoutTask && !completion.Task.IsCompleted)
            {
                request.Abort();
                LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, UnityWebRequest.Result.ConnectionError, 0, ApiClientErrorCodes.Timeout, "客户端请求超时");
                ReleaseOperation(operationKey);
                return ApiResult<T>.Fail(0, ApiClientErrorCodes.Timeout, "服务器响应超时");
            }

            var completedRequest = await completion.Task;
            timeoutCancellation.Cancel();

            if (cancellationToken.IsCancellationRequested)
                return ApiResult<T>.Fail(0, ApiClientErrorCodes.Cancelled, "请求已取消");

            var text = completedRequest.downloadHandler?.text ?? string.Empty;
            if (completedRequest.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    return ApiResult<T>.Ok(string.IsNullOrWhiteSpace(text) ? default : JsonConvert.DeserializeObject<T>(text), completedRequest.responseCode);
                }
                catch (Exception ex)
                {
                    LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, completedRequest.result, completedRequest.responseCode, ApiClientErrorCodes.InvalidJson, ex.Message);
                    return ApiResult<T>.Fail(completedRequest.responseCode, ApiClientErrorCodes.InvalidJson, "服务器响应格式无效");
                }
            }

            return ParseFailure<T>(method, targetUrl, stopwatch.ElapsedMilliseconds, completedRequest.responseCode, text, completedRequest.error, completedRequest.result, false);
        }
        catch (Exception ex)
        {
            // Do not let a client-side request construction/dispatch problem
            // escape as an unhandled task exception from the UI.
            LogFailure(method, baseUrl + path, 0, UnityWebRequest.Result.ConnectionError, 0, ApiClientErrorCodes.Request, ex.Message);
            return ApiResult<T>.Fail(0, ApiClientErrorCodes.Request, "请求无法启动");
        }
        finally
        {
            ReleaseOperation(operationKey);
        }
    }

    private void ReleaseOperation(string operationKey)
    {
        lock (operationGate) inFlightOperations.Remove(operationKey);
    }

    private ApiResult<T> ParseFailure<T>(string method, string targetUrl, long elapsedMilliseconds, long status, string body, string transportError, UnityWebRequest.Result result, bool explicitTimeout)
    {
        try
        {
            var problem = JsonConvert.DeserializeObject<ProblemDetailsDto>(body);
            if (problem != null)
            {
                LogFailure(method, targetUrl, elapsedMilliseconds, result, status, problem.code, transportError);
                return ApiResult<T>.Fail(status, problem.code, problem.detail ?? problem.title, problem.errors);
            }
        }
        catch (JsonException) { }
        var code = status == 0 ? ApiTransportFailureClassifier.Classify(result, transportError, explicitTimeout)
            : status == 400 ? "CLIENT_BAD_REQUEST"
            : status == 401 ? "AUTH_UNAUTHORIZED"
            : status == 409 ? "CLIENT_CONFLICT"
            : "CLIENT_HTTP_ERROR";
        LogFailure(method, targetUrl, elapsedMilliseconds, result, status, code, transportError);
        return ApiResult<T>.Fail(status, code, string.IsNullOrWhiteSpace(transportError) ? "请求失败" : transportError);
    }

    private static void LogFailure(string method, string targetUrl, long elapsedMilliseconds, UnityWebRequest.Result result, long status, string code, string detail)
    {
        var safeDetail = (detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeDetail.Length > 160) safeDetail = safeDetail.Substring(0, 160);
        UnityEngine.Debug.LogWarning($"[ApiClient] {method} {SanitizeUrl(targetUrl)} failed code={code} result={result} status={status} elapsedMs={elapsedMilliseconds} detail={safeDetail}");
    }

    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "<invalid-url>";
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        return uri.Scheme + "://" + uri.Host + port + uri.AbsolutePath;
    }

    public void Dispose()
    {
        disposed = true;
        token = null;
    }
}
}
