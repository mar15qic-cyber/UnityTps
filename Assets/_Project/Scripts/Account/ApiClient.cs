using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace Game.Account
{

public sealed partial class ApiClient : IApiClient, IDisposable
{
    private readonly string baseUrl;
    private readonly int timeoutSeconds;
    private readonly HttpClient httpClient;
    private readonly HashSet<string> inFlightOperations = new HashSet<string>(StringComparer.Ordinal);
    private readonly object operationGate = new object();
    private string token;
    private bool disposed;
    public event Action<string> SessionRejected;

    public static bool IsCurrentSessionRejection(int status, string path, string sentToken, string currentToken)
        => status == 401 && !string.IsNullOrEmpty(sentToken) && sentToken == currentToken
            && !path.StartsWith("/api/auth/", StringComparison.Ordinal);

    public ApiClient(ApiClientConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        baseUrl = ApiClientConfig.NormalizeBaseUrl(config.BaseUrl);
        timeoutSeconds = config.TimeoutSeconds;
        if (!config.TryValidate(out var validationError)) throw new ArgumentException(validationError, nameof(config));
        httpClient = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }

    public void SetToken(string value) => token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void ClearToken() => token = null;

    public Task<ApiResult<HealthDto>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HealthDto>("health", "GET", "/health", null, cancellationToken);

    public Task<ApiResult<AuthSessionDto>> RegisterAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<AuthSessionDto>("register", "POST", "/api/auth/register", new RegisterRequest { username = username, password = password }, cancellationToken);

    public Task<ApiResult<AuthSessionDto>> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<AuthSessionDto>("login", "POST", "/api/auth/login", new LoginRequest { username = username, password = password }, cancellationToken);

    public Task<ApiResult<PlayerProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default) =>
        SendAsync<PlayerProfileDto>("profile-get", "GET", "/api/profile", null, cancellationToken);


    public Task<ApiResult<LoadoutDto>> GetLoadoutAsync(int backpack = 1, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutDto>("loadout-get", "GET", $"/api/loadout?backpack={backpack}", null, cancellationToken);

    public Task<ApiResult<LoadoutDto>> UpdateLoadoutAsync(LoadoutRequest request, int backpack = 1, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutDto>("loadout-update", "PUT", $"/api/loadout?backpack={backpack}", request, cancellationToken);

    public Task<ApiResult<LoadoutAttachmentsDto>> GetLoadoutAttachmentsAsync(int backpack = 1, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutAttachmentsDto>("loadout-attachments-get", "GET", $"/api/loadout/attachments?backpack={backpack}", null, cancellationToken);

    public Task<ApiResult<LoadoutAttachmentsDto>> UpdateLoadoutAttachmentsAsync(LoadoutAttachmentsRequest request, int backpack = 1, CancellationToken cancellationToken = default) =>
        SendAsync<LoadoutAttachmentsDto>("loadout-attachments-update", "PUT", $"/api/loadout/attachments?backpack={backpack}", request, cancellationToken);

    /// <summary>三背包全集（CF 背包系统 2026-09-30）：大厅/仓库一次拉取。</summary>
    public Task<ApiResult<BackpackSetDto>> GetBackpackSetAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BackpackSetDto>("loadout-backpacks-get", "GET", "/api/loadout/backpacks", null, cancellationToken);

    public Task<ApiResult<AttachmentCompatibilityDto[]>> GetAttachmentCompatibilityAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AttachmentCompatibilityDto[]>("attachment-compatibility-get", "GET", "/api/loadout/compatibility", null, cancellationToken);

    public Task<ApiResult<ShopCatalogDto>> GetShopCatalogAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ShopCatalogDto>("shop-catalog-get", "GET", "/api/shop/catalog", null, cancellationToken);

    public Task<ApiResult<InventoryDto>> GetInventoryAsync(CancellationToken cancellationToken = default) =>
        SendAsync<InventoryDto>("inventory-get", "GET", "/api/inventory", null, cancellationToken);

    public Task<ApiResult<PurchaseResultDto>> PurchaseAsync(PurchaseRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PurchaseResultDto>("purchase-" + (request?.idempotencyKey ?? string.Empty), "POST", "/api/shop/purchases", request, cancellationToken);

    public Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<UserSettingsDto>("user-settings-get", "GET", "/api/settings", null, cancellationToken);

    public Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UserSettingsDto>("user-settings-update", "PUT", "/api/settings", request, cancellationToken);


    // ---- 战绩历史（2026-09-17 热更试点 P3）：大厅战绩页数据源 ----

    public Task<ApiResult<MatchHistoryPageDto>> GetMatchHistoryAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<MatchHistoryPageDto>("match-history-get-" + page + "-" + pageSize,
            "GET", $"/api/matches/history?page={page}&pageSize={pageSize}", null, cancellationToken);

    // ---- 房间（Docs/27 v1.2 CF 等待房间：创建/加入只进 Waiting；详情轮询补发 Starting/InMatch 连接票）----

    public Task<ApiResult<RoomSnapshotDto>> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-create", "POST", "/api/rooms", request, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> JoinRoomByCodeAsync(string code, string protocol, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-join-code", "POST", "/api/rooms/join-by-code",
            new JoinRoomByCodeRequest { roomCode = code, clientProtocolId = protocol }, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> JoinRoomAsync(string roomId, string teamId = null, string clientProtocolId = null, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-join-" + Key(roomId),
            "POST", "/api/rooms/" + Path(roomId) + "/join",
            new RoomJoinRequest { teamId = teamId, clientProtocolId = clientProtocolId }, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> GetRoomDetailAsync(string roomId, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-detail-" + Key(roomId), "GET", "/api/rooms/" + Path(roomId), null, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> SetRoomTeamAsync(string roomId, string teamId, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-team-" + Key(roomId), "POST", "/api/rooms/" + Path(roomId) + "/team",
            new RoomTeamRequest { teamId = teamId }, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> SetRoomReadyAsync(string roomId, bool isReady, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-ready-" + Key(roomId), "POST", "/api/rooms/" + Path(roomId) + "/ready",
            new RoomReadyRequest { isReady = isReady }, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> UpdateRoomSettingsAsync(string roomId, RoomSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-settings-" + Key(roomId), "POST", "/api/rooms/" + Path(roomId) + "/settings", request, cancellationToken);

    public Task<ApiResult<StartMatchDto>> StartRoomMatchAsync(string roomId, CancellationToken cancellationToken = default) =>
        SendAsync<StartMatchDto>("room-start-" + Key(roomId), "POST", "/api/rooms/" + Path(roomId) + "/start", null, cancellationToken);

    public Task<ApiResult<RoomSnapshotDto>> ReturnRoomAsync(string roomId, string matchId, CancellationToken cancellationToken = default) =>
        SendAsync<RoomSnapshotDto>("room-return-" + Key(roomId), "POST", "/api/rooms/" + Path(roomId) + "/return",
            new RoomReturnRequest { matchId = matchId }, cancellationToken);

    public Task<ApiResult<RoomMatchResultViewDto>> GetRoomMatchResultAsync(string roomId, string matchId, CancellationToken cancellationToken = default) =>
        SendAsync<RoomMatchResultViewDto>("room-result-" + Key(roomId) + "-" + Key(matchId),
            "GET", "/api/rooms/" + Path(roomId) + "/match-result?matchId=" + Uri.EscapeDataString(matchId ?? string.Empty), null, cancellationToken);

    public Task<ApiResult<MapCatalogDto[]>> ListMapsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<MapCatalogDto[]>("maps-list", "GET", "/api/maps", null, cancellationToken);

    public Task<ApiResult<GameRoomDto[]>> ListRoomsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<GameRoomDto[]>("room-list", "GET", "/api/rooms", null, cancellationToken);

    public Task<ApiResult<object>> HeartbeatRoomAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>("room-heartbeat", "POST", "/api/rooms/heartbeat", null, cancellationToken);

    public Task<ApiResult<object>> LeaveRoomAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>("room-leave", "POST", "/api/rooms/leave", null, cancellationToken);

    // ---- 房间聊天 HTTP 传输（Docs/27 §8：Waiting/Starting/Returning；InMatch 收发均 409 走 Owner RPC）----

    public Task<ApiResult<RoomChatMessageDto>> SendRoomChatAsync(string roomId, RoomChatSendRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RoomChatMessageDto>("room-chat-send-" + Key(roomId) + "-" + Key(request?.clientMessageId),
            "POST", "/api/rooms/" + Path(roomId) + "/chat", request, cancellationToken);

    public Task<ApiResult<RoomChatFeedDto>> FetchRoomChatAsync(string roomId, ulong after, CancellationToken cancellationToken = default) =>
        SendAsync<RoomChatFeedDto>("room-chat-fetch-" + Key(roomId),
            "GET", "/api/rooms/" + Path(roomId) + "/chat?after=" + after, null, cancellationToken);

    // ---- 好友（2026-09-20 需求2）----

    public Task<ApiResult<FriendListDto>> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FriendListDto>("friends-list", "GET", "/api/friends", null, cancellationToken);

    public Task<ApiResult<FriendRequestEntryDto>> SendFriendRequestAsync(string query, CancellationToken cancellationToken = default) =>
        SendAsync<FriendRequestEntryDto>("friends-send", "POST", "/api/friends/requests",
            new FriendSendRequest { query = query }, cancellationToken);

    public Task<ApiResult<object>> AcceptFriendRequestAsync(long requestId, CancellationToken cancellationToken = default) =>
        SendAsync<object>("friends-accept-" + requestId, "POST", "/api/friends/requests/" + requestId + "/accept", null, cancellationToken);

    public Task<ApiResult<object>> RemoveFriendRequestAsync(long requestId, CancellationToken cancellationToken = default) =>
        SendAsync<object>("friends-request-remove-" + requestId, "DELETE", "/api/friends/requests/" + requestId, null, cancellationToken);

    public Task<ApiResult<object>> RemoveFriendAsync(long friendUserId, CancellationToken cancellationToken = default) =>
        SendAsync<object>("friends-remove-" + friendUserId, "DELETE", "/api/friends/" + friendUserId, null, cancellationToken);

    private static string Path(string roomId) => Uri.EscapeDataString((roomId ?? string.Empty).Trim());

    private static string Key(string value) => Uri.EscapeDataString((value ?? string.Empty).Trim()).Replace('.', '_');

    private async Task<ApiResult<T>> SendAsync<T>(string operationKey, string method, string path, object payload, CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ApiClient));
        lock (operationGate)
        {
            if (!inFlightOperations.Add(operationKey))
                return ApiResult<T>.Fail(0, "CLIENT_DUPLICATE_REQUEST", "相同操作正在处理中，请稍候");
        }

        var targetUrl = baseUrl + path;
        var sentToken = token;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), targetUrl);
            if (payload != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(sentToken))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + sentToken);

            // F10：HttpClient(UseProxy=false) 直连。超时由 HttpClient.Timeout 统一承担
            //（到期抛 OperationCanceledException，下方与用户取消分流），不再需要显式计时器赛跑。
            using var response = await httpClient.SendAsync(request, cancellationToken);

            var text = await response.Content.ReadAsStringAsync();
            var status = (int)response.StatusCode;
            if (IsCurrentSessionRejection(status, path, sentToken, token))
                SessionRejected?.Invoke(sentToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return ApiResult<T>.Ok(string.IsNullOrWhiteSpace(text) ? default : JsonConvert.DeserializeObject<T>(text), status);
                }
                catch (Exception ex)
                {
                    LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, "DataProcessingError", status, ApiClientErrorCodes.InvalidJson, ex.Message);
                    return ApiResult<T>.Fail(status, ApiClientErrorCodes.InvalidJson, "服务器响应格式无效");
                }
            }

            return ParseFailure<T>(method, targetUrl, stopwatch.ElapsedMilliseconds, status, text, response.ReasonPhrase ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseOperation(operationKey);
            return ApiResult<T>.Fail(0, ApiClientErrorCodes.Cancelled, "请求已取消");
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout 到期（非用户取消）
            LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, "ConnectionError", 0, ApiClientErrorCodes.Timeout, "客户端请求超时");
            return ApiResult<T>.Fail(0, ApiClientErrorCodes.Timeout, "服务器响应超时");
        }
        catch (Exception ex)
        {
            // Do not let a client-side request construction/dispatch problem
            // escape as an unhandled task exception from the UI.
            LogFailure(method, targetUrl, stopwatch.ElapsedMilliseconds, "ConnectionError", 0, ApiClientErrorCodes.Request, ex.Message);
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

    private ApiResult<T> ParseFailure<T>(string method, string targetUrl, long elapsedMilliseconds, int status, string body, string transportError)
    {
        try
        {
            var problem = JsonConvert.DeserializeObject<ProblemDetailsDto>(body);
            if (problem != null)
            {
                LogFailure(method, targetUrl, elapsedMilliseconds, "ProtocolError", status, problem.code, transportError);
                return ApiResult<T>.Fail(status, problem.code, problem.detail ?? problem.title, problem.errors);
            }
        }
        catch (JsonException) { }
        var code = status == 0 ? ApiTransportFailureClassifier.Classify(transportError)
            : status == 400 ? "CLIENT_BAD_REQUEST"
            : status == 401 ? "AUTH_UNAUTHORIZED"
            : status == 409 ? "CLIENT_CONFLICT"
            : "CLIENT_HTTP_ERROR";
        LogFailure(method, targetUrl, elapsedMilliseconds, "ProtocolError", status, code, transportError);
        return ApiResult<T>.Fail(status, code, string.IsNullOrWhiteSpace(transportError) ? "请求失败" : transportError);
    }

    private static void LogFailure(string method, string targetUrl, long elapsedMilliseconds, string state, long status, string code, string detail)
    {
        var safeDetail = (detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeDetail.Length > 160) safeDetail = safeDetail.Substring(0, 160);
        UnityEngine.Debug.LogWarning($"[ApiClient] {method} {SanitizeUrl(targetUrl)} failed code={code} result={state} status={status} elapsedMs={elapsedMilliseconds} detail={safeDetail}");
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
        httpClient?.Dispose();
    }
}
}
