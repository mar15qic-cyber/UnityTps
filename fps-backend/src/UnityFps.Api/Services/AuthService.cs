using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class AuthService(AppDbContext db, IJwtTokenService jwt, IProgressionRules rules)
{
    public async Task<AuthSessionDto> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        ValidateUsername(username);
        var normalized = Normalize(username);
        if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.UsernameTaken, "用户名已存在");

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var user = new UserAccount { Username = username, NormalizedUsername = normalized, PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12), CreatedAtUtc = DateTime.UtcNow };
        user.Profile = new PlayerProfile { User = user, UpdatedAtUtc = DateTime.UtcNow };
        user.Loadout = new PlayerLoadout { User = user, UpdatedAtUtc = DateTime.UtcNow };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.UsernameTaken, "用户名已存在"); }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return CreateSession(user);
    }

    public async Task<AuthSessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Username.Trim());
        var user = await db.Users.Include(x => x.Profile).Include(x => x.Loadout).SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidCredentials, "用户名或密码错误");
        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return CreateSession(user);
    }

    private AuthSessionDto CreateSession(UserAccount user)
    {
        var profile = user.Profile ?? throw new InvalidOperationException("Profile missing");
        var loadout = user.Loadout ?? throw new InvalidOperationException("Loadout missing");
        var token = jwt.Create(user);
        return new AuthSessionDto(token.Token, token.ExpiresAtUtc, profile.ToDto(user.Username, rules), loadout.ToDto());
    }

    public static long GetUserId(System.Security.Claims.ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? principal.FindFirst("sub")?.Value, out var id)
            ? id : throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "登录状态无效");

    public static string Normalize(string username) => username.ToUpperInvariant();
    private static void ValidateUsername(string username)
    {
        if (username.Length is < 3 or > 32 || username.Any(char.IsWhiteSpace))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "用户名长度需为 3–32 个字符且不能包含空格");
    }
}
