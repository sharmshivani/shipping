using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shipping.Contracts;

namespace Shipping.StatusService;

/// <summary>
/// Turns events into the current status + history. Designed for at-least-once, unordered delivery:
///   * duplicates   -> skipped via the processed_events inbox table
///   * out of order -> history always appended; "current" only moves to an event that is not older than it
///   * poison msg   -> exception => Nack => Pub/Sub retries, then dead-letters (transaction-events-dlq)
/// </summary>
public sealed class StatusProjector(StatusDb db, ILogger<StatusProjector> log)
{
    public async Task ApplyAsync(string eventType, string eventId, string json, CancellationToken ct)
    {
        if (await db.ProcessedEvents.AnyAsync(e => e.EventId == eventId, ct))
        {
            log.LogInformation("Duplicate event {EventId} ignored", eventId);
            return;
        }

        switch (eventType)
        {
            case EventTypes.ShipmentBooked:
            {
                var e = Parse<ShipmentBooked>(json);
                await ProjectAsync(e.ShipmentId, e.UserId, e.TrackingNumber, ShipmentStatuses.Booked, eventType, "Awaiting payment", e.OccurredAt, ct);
                break;
            }
            case EventTypes.PaymentSucceeded:
            {
                var e = Parse<PaymentSucceeded>(json);
                await ProjectAsync(e.ShipmentId, e.UserId, null, ShipmentStatuses.Confirmed, eventType, "Payment received", e.OccurredAt, ct);
                break;
            }
            case EventTypes.PaymentFailed:
            {
                var e = Parse<PaymentFailed>(json);
                await ProjectAsync(e.ShipmentId, e.UserId, null, ShipmentStatuses.PaymentFailed, eventType, e.Reason, e.OccurredAt, ct);
                break;
            }
            default:
                log.LogWarning("Ignoring unknown event type {EventType}", eventType);
                break;
        }

        db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);   // status + history + inbox row commit together
    }

    private async Task ProjectAsync(Guid shipmentId, string userId, string? tracking, string status, string source,
        string? detail, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var row = await db.Statuses.FindAsync(new object[] { shipmentId }, ct);
        if (row is null)
        {
            row = new ShipmentStatus { ShipmentId = shipmentId, UserId = userId, TrackingNumber = tracking, Status = status, UpdatedAt = occurredAt };
            db.Statuses.Add(row);
        }
        else
        {
            row.TrackingNumber ??= tracking;
            if (occurredAt >= row.UpdatedAt)
            {
                row.Status = status;
                row.UpdatedAt = occurredAt;
            }
        }

        db.History.Add(new StatusHistory { ShipmentId = shipmentId, Status = status, Source = source, Detail = detail, OccurredAt = occurredAt });
    }

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, EventJson.Options) ?? throw new InvalidDataException($"Empty {typeof(T).Name} payload");
}
