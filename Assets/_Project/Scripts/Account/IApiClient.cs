using System.Threading;
using System.Threading.Tasks;

namespace Game.Account
{
public interface IApiClient
{
    Task<ApiResult<HealthDto>> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<AuthSessionDto>> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<AuthSessionDto>> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<PlayerProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<PlayerProfileDto>> UpdateUpgradesAsync(UpgradeRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> GetLoadoutAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> UpdateLoadoutAsync(LoadoutRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutAttachmentsDto>> GetLoadoutAttachmentsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutAttachmentsDto>> UpdateLoadoutAttachmentsAsync(LoadoutAttachmentsRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<AttachmentCompatibilityDto[]>> GetAttachmentCompatibilityAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<ShopCatalogDto>> GetShopCatalogAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<InventoryDto>> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<PurchaseResultDto>> PurchaseAsync(PurchaseRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<MatchResultDto>> SubmitMatchAsync(MatchSubmissionRequest request, CancellationToken cancellationToken = default);
    // ---- 战绩历史（2026-09-17 热更试点 P3）：大厅战绩页数据源 ----
    Task<ApiResult<MatchHistoryPageDto>> GetMatchHistoryAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    // ---- Docs/27 v1.2 CF 等待房间（创建/加入只进 Waiting，不返回票据；开局才签个人票）----
    Task<ApiResult<RoomSnapshotDto>> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> JoinRoomAsync(string roomCode, string teamId = null, string clientProtocolId = null, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> GetRoomDetailAsync(string roomCode, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> SetRoomTeamAsync(string roomCode, string teamId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> SetRoomReadyAsync(string roomCode, bool isReady, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> UpdateRoomSettingsAsync(string roomCode, RoomSettingsRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<StartMatchDto>> StartRoomMatchAsync(string roomCode, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> ReturnRoomAsync(string roomCode, string matchId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomMatchResultViewDto>> GetRoomMatchResultAsync(string roomCode, string matchId, CancellationToken cancellationToken = default);
    Task<ApiResult<MapCatalogDto[]>> ListMapsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<GameRoomDto[]>> ListRoomsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<object>> HeartbeatRoomAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<object>> LeaveRoomAsync(CancellationToken cancellationToken = default);
    // ---- 房间聊天 HTTP 传输（Docs/27 §8：Waiting/Starting/Returning；InMatch 走 Owner RPC）----
    Task<ApiResult<RoomChatMessageDto>> SendRoomChatAsync(string roomCode, RoomChatSendRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomChatFeedDto>> FetchRoomChatAsync(string roomCode, ulong after, CancellationToken cancellationToken = default);
    void SetToken(string token);
    void ClearToken();
}
}
