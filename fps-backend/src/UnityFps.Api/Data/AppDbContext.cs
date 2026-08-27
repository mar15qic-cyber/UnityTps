using Microsoft.EntityFrameworkCore;

namespace UnityFps.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<PlayerProfile> Profiles => Set<PlayerProfile>();
    public DbSet<PlayerLoadout> Loadouts => Set<PlayerLoadout>();
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();

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
            entity.Property(x => x.PrimaryWeaponId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SecondaryWeaponId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ThrowableId).HasMaxLength(32);
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("datetime(6)");
        });
        model.Entity<MatchRecord>(entity =>
        {
            entity.ToTable("MatchRecord");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.PlayedAtUtc });
            entity.Property(x => x.PlayedAtUtc).HasColumnType("datetime(6)");
            entity.HasOne(x => x.User).WithMany(x => x.Matches).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
