using Microsoft.EntityFrameworkCore;

namespace UnityFps.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<PlayerProfile> Profiles => Set<PlayerProfile>();
    public DbSet<PlayerLoadout> Loadouts => Set<PlayerLoadout>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<PlayerWallet> Wallets => Set<PlayerWallet>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<PlayerInventoryItem> InventoryItems => Set<PlayerInventoryItem>();
    public DbSet<ShopPurchase> Purchases => Set<ShopPurchase>();
    public DbSet<WalletLedgerEntry> WalletLedger => Set<WalletLedgerEntry>();
    public DbSet<PlayerLoadoutAttachment> LoadoutAttachments => Set<PlayerLoadoutAttachment>();
    public DbSet<AttachmentCompat> AttachmentCompat => Set<AttachmentCompat>();
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    public DbSet<GameRoom> GameRooms => Set<GameRoom>();
    public DbSet<GameRoomMember> GameRoomMembers => Set<GameRoomMember>();
    public DbSet<RoomMatchRoster> RoomMatchRosters => Set<RoomMatchRoster>();
    public DbSet<RoomMatchResult> RoomMatchResults => Set<RoomMatchResult>();
    public DbSet<ServerInstance> ServerInstances => Set<ServerInstance>();
    public DbSet<ServerJoinTicket> ServerJoinTickets => Set<ServerJoinTicket>();
    public DbSet<PlayerPass> PlayerPasses => Set<PlayerPass>();
    public DbSet<PassReward> PassRewards => Set<PassReward>();
    public DbSet<PlayerPassRewardGrant> PassRewardGrants => Set<PlayerPassRewardGrant>();
    public DbSet<AchievementDefinition> AchievementDefinitions => Set<AchievementDefinition>();
    public DbSet<PlayerAchievement> PlayerAchievements => Set<PlayerAchievement>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<UserAccount>(entity =>
        {
            entity.ToTable("UserAccount");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(32).IsRequired();
            entity.Property(x => x.NormalizedUsername).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            entity.HasOne(x => x.Profile).WithOne(x => x.User).HasForeignKey<PlayerProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Loadout).WithOne(x => x.User).HasForeignKey<PlayerLoadout>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Wallet).WithOne(x => x.User).HasForeignKey<PlayerWallet>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PlayerProfile>(entity =>
        {
            entity.ToTable("PlayerProfile");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.Level).HasDefaultValue(1);
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
        });
        model.Entity<PlayerLoadout>(entity =>
        {
            entity.ToTable("PlayerLoadout");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.Property(x => x.PrimaryWeaponId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SecondaryWeaponId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ThrowableId).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
        });
        model.Entity<CatalogItem>(entity =>
        {
            entity.ToTable("CatalogItem");
            entity.HasKey(x => x.ItemId);
            entity.Property(x => x.ItemId).HasMaxLength(64);
            entity.Property(x => x.ItemType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.SlotType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(24).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(240).IsRequired();
            entity.Property(x => x.AssetKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CalibrationKey).HasMaxLength(96).IsRequired();
            entity.Property(x => x.AcquisitionSource).HasMaxLength(16).IsRequired();
        });
        model.Entity<PlayerWallet>(entity =>
        {
            entity.ToTable("PlayerWallet");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
        });
        model.Entity<UserSetting>(entity =>
        {
            entity.ToTable("UserSetting");
            entity.HasKey(x => new { x.UserId, x.SettingKey });
            entity.Property(x => x.SettingKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SettingValue).HasMaxLength(256).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PlayerInventoryItem>(entity =>
        {
            entity.ToTable("PlayerInventoryItem");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AcquiredAtUtc).HasColumnType("datetime(6)");
            entity.HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.Inventory).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ShopPurchase>(entity =>
        {
            entity.ToTable("ShopPurchase");
            entity.HasKey(x => x.PurchaseId);
            entity.Property(x => x.PurchaseId).HasMaxLength(32);
            entity.Property(x => x.ItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(96).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnType("datetime(6)");
            entity.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.Purchases).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WalletLedgerEntry>(entity =>
        {
            entity.ToTable("WalletLedgerEntry");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Reason).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ReferenceId).HasMaxLength(96).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnType("datetime(6)");
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        });
        model.Entity<PlayerLoadoutAttachment>(entity =>
        {
            entity.ToTable("PlayerLoadoutAttachment");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WeaponSlot).HasMaxLength(24).IsRequired();
            entity.Property(x => x.AttachmentSlot).HasMaxLength(24).IsRequired();
            entity.Property(x => x.AttachmentItemId).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.LoadoutId, x.WeaponSlot, x.AttachmentSlot }).IsUnique();
            entity.HasOne(x => x.Loadout).WithMany(x => x.Attachments).HasForeignKey(x => x.LoadoutId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AttachmentCompat>(entity =>
        {
            entity.ToTable("AttachmentCompat");
            entity.HasKey(x => new { x.WeaponItemId, x.AttachmentItemId });
            entity.Property(x => x.WeaponItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AttachmentItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SlotType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.CalibrationKey).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.AttachmentItemId);
            entity.HasOne(x => x.WeaponItem).WithMany().HasForeignKey(x => x.WeaponItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AttachmentItem).WithMany().HasForeignKey(x => x.AttachmentItemId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MatchRecord>(entity =>
        {
            entity.ToTable("MatchRecord");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.PlayedAtUtc });
            entity.Property(x => x.PlayedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.ClientMatchId).HasMaxLength(64);
            entity.HasIndex(x => new { x.UserId, x.ClientMatchId }).IsUnique();
            entity.Property(x => x.MatchId).HasMaxLength(64);
            entity.HasIndex(x => x.MatchId);
            entity.HasOne(x => x.User).WithMany(x => x.Matches).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GameRoom>(entity =>
        {
            entity.ToTable("GameRoom");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RoomCode).HasMaxLength(6).IsRequired();
            entity.HasIndex(x => x.RoomCode).IsUnique();
            entity.Property(x => x.HostUsername).HasMaxLength(32).IsRequired();
            entity.Property(x => x.HostAddress).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.Status, x.StateChangedAtUtc }); // Starting/Returning 超时懒判定
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.MapId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CurrentMatchId).HasMaxLength(64);
            entity.Property(x => x.LastMatchId).HasMaxLength(64);
            entity.Property(x => x.CreatedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.LastHeartbeatUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.StateChangedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostUserId).OnDelete(DeleteBehavior.Cascade);
            // 实例行被删时房间解绑（悬空房间随后按"实例失联"懒清理），不阻塞实例删除
            entity.HasOne(x => x.ServerInstance).WithMany().HasForeignKey(x => x.ServerInstanceId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<GameRoomMember>(entity =>
        {
            entity.ToTable("GameRoomMember");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserId).IsUnique(); // 一人同时只能在一个房间
            entity.HasIndex(x => x.RoomId);
            entity.Property(x => x.TeamId).HasMaxLength(8).IsRequired();
            entity.Property(x => x.JoinedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.LastSeenUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.Room).WithMany(x => x.Members).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        // ===== CF 等待房间/比赛名单（Docs/27 v1）=====

        model.Entity<RoomMatchRoster>(entity =>
        {
            entity.ToTable("RoomMatchRoster");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MatchId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TeamId).HasMaxLength(8).IsRequired();
            entity.HasIndex(x => new { x.MatchId, x.UserId }).IsUnique(); // 终局资格/结果聚合的身份锚点
            entity.HasIndex(x => x.RoomId);
            entity.Property(x => x.IssuedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.LeftAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.ReturnedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.Room).WithMany(x => x.Rosters).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<RoomMatchResult>(entity =>
        {
            entity.ToTable("RoomMatchResult");
            entity.HasKey(x => x.MatchId);
            entity.Property(x => x.MatchId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.WinnerTeam).HasMaxLength(8);
            entity.Property(x => x.PlayersJson).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.EndedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.RewardsAppliedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.ReportedByInstanceId).HasMaxLength(64);
            entity.HasIndex(x => x.RoomId);
        });
        // ===== Dedicated Server 控制面（Docs/27 §2）=====

        model.Entity<ServerInstance>(entity =>
        {
            entity.ToTable("ServerInstance");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.InstanceId).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.InstanceId).IsUnique();
            entity.Property(x => x.Address).HasMaxLength(64).IsRequired();
            entity.Property(x => x.BuildVersion).HasMaxLength(32);
            entity.Property(x => x.State).HasMaxLength(16).IsRequired();
            entity.Property(x => x.RoomCode).HasMaxLength(6);
            entity.HasIndex(x => new { x.State, x.LastHeartbeatUtc }); // 租用查询：心跳新鲜的 Ready 实例
            entity.HasIndex(x => x.RoomCode);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.RegisteredAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.LastHeartbeatUtc).HasColumnType("datetime(6)");
        });
        model.Entity<ServerJoinTicket>(entity =>
        {
            entity.ToTable("ServerJoinTicket");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TicketHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TicketHash).IsUnique();
            entity.Property(x => x.RoomCode).HasMaxLength(6).IsRequired();
            entity.HasIndex(x => x.RoomCode);
            entity.Property(x => x.Username).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.ExpiresAtUtc); // 过期票据清理查询
            entity.Property(x => x.MatchId).HasMaxLength(64);
            entity.Property(x => x.IssuedAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.ExpiresAtUtc).HasColumnType("datetime(6)");
            entity.Property(x => x.ConsumedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.Instance).WithMany().HasForeignKey(x => x.ServerInstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== 通行证与成就（Docs/17 §4.3）=====

        model.Entity<PlayerPass>(entity =>
        {
            entity.ToTable("PlayerPass");
            entity.HasKey(x => new { x.UserId, x.SeasonId });
            entity.Property(x => x.SeasonId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.User).WithMany(x => x.Passes).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PassReward>(entity =>
        {
            entity.ToTable("PassReward");
            entity.HasKey(x => new { x.SeasonId, x.PassLevel });
            entity.Property(x => x.SeasonId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RewardType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ItemId).HasMaxLength(64);
        });
        model.Entity<PlayerPassRewardGrant>(entity =>
        {
            entity.ToTable("PlayerPassRewardGrant");
            entity.HasKey(x => new { x.UserId, x.SeasonId, x.PassLevel });
            entity.Property(x => x.SeasonId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.GrantedAtUtc).HasColumnType("datetime(6)");
        });
        model.Entity<AchievementDefinition>(entity =>
        {
            entity.ToTable("AchievementDefinition");
            entity.HasKey(x => x.AchievementId);
            entity.Property(x => x.AchievementId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(240).IsRequired();
            entity.Property(x => x.TargetMetric).HasMaxLength(32).IsRequired();
        });
        model.Entity<PlayerAchievement>(entity =>
        {
            entity.ToTable("PlayerAchievement");
            entity.HasKey(x => new { x.UserId, x.AchievementId });
            entity.Property(x => x.AchievementId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UnlockedAtUtc).HasColumnType("datetime(6)");
        });
    }
}
