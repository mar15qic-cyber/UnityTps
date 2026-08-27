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
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username)]),
            Expires = expires,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(handler.CreateToken(descriptor)), expires);
    }
}
