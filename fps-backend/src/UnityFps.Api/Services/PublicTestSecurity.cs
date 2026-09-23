using System.Text;

namespace UnityFps.Api.Services;

public static class PublicTestSecurity
{
    public const string DevelopmentSigningKey = "development-only-signing-key-change-me-please-32-bytes";

    public static string SigningKey(IConfiguration config) =>
        config["Jwt:SigningKey"] ?? DevelopmentSigningKey;

    public static void Validate(IConfiguration config, bool development)
    {
        if (development) return;
        var jwt = config["Jwt:SigningKey"];
        var server = config["ServerInstances:ServerKey"];
        if (!StrongSecret(jwt) || !StrongSecret(server) || jwt == server)
            throw new InvalidOperationException("Production requires distinct, non-development JWT and server secrets (at least 32 UTF-8 bytes).");
        if (config.GetValue<bool>("Database:AllowInMemoryFallback") || config["Database:InMemoryName"] is not null)
            throw new InvalidOperationException("Production cannot use an in-memory database.");
    }

    public static bool StrongSecret(string? value) => !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) >= 32
        && !value.Contains("development", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("change-me", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}
