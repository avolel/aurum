using Aurum.App.Infrastructure.Data.Entities.Logging;
using Aurum.App.Infrastructure.Data.Entities.Macro;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Infrastructure.Data;

public class AurumDbContext(DbContextOptions<AurumDbContext> options, TimeProvider clock) : DbContext(options)
{
    public DbSet<PriceTick> PriceTicks => Set<PriceTick>();
    public DbSet<PriceSource> PriceSources => Set<PriceSource>();
    public DbSet<ApiQuotaWindow> ApiQuotaWindows => Set<ApiQuotaWindow>();
    public DbSet<MacroSeries> MacroSeries => Set<MacroSeries>();
    public DbSet<MacroObservation> MacroObservations => Set<MacroObservation>();
    public DbSet<AppLog> AppLogs => Set<AppLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("timescaledb");
        b.HasPostgresExtension("vector");

        b.Entity<PriceSource>(e =>
        {
            e.ToTable("price_sources");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.LastFailureReason).HasMaxLength(512);
            e.HasIndex(x => new { x.IsEnabled, x.Priority });
        });

        b.Entity<PriceTick>(e =>
        {
            e.ToTable("price_ticks");

            // Partitioning column first: TimescaleDB requires every unique index to include it.
            e.HasKey(x => new { x.ObservedAt, x.Id });
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.SourceCode).HasMaxLength(64).IsRequired();

            // 18,4 covers spot metals with room for JPY-denominated pairs later without
            // hitting the float rounding that makes deltas non-reproducible.
            e.Property(x => x.Bid).HasPrecision(18, 4);
            e.Property(x => x.Ask).HasPrecision(18, 4);
            e.Property(x => x.Mid).HasPrecision(18, 4);

            // The delta engine's read pattern (Phase 1): latest N for one symbol, newest first.
            e.HasIndex(x => new { x.Symbol, x.ObservedAt }).IsDescending(false, true);

            e.HasOne(x => x.Source)
                .WithMany()
                .HasForeignKey(x => x.SourceCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApiQuotaWindow>(e =>
        {
            e.ToTable("api_quota_windows");
            e.Property(x => x.SourceCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.PeriodKey).HasMaxLength(64).IsRequired();

            // The correctness guarantee the governor leans on: at most one live counter
            // per source per period, so a concurrent acquire cannot create a second bucket.
            e.HasIndex(x => new { x.SourceCode, x.PeriodKey }).IsUnique();
        });

        b.Entity<AppLog>(e =>
        {
            e.ToTable("app_logs");
            e.Property(x => x.LogType).HasMaxLength(32).IsRequired();
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.Details).HasMaxLength(4000);
            e.Property(x => x.ExceptionType).HasMaxLength(256);
            e.Property(x => x.StackTrace).HasMaxLength(8000);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.RequestPath).HasMaxLength(512);
            e.Property(x => x.HttpMethod).HasMaxLength(16);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(128);

            // The two reads this table has: "what happened around then" and "everything from this
            // one request". Nothing scans it by user or by action, so neither gets an index.
            e.HasIndex(x => x.CreatedAt).IsDescending();
            e.HasIndex(x => x.CorrelationId);
        });

        b.Entity<MacroSeries>(e =>
        {
            e.ToTable("macro_series");
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Units).HasMaxLength(64);
            e.Property(x => x.Frequency).HasMaxLength(32);
            e.HasIndex(x => new { x.Provider, x.Code }).IsUnique();
        });

        b.Entity<MacroObservation>(e =>
        {
            e.ToTable("macro_observations");
            e.Property(x => x.Value).HasPrecision(18, 6);
            e.HasIndex(x => new { x.MacroSeriesId, x.ObservedOn }).IsUnique();
            e.HasOne(x => x.Series)
                .WithMany(x => x.Observations)
                .HasForeignKey(x => x.MacroSeriesId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        ApplyAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void ApplyAuditFields()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }
    }
}
