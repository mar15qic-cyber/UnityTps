using System;
using System.Collections.Generic;
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
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("API BaseUrl 不能为空", nameof(config));
    }

    public void SetToken(string value) => token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void ClearToken() => token = null;

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

    public Task<ApiResult<MatchResultDto>> SubmitMatchAsync(MatchSubmissionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MatchResultDto>("match-submit", UnityWebRequest.kHttpVerbPOST, "/api/matches", request, cancellationToken);

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
        using var request = new UnityWebRequest(baseUrl + path, method)
        {
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
        using var registration = cancellationToken.Register(() => request.Abort());
        var operation = request.SendWebRequest();
        operation.completed += _ => completion.TrySetResult(request);
        var completedRequest = await completion.Task;
        if (cancellationToken.IsCancellationRequested) return ApiResult<T>.Fail(0, "CLIENT_CANCELLED", "请求已取消");

        var text = completedRequest.downloadHandler?.text ?? string.Empty;
        if (completedRequest.result == UnityWebRequest.Result.Success)
        {
            try { return ApiResult<T>.Ok(string.IsNullOrWhiteSpace(text) ? default : JsonConvert.DeserializeObject<T>(text), completedRequest.responseCode); }
            catch (Exception ex) { return ApiResult<T>.Fail(completedRequest.responseCode, "CLIENT_INVALID_JSON", ex.Message); }
        }

        return ParseFailure<T>(completedRequest.responseCode, text, completedRequest.error);
        }
        finally
        {
            lock (operationGate) inFlightOperations.Remove(operationKey);
        }
    }

    private ApiResult<T> ParseFailure<T>(long status, string body, string transportError)
    {
        try
        {
            var problem = JsonConvert.DeserializeObject<ProblemDetailsDto>(body);
            if (problem != null)
                return ApiResult<T>.Fail(status, problem.code, problem.detail ?? problem.title, problem.errors);
        }
        catch (JsonException) { }
        var code = status == 0 ? "CLIENT_NETWORK_ERROR"
            : status == 400 ? "CLIENT_BAD_REQUEST"
            : status == 401 ? "AUTH_UNAUTHORIZED"
            : status == 409 ? "CLIENT_CONFLICT"
            : "CLIENT_HTTP_ERROR";
        return ApiResult<T>.Fail(status, code, string.IsNullOrWhiteSpace(transportError) ? "请求失败" : transportError);
    }

    public void Dispose()
    {
        disposed = true;
        token = null;
    }
}
}
