using Shipping.Contracts;
using Shipping.Platform;

namespace Shipping.PaymentService;

/// <summary>
/// The only place payment state changes. Each transition updates the row AND writes the outbox event in one
/// SaveChanges (= one DB transaction). Transitions are idempotent because both the browser callback and the
/// gateway webhook will normally report the same payment.
/// </summary>
public sealed class PaymentWorkflow(PaymentDb db)
{
    /// <returns>true if this call changed state; false if the payment was already succeeded.</returns>
    public async Task<bool> MarkSucceededAsync(Payment p, string gatewayPaymentId, CancellationToken ct)
    {
        if (p.Status == PaymentStatus.Succeeded) return false;

        var now = DateTimeOffset.UtcNow;
        p.Status = PaymentStatus.Succeeded;
        p.GatewayPaymentId = gatewayPaymentId;
        p.FailureReason = null;
        p.UpdatedAt = now;

        db.Outbox.Add(OutboxMessage.Create(
            EventTypes.PaymentSucceeded, p.ShipmentId.ToString(),
            new PaymentSucceeded(p.Id, p.ShipmentId, p.UserId, p.AmountMinor, p.Currency, p.Provider, gatewayPaymentId, now),
            now));

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A failed attempt can still be followed by a successful retry on the same gateway order.</summary>
    public async Task<bool> MarkFailedAsync(Payment p, string reason, CancellationToken ct)
    {
        if (p.Status != PaymentStatus.Created) return false;

        var now = DateTimeOffset.UtcNow;
        p.Status = PaymentStatus.Failed;
        p.FailureReason = reason.Length > 500 ? reason[..500] : reason;
        p.UpdatedAt = now;

        db.Outbox.Add(OutboxMessage.Create(
            EventTypes.PaymentFailed, p.ShipmentId.ToString(),
            new PaymentFailed(p.Id, p.ShipmentId, p.UserId, p.Provider, p.FailureReason, now),
            now));

        await db.SaveChangesAsync(ct);
        return true;
    }
}
