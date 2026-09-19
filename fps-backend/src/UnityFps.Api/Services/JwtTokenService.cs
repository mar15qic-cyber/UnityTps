using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) Create(UserAccount user);
}

public sealed class JwtTokenService(IConfiguration configuration) : IJwtTokenService
{
    public (string Token, DateTime ExpiresAtUtc) Create(UserAccount user)
    {
        var key = configuration["Jwt:SigningKey"] ?? Environment.GetEnvironmentVariable("Jwt__SigningKey") ?? "development-only-signing-key-change-me-please-32-bytes";
        var issuer = configuration["Jwt:Issuer"] ?? "UnityFps.Api";
        var audience = configuration["Jwt:Audience"] ?? "UnityFps.Client";
        var hours = configuration.GetValue("Jwt:ExpiryHours", 12);
        var expires = DateTime.UtcNow.AddHours(hours);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                // 单活会话（2026-09-17）：登录即 +1 的会话版本；OnTokenValidated 比对库值，
                // 旧客户端 token（旧 tv）在任何认证端点上立即 401——同账号多端登录=后者顶替前者。
                new Claim("tv", user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
            Expires = expires,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(handler.CreateToken(descriptor)), expires);
    }
}
