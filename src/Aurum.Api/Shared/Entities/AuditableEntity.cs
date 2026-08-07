namespace Aurum.Api.Shared.Entities;

/// <summary>
/// Audit fields required on every persisted entity (§9.1). <see cref="AurumDbContext"/>
/// maintains both timestamps in SaveChanges; callers must not set them.
/// </summary>
public abstract class AuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
