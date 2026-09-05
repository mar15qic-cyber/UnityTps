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
    Task<ApiResult<MatchResultDto>> SubmitMatchAsync(MatchSubmissionRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<GameRoomDto>> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<GameRoomDto[]>> ListRoomsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<GameRoomDto>> JoinRoomAsync(string roomCode, CancellationToken cancellationToken = default);
    Task<ApiResult<object>> HeartbeatRoomAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<object>> LeaveRoomAsync(CancellationToken cancellationToken = default);
    void SetToken(string token);
    void ClearToken();
}
}
