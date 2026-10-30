using Microsoft.EntityFrameworkCore;
using Shipping.Platform;

namespace Shipping.PaymentService;

public static class PaymentStatus
{
    public const string Created = "CREATED";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShipmentId { get; set; }
    public string UserId { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string Provider { get; set; } = "";
    public string GatewayOrderId { get; set; } = "";
    public string? GatewayPaymentId { get; set; }
    public string Status { get; set; } = PaymentStatus.Created;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Gateways deliver webhooks at-least-once; we remember event ids to ignore duplicates.</summary>
public class WebhookEvent
{
    public string EventId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class PaymentDb(DbContextOptions<PaymentDb> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("payment");
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.GatewayOrderId).HasMaxLength(100).IsRequired();
            e.Property(x => x.GatewayPaymentId).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.HasIndex(x => x.GatewayOrderId).IsUnique();
            e.HasIndex(x => x.UserId);
            // Safety net: a shipment can be paid at most once, enforced by the database itself.
            e.HasIndex(x => x.ShipmentId).IsUnique().HasFilter("\"Status\" = 'SUCCEEDED'");
        });
        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasMaxLength(100);
        });
        b.ConfigureOutbox("payment");
    }
}

public sealed record CreatePaymentRequest(Guid ShipmentId);
public sealed record ConfirmPaymentRequest(string? GatewayPaymentId, string? Signature);

public sealed record PaymentDto(
    Guid Id, Guid ShipmentId, long AmountMinor, string Currency, string Provider,
    string Status, string? GatewayOrderId, string? FailureReason, DateTimeOffset CreatedAt)
{
    public static PaymentDto From(Payment p) => new(
        p.Id, p.ShipmentId, p.AmountMinor, p.Currency, p.Provider, p.Status, p.GatewayOrderId, p.FailureReason, p.CreatedAt);
}

/// <summary>The subset of booking-service's ShipmentDto that payment needs.</summary>
public sealed record ShipmentInfo(Guid Id, string TrackingNumber, long AmountMinor, string Currency);
