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
    public DbSet<PriceEvent> PriceEvents => Set<PriceEvent>();
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

            // Exact decimals, so deltas are reproducible. Room for JPY pairs later.
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

        b.Entity<PriceEvent>(e =>
        {
            e.ToTable("price_events");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.WindowCode).HasMaxLength(8).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(8).IsRequired();
            e.Property(x => x.SourceCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.BaselineSourceCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.ThresholdProfile).HasMaxLength(64).IsRequired();
            e.Property(x => x.TriggeredRule).HasMaxLength(256).IsRequired();

            e.Property(x => x.StartMid).HasPrecision(18, 4);
            e.Property(x => x.EndMid).HasPrecision(18, 4);
            e.Property(x => x.DeltaAbsolute).HasPrecision(18, 4);
            e.Property(x => x.DeltaPercent).HasPrecision(18, 6);
            e.Property(x => x.VelocityPercentPerMinute).HasPrecision(18, 6);
            e.Property(x => x.Volatility).HasPrecision(18, 6);

            // The duplicate safety net's ON CONFLICT, and the cooldown's range read (D-18).
            e.HasIndex(x => new { x.Symbol, x.WindowCode, x.WindowEndedAt }).IsUnique();

            e.HasOne<PriceSource>()
                .WithMany()
                .HasForeignKey(x => x.SourceCode)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PriceSource>()
                .WithMany()
                .HasForeignKey(x => x.BaselineSourceCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApiQuotaWindow>(e =>
        {
            e.ToTable("api_quota_windows");
            e.Property(x => x.SourceCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.PeriodKey).HasMaxLength(64).IsRequired();

            // One counter per source per period; the governor's ON CONFLICT depends on it.
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

            // The two reads: by time, and by request. Nothing reads by user or action.
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
