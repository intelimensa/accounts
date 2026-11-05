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

    public DbSet<PreferenceSlot> PreferenceSlots => Set<PreferenceSlot>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<StudyParticipation> StudyParticipations => Set<StudyParticipation>();

    public DbSet<TelemetryEvent> TelemetryEvents => Set<TelemetryEvent>();

    public DbSet<BciDevice> BciDevices => Set<BciDevice>();

    public DbSet<AccountDevice> AccountDevices => Set<AccountDevice>();

    public DbSet<BciDeviceEvent> BciDeviceEvents => Set<BciDeviceEvent>();

    public DbSet<ProductionStep> ProductionSteps => Set<ProductionStep>();

    public DbSet<MountingType> MountingTypes => Set<MountingType>();

    public DbSet<ElectrodeSet> ElectrodeSets => Set<ElectrodeSet>();

    public DbSet<ElectrodeAssembly> ElectrodeAssemblies => Set<ElectrodeAssembly>();

    public DbSet<ElectrodePart> ElectrodeParts => Set<ElectrodePart>();

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

        builder.Entity<PreferenceSlot>(entity =>
        {
            entity.HasKey(s => new { s.AccountId, s.Slot });
            entity.Property(s => s.Name).HasMaxLength(40);
            entity.Property(s => s.Version).IsConcurrencyToken();
            entity.ToTable(t => t.HasCheckConstraint("CK_PreferenceSlots_Slot", "\"Slot\" BETWEEN 1 AND 5"));

            entity.HasOne(s => s.Account)
                .WithMany()
                .HasForeignKey(s => s.AccountId)
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

        builder.Entity<ProductionStep>(entity =>
        {
            entity.HasIndex(p => new { p.BciDeviceId, p.Step }).IsUnique();
            entity.Property(p => p.Step).HasConversion<int>();

            entity.HasOne(p => p.BciDevice)
                .WithMany(d => d.ProductionSteps)
                .HasForeignKey(p => p.BciDeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.MountingType).WithMany().HasForeignKey(p => p.MountingTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(p => p.ElectrodeSet).WithMany().HasForeignKey(p => p.ElectrodeSetId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MountingType>(entity =>
        {
            entity.HasIndex(m => m.Key).IsUnique();
        });

        builder.Entity<ElectrodeSet>(entity =>
        {
            entity.HasIndex(s => new { s.Name, s.Version }).IsUnique();
            entity.HasIndex(s => s.PreviousId);
        });

        builder.Entity<ElectrodeAssembly>(entity =>
        {
            entity.HasIndex(a => new { a.ElectrodeSetId, a.Name, a.Version }).IsUnique();

            entity.HasOne(a => a.ElectrodeSet)
                .WithMany(s => s.Assemblies)
                .HasForeignKey(a => a.ElectrodeSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ElectrodePart>(entity =>
        {
            entity.HasIndex(p => new { p.ElectrodeAssemblyId, p.Name, p.Version }).IsUnique();

            entity.HasOne(p => p.ElectrodeAssembly)
                .WithMany(a => a.Parts)
                .HasForeignKey(p => p.ElectrodeAssemblyId)
                .OnDelete(DeleteBehavior.Cascade);
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
