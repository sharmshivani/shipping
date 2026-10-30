using Microsoft.EntityFrameworkCore;

namespace Shipping.StatusService;

/// <summary>Current status of a shipment (read model, built from events).</summary>
public class ShipmentStatus
{
    public Guid ShipmentId { get; set; }
    public string UserId { get; set; } = "";
    public string? TrackingNumber { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Append-only timeline shown to the customer.</summary>
public class StatusHistory
{
    public long Id { get; set; }
    public Guid ShipmentId { get; set; }
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";     // event type, or "carrier"
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Inbox table: remembers processed Pub/Sub eventIds so redelivered messages are ignored.</summary>
public class ProcessedEvent
{
    public string EventId { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class StatusDb(DbContextOptions<StatusDb> options) : DbContext(options)
{
    public DbSet<ShipmentStatus> Statuses => Set<ShipmentStatus>();
    public DbSet<StatusHistory> History => Set<StatusHistory>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("status");
        b.Entity<ShipmentStatus>(e =>
        {
            e.ToTable("shipment_status");
            e.HasKey(x => x.ShipmentId);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.TrackingNumber).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.HasIndex(x => x.UserId);
        });
        b.Entity<StatusHistory>(e =>
        {
            e.ToTable("status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.Property(x => x.Source).HasMaxLength(48).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(500);
            e.HasIndex(x => new { x.ShipmentId, x.OccurredAt });
        });
        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasMaxLength(64);
        });
    }
}

public sealed record StatusDto(Guid ShipmentId, string? TrackingNumber, string Status, DateTimeOffset UpdatedAt)
{
    public static StatusDto From(ShipmentStatus s) => new(s.ShipmentId, s.TrackingNumber, s.Status, s.UpdatedAt);
}

public sealed record HistoryDto(string Status, string Source, string? Detail, DateTimeOffset OccurredAt);
public sealed record StatusDetailDto(StatusDto Current, IReadOnlyList<HistoryDto> History);
public sealed record CarrierEventRequest(string? Status, string? Note);
