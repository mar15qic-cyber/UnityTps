using System.Threading;
using System.Threading.Tasks;

namespace Game.Account
{
public partial interface IApiClient
{
    Task<ApiResult<HealthDto>> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<AuthSessionDto>> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<AuthSessionDto>> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<PlayerProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> GetLoadoutAsync(int backpack = 1, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> UpdateLoadoutAsync(LoadoutRequest request, int backpack = 1, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutAttachmentsDto>> GetLoadoutAttachmentsAsync(int backpack = 1, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutAttachmentsDto>> UpdateLoadoutAttachmentsAsync(LoadoutAttachmentsRequest request, int backpack = 1, CancellationToken cancellationToken = default);
    /// <summary>三背包全集（CF 背包系统 2026-09-30）：大厅/仓库一次拉取；恒长 3（后端懒默认合成）。</summary>
    Task<ApiResult<BackpackSetDto>> GetBackpackSetAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<AttachmentCompatibilityDto[]>> GetAttachmentCompatibilityAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<ShopCatalogDto>> GetShopCatalogAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<InventoryDto>> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<PurchaseResultDto>> PurchaseAsync(PurchaseRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default);
    // ---- 战绩历史（2026-09-17 热更试点 P3）：大厅战绩页数据源 ----
    Task<ApiResult<MatchHistoryPageDto>> GetMatchHistoryAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    // ---- Docs/27 v1.2 CF 等待房间（创建/加入只进 Waiting，不返回票据；开局才签个人票）----
    Task<ApiResult<RoomSnapshotDto>> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> JoinRoomByCodeAsync(string code, string protocol, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> JoinRoomAsync(string roomId, string teamId = null, string clientProtocolId = null, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> GetRoomDetailAsync(string roomId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> SetRoomTeamAsync(string roomId, string teamId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> SetRoomReadyAsync(string roomId, bool isReady, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> UpdateRoomSettingsAsync(string roomId, RoomSettingsRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<StartMatchDto>> StartRoomMatchAsync(string roomId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomSnapshotDto>> ReturnRoomAsync(string roomId, string matchId, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomMatchResultViewDto>> GetRoomMatchResultAsync(string roomId, string matchId, CancellationToken cancellationToken = default);
    Task<ApiResult<MapCatalogDto[]>> ListMapsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<GameRoomDto[]>> ListRoomsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<object>> HeartbeatRoomAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<object>> LeaveRoomAsync(CancellationToken cancellationToken = default);
    // ---- 房间聊天 HTTP 传输（Docs/27 §8：Waiting/Starting/Returning；InMatch 走 Owner RPC）----
    Task<ApiResult<RoomChatMessageDto>> SendRoomChatAsync(string roomId, RoomChatSendRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<RoomChatFeedDto>> FetchRoomChatAsync(string roomId, ulong after, CancellationToken cancellationToken = default);
    // ---- 好友（2026-09-20 需求2：用户名#编码 查找 + 请求-同意制 + 在线状态）----
    Task<ApiResult<FriendListDto>> GetFriendsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<FriendRequestEntryDto>> SendFriendRequestAsync(string query, CancellationToken cancellationToken = default);
    Task<ApiResult<object>> AcceptFriendRequestAsync(long requestId, CancellationToken cancellationToken = default);
    Task<ApiResult<object>> RemoveFriendRequestAsync(long requestId, CancellationToken cancellationToken = default);
    Task<ApiResult<object>> RemoveFriendAsync(long friendUserId, CancellationToken cancellationToken = default);
    void SetToken(string token);
    void ClearToken();
}
}
