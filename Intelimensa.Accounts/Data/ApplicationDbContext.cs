using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Config> Configs => Set<Config>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<StudyParticipation> StudyParticipations => Set<StudyParticipation>();

    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();

    public DbSet<BciDevice> BciDevices => Set<BciDevice>();

    public DbSet<AccountDevice> AccountDevices => Set<AccountDevice>();

    public DbSet<BciDeviceEvent> BciDeviceEvents => Set<BciDeviceEvent>();

    public DbSet<Release> Releases => Set<Release>();

    public DbSet<ReleaseArtifact> ReleaseArtifacts => Set<ReleaseArtifact>();

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

        builder.Entity<Device>(entity =>
        {
            entity.HasOne(d => d.Account)
                .WithMany(a => a.Devices)
                .HasForeignKey(d => d.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Config>(entity =>
        {
            entity.HasIndex(c => c.Key).IsUnique();
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(r => r.TokenHash).IsUnique();
            entity.HasIndex(r => r.UserId);

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
    }
}
