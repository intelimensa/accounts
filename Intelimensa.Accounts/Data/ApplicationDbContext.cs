using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<ClientInstall> ClientInstalls => Set<ClientInstall>();

    public DbSet<ConfigKeyIssuance> ConfigKeyIssuances => Set<ConfigKeyIssuance>();

    public DbSet<Config> Configs => Set<Config>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<StudyParticipation> StudyParticipations => Set<StudyParticipation>();

    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();

    public DbSet<BciDevice> BciDevices => Set<BciDevice>();

    public DbSet<AccountDevice> AccountDevices => Set<AccountDevice>();

    public DbSet<BciDeviceEvent> BciDeviceEvents => Set<BciDeviceEvent>();

    public DbSet<Release> Releases => Set<Release>();

    public DbSet<ReleaseArtifact> ReleaseArtifacts => Set<ReleaseArtifact>();

    public DbSet<FirmwareBuild> FirmwareBuilds => Set<FirmwareBuild>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Account>(entity =>
        {
            entity.HasIndex(a => a.UserId).IsUnique();

            entity.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientInstall>(entity =>
        {
            entity.HasIndex(i => new { i.AccountId, i.KeyThumbprint }).IsUnique();
            entity.Property(i => i.Platform).HasConversion<string>();

            entity.HasOne(i => i.Account)
                .WithMany(a => a.ClientInstalls)
                .HasForeignKey(i => i.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ConfigKeyIssuance>(entity =>
        {
            entity.HasIndex(i => i.AccountId);
            entity.HasIndex(i => i.ClientInstallId);
        });

        builder.Entity<Config>(entity =>
        {
            entity.HasIndex(c => c.Key).IsUnique();
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(r => r.TokenHash).IsUnique();
            entity.HasIndex(r => r.UserId);

            entity.HasOne(r => r.ClientInstall)
                .WithMany()
                .HasForeignKey(r => r.ClientInstallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StudyParticipation>(entity =>
        {
            entity.HasKey(sp => sp.AccountId);

            entity.HasOne(sp => sp.Account)
                .WithOne(a => a.StudyParticipation)
                .HasForeignKey<StudyParticipation>(sp => sp.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelemetryEvent>(entity =>
        {
            entity.HasIndex(t => t.AccountId);
            entity.HasIndex(t => t.AccountDeviceId);

            entity.HasOne(t => t.Account)
                .WithMany()
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(t => t.AccountDevice)
                .WithMany()
                .HasForeignKey(t => t.AccountDeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BciDevice>(entity =>
        {
            entity.HasIndex(d => d.SerialNumber).IsUnique();
        });

        builder.Entity<BciDeviceEvent>(entity =>
        {
            entity.HasIndex(e => e.BciDeviceId);

            entity.HasOne(e => e.BciDevice)
                .WithMany()
                .HasForeignKey(e => e.BciDeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccountDevice>(entity =>
        {
            entity.HasIndex(ad => new { ad.AccountId, ad.BciDeviceId }).IsUnique();

            entity.HasOne(ad => ad.Account)
                .WithMany(a => a.AccountDevices)
                .HasForeignKey(ad => ad.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ad => ad.BciDevice)
                .WithMany(d => d.AccountDevices)
                .HasForeignKey(ad => ad.BciDeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(ad => ad.AssignedConfig)
                .WithMany()
                .HasForeignKey(ad => ad.AssignedConfigId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Release>(entity =>
        {
            entity.HasIndex(r => r.Version).IsUnique();
        });

        builder.Entity<ReleaseArtifact>(entity =>
        {
            entity.HasIndex(a => new { a.ReleaseId, a.Platform }).IsUnique();

            entity.HasOne(a => a.Release)
                .WithMany(r => r.Artifacts)
                .HasForeignKey(a => a.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FirmwareBuild>(entity =>
        {
            // A build is identified by its app version and, for a factory image, its bootloader version too:
            // a bootloader fix ships with an unchanged app. Two filtered indexes, because SQLite treats NULLs
            // as distinct in a unique index, so one index over all four columns wouldn't catch duplicate
            // application images (whose bootloader version is null).
            entity.HasIndex(b => new { b.DeviceType, b.Kind, b.Version })
                .IsUnique()
                .HasFilter("\"BootloaderVersion\" IS NULL");
            entity.HasIndex(b => new { b.DeviceType, b.Kind, b.Version, b.BootloaderVersion })
                .IsUnique()
                .HasFilter("\"BootloaderVersion\" IS NOT NULL");
        });
    }
}
