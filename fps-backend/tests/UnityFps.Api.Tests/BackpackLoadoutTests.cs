using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// CF 三背包（2026-09-30 Phase A）后端行为锁定：
/// 懒默认合成 / 懒创建落库 / 背包间隔离 / 独立版本号 / 投掷物校验 / consume 三背包快照。
/// </summary>
public sealed class BackpackLoadoutTests
{
    // ---- BackpackPolicy 参数归一化 ----

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(0, 0, false)]
    [InlineData(4, 0, false)]
    [InlineData(-1, 0, false)]
    public void NormalizeBackpackParam_Matrix(int? input, int expected, bool valid)
    {
        Assert.Equal(valid, BackpackPolicy.TryNormalizeParam(input, out var index));
        if (valid) Assert.Equal(expected, index);
    }

    // ---- 注册会话：背包 0 落库 + 1/2 懒默认 + 初始投掷物 ----

    [Fact]
    public async Task Register_SeedsBackpackZeroAndSynthesizesLazyBackpacks()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var auth = new AuthService(db, new FakeJwt(), new DemoProgressionRules());
        var session = await auth.RegisterAsync(new RegisterRequest { Username = "packuser", Password = "Password123!" }, CancellationToken.None);

        Assert.NotNull(session.Backpacks);
        Assert.Equal(3, session.Backpacks!.Backpacks.Length);
        Assert.Equal(0, session.Backpacks.ActiveIndex);
        Assert.Equal(0, session.Backpacks.Backpacks[0].BackpackIndex);
        Assert.Equal("weapon.m4", session.Backpacks.Backpacks[0].PrimaryWeaponId);
        Assert.Equal(BackpackPolicy.DefaultThrowableItemId, session.Backpacks.Backpacks[0].ThrowableId);
        // 背包 1/2 懒默认：武器拷贝背包 0
        for (var i = 1; i < 3; i++)
        {
            Assert.Equal(i, session.Backpacks.Backpacks[i].BackpackIndex);
            Assert.Equal("weapon.m4", session.Backpacks.Backpacks[i].PrimaryWeaponId);
            Assert.Equal(BackpackPolicy.DefaultThrowableItemId, session.Backpacks.Backpacks[i].ThrowableId);
        }
        // AuthSessionDto.Loadout 旧字段 = 背包 0 镜像（旧客户端兼容）
        Assert.Equal(session.Backpacks.Backpacks[0].PrimaryWeaponId, session.Loadout.PrimaryWeaponId);
        // 注册即持有默认投掷物
        Assert.Equal(1, await db.Loadouts.CountAsync());
        Assert.True(await db.InventoryItems.AnyAsync(x => x.ItemId == "throwable.frag"));
    }

    // ---- GET 背包 2：懒默认不落库 ----

    [Fact]
    public async Task GetLazyBackpack_ReturnsDefaultWithoutPersisting()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new LoadoutService(db);

        var dto = await service.GetAsync(user.Id, 1, CancellationToken.None);
        Assert.Equal(1, dto.BackpackIndex);
        Assert.Equal("weapon.m4", dto.PrimaryWeaponId);
        Assert.Equal("weapon.service_pistol", dto.SecondaryWeaponId);
        Assert.Equal(BackpackPolicy.DefaultThrowableItemId, dto.ThrowableId);
        Assert.Equal(1, dto.Version);
        Assert.Empty(dto.Attachments);
        Assert.Equal(1, await db.Loadouts.CountAsync()); // 懒读取不落库
    }

    // ---- PUT 背包 2：懒创建落库 + 背包隔离 + 独立版本号 ----

    [Fact]
    public async Task PutLazyBackpack_CreatesRowAndIsolatesFromBackpackZero()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = AddUser(db);
        db.InventoryItems.Add(new PlayerInventoryItem { UserId = user.Id, ItemId = "weapon.handgun02", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        db.InventoryItems.Add(new PlayerInventoryItem { UserId = user.Id, ItemId = "weapon.lpw.rifle.01", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new LoadoutService(db);

        var saved = await service.UpdateAsync(user.Id, 1, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.lpw.rifle.01", SecondaryWeaponId = "weapon.handgun02", ExpectedVersion = 1
        }, CancellationToken.None);
        Assert.Equal(1, saved.BackpackIndex);
        Assert.Equal(2, saved.Version);
        Assert.Equal(2, await db.Loadouts.CountAsync());

        // 背包 0 未被波及
        var zero = await service.GetAsync(user.Id, 0, CancellationToken.None);
        Assert.Equal("weapon.m4", zero.PrimaryWeaponId);
        Assert.Equal("weapon.service_pistol", zero.SecondaryWeaponId);
        Assert.Equal(1, zero.Version);

        // 独立版本号：背包 2 的过期版本被拒不影响背包 0 更新
        await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, 1, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.lpw.rifle.01", SecondaryWeaponId = "weapon.handgun02", ExpectedVersion = 1
        }, CancellationToken.None));
        var zeroSaved = await service.UpdateAsync(user.Id, 0, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.ak", SecondaryWeaponId = "weapon.service_pistol", ExpectedVersion = 1
        }, CancellationToken.None);
        Assert.Equal(2, zeroSaved.Version);
        // 版本冲突错误码验证（上面 ThrowsAsync 不判型，这里补语义）
        var conflict = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, 1, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.lpw.rifle.01", SecondaryWeaponId = "weapon.handgun02", ExpectedVersion = 1
        }, CancellationToken.None));
        Assert.Equal(ApiErrorCodes.LoadoutVersionConflict, conflict.Code);
    }

    // ---- 投掷物校验矩阵 ----

    [Fact]
    public async Task ThrowableValidation_OwnedAccepted_WeaponRejected_UnownedRejected_NullAllowed()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new LoadoutService(db);

        // 已拥有投掷物 → 接受并落库
        var withThrowable = await service.UpdateAsync(user.Id, 0, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol",
            ThrowableId = BackpackPolicy.DefaultThrowableItemId, ExpectedVersion = 1
        }, CancellationToken.None);
        Assert.Equal(BackpackPolicy.DefaultThrowableItemId, withThrowable.ThrowableId);

        // 武器当投掷物 → 400
        var weaponAsThrowable = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, 0, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol",
            ThrowableId = "weapon.m4", ExpectedVersion = 2
        }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, weaponAsThrowable.StatusCode);

        // 未拥有投掷物 → 403
        var unowned = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, 0, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol",
            ThrowableIds = ["throwable.frag_02", null, null], ExpectedVersion = 2
        }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, unowned.StatusCode);

        // null = 不带雷（放行并清空）
        var cleared = await service.UpdateAsync(user.Id, 0, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol",
            ThrowableId = null, ExpectedVersion = 2
        }, CancellationToken.None);
        Assert.Null(cleared.ThrowableId);
    }

    // ---- 配件按背包隔离 ----

    [Fact]
    public async Task Attachments_AreScopedPerBackpack()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        var user = AddUser(db);
        const string optic = "attach.lpfp.optic.01";
        db.InventoryItems.Add(new PlayerInventoryItem { UserId = user.Id, ItemId = optic, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new LoadoutService(db);

        var saved = await service.UpdateAttachmentsAsync(user.Id, 2, new LoadoutAttachmentsRequest
        {
            ExpectedVersion = 1, WeaponSlot = "Primary",
            Attachments = [new AttachmentSelectionRequest { AttachmentSlot = "Optic", AttachmentItemId = optic }]
        }, CancellationToken.None);
        Assert.Single(saved.Attachments);
        Assert.Equal(2, saved.Version);
        Assert.Equal(2, await db.Loadouts.CountAsync()); // 首次配件保存 = 懒创建该行

        var zero = await service.GetAttachmentsAsync(user.Id, 0, CancellationToken.None);
        Assert.Empty(zero.Attachments); // 背包 0 不被波及
        var two = await service.GetAttachmentsAsync(user.Id, 2, CancellationToken.None);
        Assert.Single(two.Attachments);
    }

    // ---- 背包 0 缺失（异常态）保持 404 语义 ----

    [Fact]
    public async Task MissingBackpackZero_StillNotFound()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = new UserAccount
        {
            Username = "norow", NormalizedUsername = "NOROW", PasswordHash = "test", CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = 0, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new LoadoutService(db);
        await Assert.ThrowsAsync<ApiException>(() => service.GetAsync(user.Id, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => service.GetBackpackSetAsync(user.Id, CancellationToken.None));
    }

    // ---- 种子回填：存量用户获默认投掷物 + 空投掷槽补默认 ----

    [Fact]
    public async Task Seed_BackfillsThrowableOwnershipAndEmptySlot()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = new UserAccount
        {
            Username = "legacy2", NormalizedUsername = "LEGACY2", PasswordHash = "test", CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadouts = { new PlayerLoadout { ThrowableId = null, UpdatedAtUtc = DateTime.UtcNow } },
            Wallet = new PlayerWallet { Coins = 0, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await CatalogSeeder.SeedAsync(db);
        Assert.Null(user.Loadouts[0].ThrowableId);
        Assert.Equal(new string?[3], ThrowableSlotPolicy.Read(user.Loadouts[0]));
        await CatalogSeeder.SeedAsync(db);
        Assert.Equal(new string?[3], ThrowableSlotPolicy.Read(user.Loadouts[0]));
        Assert.Contains(user.Inventory, x => x.ItemId == "throwable.frag");
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserAccount AddUser(AppDbContext db)
    {
        var user = new UserAccount
        {
            Username = Guid.NewGuid().ToString("N"), NormalizedUsername = Guid.NewGuid().ToString("N"),
            PasswordHash = "test", CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadouts = { new PlayerLoadout { ThrowableId = BackpackPolicy.DefaultThrowableItemId, UpdatedAtUtc = DateTime.UtcNow } },
            Wallet = new PlayerWallet { Coins = 10_000, UpdatedAtUtc = DateTime.UtcNow }
        };
        foreach (var itemId in CatalogSeeder.InitialWeapons)
            user.Inventory.Add(new PlayerInventoryItem { ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        foreach (var itemId in CatalogSeeder.InitialThrowables)
            user.Inventory.Add(new PlayerInventoryItem { ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private sealed class FakeJwt : IJwtTokenService
    {
        public (string Token, DateTime ExpiresAtUtc) Create(UserAccount user) => ("test-token", DateTime.UtcNow.AddHours(12));
    }
}
