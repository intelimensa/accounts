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

            entity.HasOne(a => a.AssignedConfig)
                .WithMany()
                .HasForeignKey(a => a.AssignedConfigId)
                .OnDelete(DeleteBehavior.Restrict);
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
    }
}
