using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class AuthService(AppDbContext db, IJwtTokenService jwt, IProgressionRules rules, IConfiguration? configuration = null)
{
    public async Task<AuthSessionDto> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (configuration?.GetValue<bool>("Access:InviteOnly") == true)
            throw new ApiException(403, "INVITE_ONLY", "仅限受邀测试账号，请联系测试组织者");
        return await CreateInvitedAsync(request, cancellationToken);
    }

    public async Task<AuthSessionDto> CreateInvitedAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (request.Password.Length < 8 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72)
            throw new ApiException(400, ApiErrorCodes.ValidationFailed, "密码需至少 8 个字符且不超过 72 UTF-8 字节");
        var username = request.Username.Trim();
        ValidateUsername(username);
        var normalized = Normalize(username);
        if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.UsernameTaken, "用户名已存在");

        // EnableRetryOnFailure 与显式事务不兼容（InvalidOperationException）——注册本身是
        // 单次 SaveChanges 原子操作，事务包裹无必要；唯一约束冲突由 catch 转业务 409。
        var user = new UserAccount { Username = username, NormalizedUsername = normalized, PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12), CreatedAtUtc = DateTime.UtcNow, TokenVersion = 1, IdentityTag = GenerateIdentityTag() };
        user.Profile = new PlayerProfile { User = user, UpdatedAtUtc = DateTime.UtcNow };
        // CF 三背包（2026-09-30）：注册建背包 0（默认武器+默认投掷包），背包 1/2 懒创建
        user.Loadouts.Add(new PlayerLoadout { User = user, BackpackIndex = 0, ThrowableId = BackpackPolicy.DefaultThrowableItemId, UpdatedAtUtc = DateTime.UtcNow });
        ThrowableSlotPolicy.Write(user.Loadouts[0], ThrowableSlotPolicy.FromLegacy(BackpackPolicy.DefaultThrowableItemId));
        user.Wallet = new PlayerWallet { User = user, Coins = CatalogSeeder.InitialCoins, UpdatedAtUtc = DateTime.UtcNow };
        user.Passes.Add(new PlayerPass { SeasonId = PassSeeder.SeasonId, PassLevel = 1, PassXp = 0, Version = 1, UpdatedAtUtc = DateTime.UtcNow });
        foreach (var itemId in CatalogSeeder.InitialWeapons)
            user.Inventory.Add(new PlayerInventoryItem { User = user, ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        foreach (var itemId in CatalogSeeder.InitialThrowables)
            user.Inventory.Add(new PlayerInventoryItem { User = user, ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.UsernameTaken, "用户名已存在"); }
        return CreateSession(user);
    }

    public async Task<AuthSessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Username.Trim());
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            var user = await db.Users.Include(x => x.Profile).Include(x => x.Wallet)
                .Include(x => x.Loadouts).ThenInclude(x => x.Attachments)
                .SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, token);
            if (user is null || user.Disabled || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidCredentials, "用户名或密码错误");

            if (!db.Database.IsRelational())
            {
                // InMemory 仅测试宿主使用：单线程假并发，读改写语义可接受。
                user.LastLoginAtUtc = DateTime.UtcNow;
                user.TokenVersion++;
                await db.SaveChangesAsync(token);
                return CreateSession(user);
            }

            // 单活会话（2026-09-17 实测缺口）：每次登录 +1 → 本次签发的 token 携带新 tv，
            // 该账号此前所有 token（旧 tv）在下一个认证请求立即 401（SessionExpired 语义）。
            // F15（2026-09-19 审计）：读改写非原子——两个并发登录都读到 v 再各存 v+1，
            // 两个 token 同时有效。改为事务内原子自增后回读：UPDATE 持有行 X 锁直到提交，
            // 并发登录串行化，各取得唯一递增版本；只有最后提交者签发的 token 与库值一致。
            var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE UserAccount SET TokenVersion = TokenVersion + 1, LastLoginAtUtc = {now} WHERE Id = {user.Id}", token);
            var committedVersion = await db.Database.SqlQuery<long>(
                $"SELECT TokenVersion AS Value FROM UserAccount WHERE Id = {user.Id}").SingleAsync(token);
            await tx.CommitAsync(token);

            // 原子路径绕过 SaveChanges：仅同步跟踪实体与库值，供 JWT 签发读取。
            user.TokenVersion = committedVersion;
            user.LastLoginAtUtc = now;
            return CreateSession(user);
        }, cancellationToken);
    }

    private AuthSessionDto CreateSession(UserAccount user)
    {
        var profile = user.Profile ?? throw new InvalidOperationException("Profile missing");
        // 背包 0 = 活动背包（AuthSessionDto.Loadout 保留旧语义镜像）；三背包全集走 Backpacks。
        var loadout = user.Loadouts.FirstOrDefault(x => x.BackpackIndex == 0)
            ?? throw new InvalidOperationException("Loadout missing");
        var token = jwt.Create(user);
        var coins = user.Wallet?.Coins ?? 0;
        return new AuthSessionDto(token.Token, token.ExpiresAtUtc, profile.ToDto(user, coins, rules), loadout.ToDto(), coins,
            new BackpackSetDto(BackpackPolicy.BuildBackpackSet(user.Loadouts), BackpackPolicy.DefaultActiveIndex));
    }

    public static long GetUserId(System.Security.Claims.ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? principal.FindFirst("sub")?.Value, out var id)
            ? id : throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "登录状态无效");

    public static string Normalize(string username) => username.ToUpperInvariant();

    /// <summary>好友查找编码：4 位数字含前导零（如 0042）。用户名全局唯一 ⇒ 名字+编码组合唯一，
    /// 编码本身不要求全局唯一，注册时无需冲突重试。</summary>
    private static string GenerateIdentityTag() => Random.Shared.Next(0, 10000).ToString("D4");

    private static void ValidateUsername(string username)
    {
        if (username.Length is < 3 or > 32 || username.Any(char.IsWhiteSpace))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "用户名长度需为 3–32 个字符且不能包含空格");
    }
}
