using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class ServiceFlowTests
{
    [Fact]
    public async Task RegisterCreatesProfileLoadoutAndSeasonOnePass()
    {
        await using var db = CreateDb();
        var auth = new AuthService(db, new FakeJwt(), new DemoProgressionRules());
        var session = await auth.RegisterAsync(new RegisterRequest { Username = "  Alice ", Password = "Password123!" }, CancellationToken.None);

        Assert.Equal("Alice", session.Profile.Username);
        Assert.Equal("weapon.m4", session.Loadout.PrimaryWeaponId);
        Assert.Equal("weapon.service_pistol", session.Loadout.SecondaryWeaponId);
        Assert.Equal(1, session.Loadout.Version);
        Assert.Equal(CatalogSeeder.InitialCoins, session.Coins);
        Assert.Equal(3, await db.InventoryItems.CountAsync());
        Assert.Null(typeof(PlayerProfileDto).GetProperty("SkillPoints"));
        var pass = await db.PlayerPasses.SingleAsync();
        Assert.Equal(PassSeeder.SeasonId, pass.SeasonId);
        Assert.Equal(1, pass.PassLevel);
        Assert.Equal(0, pass.PassXp);
    }

    [Fact]
    public async Task ProfileReadPreservesProgressionWithoutAttributeUpgrades()
    {
        await using var db = CreateDb();
        var user = AddUser(db, "demo", 6);
        user.Profile!.Level = 8; user.Profile.Xp = 45;
        await db.SaveChangesAsync();
        var dto = await new ProfileService(db, new DemoProgressionRules()).GetAsync(user.Id, CancellationToken.None);
        Assert.Equal(8, dto.Level); Assert.Equal(45, dto.Xp);
        Assert.Equal(CatalogSeeder.InitialCoins, dto.Coins);
        Assert.Null(typeof(PlayerProfile).GetProperty("UpDamage"));
        Assert.Null(typeof(ProfileService).GetMethod("UpgradeAsync"));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static UserAccount AddUser(AppDbContext db, string username, int skillPoints)
    {
        var user = new UserAccount
        {
            Username = username,
            NormalizedUsername = username.ToUpperInvariant(),
            PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = CatalogSeeder.InitialCoins, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private sealed class FakeJwt : IJwtTokenService
    {
        public (string Token, DateTime ExpiresAtUtc) Create(UserAccount user) => ("test-token", DateTime.UtcNow.AddHours(12));
    }
}
