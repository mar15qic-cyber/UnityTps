using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public static class DemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        if (!configuration.GetValue("DemoSeed:Enabled", false)) return;
        var username = configuration["DemoSeed:Username"] ?? "demo";
        var password = Environment.GetEnvironmentVariable("DemoSeed__Password");
        if (string.IsNullOrWhiteSpace(password)) return;
        var db = services.GetRequiredService<AppDbContext>();
        var normalized = AuthService.Normalize(username);
        if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized)) return;
        var user = new UserAccount
        {
            Username = username,
            NormalizedUsername = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { SkillPoints = 6, UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }
}
