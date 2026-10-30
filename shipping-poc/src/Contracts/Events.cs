using System.Text.Json;

namespace Shipping.Contracts;

/// <summary>Pub/Sub attribute "eventType" values. All services publish to ONE topic (transaction-events).</summary>
public static class EventTypes
{
    public const string ShipmentBooked = "shipment.booked";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
}

public sealed record ShipmentBooked(
    Guid ShipmentId, string UserId, string TrackingNumber,
    long AmountMinor, string Currency, DateTimeOffset OccurredAt);

public sealed record PaymentSucceeded(
    Guid PaymentId, Guid ShipmentId, string UserId,
    long AmountMinor, string Currency, string Provider,
    string GatewayPaymentId, DateTimeOffset OccurredAt);

public sealed record PaymentFailed(
    Guid PaymentId, Guid ShipmentId, string UserId,
    string Provider, string Reason, DateTimeOffset OccurredAt);

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Shipment lifecycle. The status-service owns the current value; everyone else just emits events.</summary>
public static class ShipmentStatuses
{
    public const string Booked = "BOOKED";                 // awaiting payment
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string Confirmed = "CONFIRMED";           // paid
    public const string PickedUp = "PICKED_UP";
    public const string InTransit = "IN_TRANSIT";
    public const string OutForDelivery = "OUT_FOR_DELIVERY";
    public const string Delivered = "DELIVERED";

    public static int Rank(string? status) => status switch
    {
        Booked => 1,
        PaymentFailed => 1,
        Confirmed => 2,
        PickedUp => 3,
        InTransit => 4,
        OutForDelivery => 5,
        Delivered => 6,
        _ => 0,
    };

    /// <summary>Carrier updates must move forward and only after payment succeeded.</summary>
    public static bool CanCarrierAdvance(string? current, string? next) =>
        Rank(current) >= Rank(Confirmed) && Rank(next) >= Rank(PickedUp) && Rank(next) > Rank(current);
}
