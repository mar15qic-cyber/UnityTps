using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UnityFps.Api.Data;

#nullable disable

namespace UnityFps.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.13");
        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 64);
        modelBuilder.HasAnnotation("MySql:CharSet", "utf8mb4");

        modelBuilder.Entity("UnityFps.Api.Data.UserAccount", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasAnnotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn);
            b.Property<DateTime>("CreatedAtUtc").HasColumnType("datetime(6)");
            b.Property<DateTime?>("LastLoginAtUtc").HasColumnType("datetime(6)");
            b.Property<string>("NormalizedUsername").IsRequired().HasMaxLength(32).HasColumnType("varchar(32)");
            b.Property<string>("PasswordHash").IsRequired().HasMaxLength(100).HasColumnType("varchar(100)");
            b.Property<string>("Username").IsRequired().HasMaxLength(32).HasColumnType("varchar(32)");
            b.HasKey("Id");
            b.HasIndex("NormalizedUsername").IsUnique();
            b.ToTable("UserAccount");
        });
        modelBuilder.Entity("UnityFps.Api.Data.PlayerProfile", b =>
        {
            b.Property<long>("UserId").HasColumnType("bigint");
            b.Property<int>("Level").ValueGeneratedOnAdd().HasColumnType("int").HasDefaultValue(1);
            b.Property<int>("SkillPoints").HasColumnType("int");
            b.Property<int>("UpAmmoCap").HasColumnType("int");
            b.Property<int>("UpDamage").HasColumnType("int");
            b.Property<int>("UpMaxHealth").HasColumnType("int");
            b.Property<int>("Xp").HasColumnType("int");
            b.Property<DateTime>("UpdatedAtUtc").HasColumnType("datetime(6)");
            b.HasKey("UserId");
            b.ToTable("PlayerProfile");
        });
        modelBuilder.Entity("UnityFps.Api.Data.PlayerLoadout", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasAnnotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn);
            b.Property<string>("PrimaryWeaponId").IsRequired().HasMaxLength(32).HasColumnType("varchar(32)");
            b.Property<string>("SecondaryWeaponId").IsRequired().HasMaxLength(32).HasColumnType("varchar(32)");
            b.Property<string>("ThrowableId").HasMaxLength(32).HasColumnType("varchar(32)");
            b.Property<DateTime>("UpdatedAtUtc").HasColumnType("datetime(6)");
            b.Property<long>("UserId").HasColumnType("bigint");
            b.HasKey("Id");
            b.HasIndex("UserId").IsUnique();
            b.ToTable("PlayerLoadout");
        });
        modelBuilder.Entity("UnityFps.Api.Data.MatchRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasAnnotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn);
            b.Property<int>("Deaths").HasColumnType("int");
            b.Property<int>("Kills").HasColumnType("int");
            b.Property<DateTime>("PlayedAtUtc").HasColumnType("datetime(6)");
            b.Property<int>("Score").HasColumnType("int");
            b.Property<long>("UserId").HasColumnType("bigint");
            b.Property<int>("XpEarned").HasColumnType("int");
            b.HasKey("Id");
            b.HasIndex("UserId", "PlayedAtUtc");
            b.ToTable("MatchRecord");
        });
        modelBuilder.Entity("UnityFps.Api.Data.PlayerProfile", b => b.HasOne("UnityFps.Api.Data.UserAccount", "User").WithOne("Profile").HasForeignKey("UnityFps.Api.Data.PlayerProfile", "UserId").OnDelete(DeleteBehavior.Cascade).IsRequired());
        modelBuilder.Entity("UnityFps.Api.Data.PlayerLoadout", b => b.HasOne("UnityFps.Api.Data.UserAccount", "User").WithOne("Loadout").HasForeignKey("UnityFps.Api.Data.PlayerLoadout", "UserId").OnDelete(DeleteBehavior.Cascade).IsRequired());
        modelBuilder.Entity("UnityFps.Api.Data.MatchRecord", b => b.HasOne("UnityFps.Api.Data.UserAccount", "User").WithMany("Matches").HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired());
        modelBuilder.Entity("UnityFps.Api.Data.UserAccount", b => { b.Navigation("Loadout"); b.Navigation("Matches"); b.Navigation("Profile"); });
        modelBuilder.Entity("UnityFps.Api.Data.PlayerProfile", b => b.Navigation("User"));
        modelBuilder.Entity("UnityFps.Api.Data.PlayerLoadout", b => b.Navigation("User"));
        modelBuilder.Entity("UnityFps.Api.Data.MatchRecord", b => b.Navigation("User"));
    }
}
