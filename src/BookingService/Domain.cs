using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Shipping.Platform;

namespace Shipping.BookingService;

public static class ServiceLevels
{
    public const string Standard = "STANDARD";
    public const string Express = "EXPRESS";
}

public class Shipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string ReceiverName { get; set; } = "";
    public decimal WeightKg { get; set; }
    public string ServiceLevel { get; set; } = ServiceLevels.Standard;
    public long AmountMinor { get; set; }              // never use floating point for money
    public string Currency { get; set; } = Pricing.Currency;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>booking.shipments + booking.outbox (the diagram's txn.transactions + txn.outbox, split per service).</summary>
public sealed class BookingDb(DbContextOptions<BookingDb> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("booking");
        b.Entity<Shipment>(e =>
        {
            e.ToTable("shipments");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.TrackingNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(200).IsRequired();
            e.Property(x => x.Destination).HasMaxLength(200).IsRequired();
            e.Property(x => x.ReceiverName).HasMaxLength(120).IsRequired();
            e.Property(x => x.WeightKg).HasPrecision(8, 2);
            e.Property(x => x.ServiceLevel).HasMaxLength(16).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.TrackingNumber).IsUnique();
        });
        b.ConfigureOutbox("booking");
    }
}

/// <summary>Toy rate card: INR 49 base + INR 25/kg, Express = x1.8. All math in paise.</summary>
public static class Pricing
{
    public const string Currency = "INR";

    public static long Calculate(decimal weightKg, string? serviceLevel)
    {
        var minor = 4900m + weightKg * 2500m;
        if (string.Equals(serviceLevel, ServiceLevels.Express, StringComparison.OrdinalIgnoreCase)) minor *= 1.8m;
        return (long)Math.Round(minor, MidpointRounding.AwayFromZero);
    }

    public static string NewTrackingNumber() => "SHP" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
}

public sealed record CreateShipmentRequest(string? Origin, string? Destination, string? ReceiverName, decimal WeightKg, string? ServiceLevel)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Origin) || Origin.Length > 200) errors[nameof(Origin)] = new[] { "Origin is required (max 200 chars)." };
        if (string.IsNullOrWhiteSpace(Destination) || Destination.Length > 200) errors[nameof(Destination)] = new[] { "Destination is required (max 200 chars)." };
        if (string.IsNullOrWhiteSpace(ReceiverName) || ReceiverName.Length > 120) errors[nameof(ReceiverName)] = new[] { "Receiver name is required (max 120 chars)." };
        if (WeightKg is <= 0 or > 70) errors[nameof(WeightKg)] = new[] { "Weight must be between 0.01 and 70 kg." };
        if (ServiceLevel is not null &&
            !ServiceLevel.Equals(ServiceLevels.Standard, StringComparison.OrdinalIgnoreCase) &&
            !ServiceLevel.Equals(ServiceLevels.Express, StringComparison.OrdinalIgnoreCase))
            errors[nameof(ServiceLevel)] = new[] { "ServiceLevel must be STANDARD or EXPRESS." };
        return errors;
    }
}

/// <summary>Also the contract payment-service deserializes (it only reads the fields it needs).</summary>
public sealed record ShipmentDto(
    Guid Id, string TrackingNumber, string Origin, string Destination, string ReceiverName,
    decimal WeightKg, string ServiceLevel, long AmountMinor, string Currency, DateTimeOffset CreatedAt)
{
    public static ShipmentDto From(Shipment s) => new(
        s.Id, s.TrackingNumber, s.Origin, s.Destination, s.ReceiverName,
        s.WeightKg, s.ServiceLevel, s.AmountMinor, s.Currency, s.CreatedAt);
}
