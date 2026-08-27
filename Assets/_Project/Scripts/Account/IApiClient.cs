using System.Threading;
using System.Threading.Tasks;

namespace Game.Account
{

public interface IApiClient
{
    Task<ApiResult<AuthSessionDto>> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<AuthSessionDto>> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ApiResult<PlayerProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<PlayerProfileDto>> UpdateUpgradesAsync(UpgradeRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> GetLoadoutAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<LoadoutDto>> UpdateLoadoutAsync(LoadoutRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<MatchResultDto>> SubmitMatchAsync(MatchSubmissionRequest request, CancellationToken cancellationToken = default);
    void SetToken(string token);
    void ClearToken();
}
}
