using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shipping.Platform;

namespace Shipping.PaymentService;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/payments").RequireAuthorization("payment");   // needs scope "payment"

        // Step 1: start a payment for a shipment. Amount is taken from booking-service, never from the client.
        api.MapPost("/", async (CreatePaymentRequest req, ClaimsPrincipal user, PaymentDb db, IPaymentGateway gateway,
            IBookingClient booking, ILogger<PaymentWorkflow> log, CancellationToken ct) =>
        {
            var userId = user.GetUserId()!;
            var shipment = await booking.GetShipmentAsync(req.ShipmentId, ct);
            if (shipment is null) return Results.NotFound(new { error = "shipment_not_found" });

            if (await db.Payments.AnyAsync(p => p.ShipmentId == shipment.Id && p.Status == PaymentStatus.Succeeded, ct))
                return Results.Conflict(new { error = "already_paid" });

            var now = DateTimeOffset.UtcNow;
            var payment = new Payment
            {
                ShipmentId = shipment.Id,
                UserId = userId,
                AmountMinor = shipment.AmountMinor,
                Currency = shipment.Currency,
                Provider = gateway.Name,
                CreatedAt = now,
                UpdatedAt = now,
            };

            GatewayOrder order;
            try
            {
                order = await gateway.CreateOrderAsync(payment.AmountMinor, payment.Currency, payment.Id.ToString("N"), ct);
            }
            catch (GatewayException ex)
            {
                log.LogError(ex, "Gateway order creation failed for shipment {ShipmentId}", shipment.Id);
                return Results.Problem(statusCode: 502, title: "Payment gateway unavailable. Please retry.");
            }

            payment.GatewayOrderId = order.OrderId;
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);

            // The browser needs these to open the gateway's checkout widget.
            return Results.Ok(new
            {
                paymentId = payment.Id,
                provider = gateway.Name,
                keyId = gateway.PublicKey,
                orderId = order.OrderId,
                amountMinor = payment.AmountMinor,
                currency = payment.Currency,
                shipmentTracking = shipment.TrackingNumber,
            });
        });

        // Step 2 (browser callback after checkout): verify the gateway's signature, then mark paid.
        // The webhook below is the authoritative path; this just makes the UI feel instant.
        api.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmPaymentRequest req, ClaimsPrincipal user, PaymentDb db,
            IPaymentGateway gateway, PaymentWorkflow workflow, ILogger<PaymentWorkflow> log, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == uid, ct);
            if (payment is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(req.GatewayPaymentId) || string.IsNullOrWhiteSpace(req.Signature) ||
                !gateway.VerifyPaymentSignature(payment.GatewayOrderId, req.GatewayPaymentId, req.Signature))
                return Results.BadRequest(new { error = "invalid_signature" });

            try
            {
                await workflow.MarkSucceededAsync(payment, req.GatewayPaymentId, ct);
            }
            catch (DbUpdateException ex)
            {
                // Unique index: this shipment already has a SUCCEEDED payment (customer paid two open orders).
                log.LogError(ex, "DUPLICATE PAYMENT for shipment {ShipmentId}, payment {PaymentId}: refund required", payment.ShipmentId, payment.Id);
                return Results.Conflict(new { error = "shipment_already_paid" });
            }
            return Results.Ok(PaymentDto.From(payment));
        });

        api.MapGet("/", async (ClaimsPrincipal user, PaymentDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var items = await db.Payments.AsNoTracking().Where(p => p.UserId == uid)
                .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(items.Select(PaymentDto.From));
        });

        api.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, PaymentDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
            return p is null ? Results.NotFound() : Results.Ok(PaymentDto.From(p));
        });

        // Local-dev only (provider = fake): pretend the gateway reported success/failure.
        api.MapPost("/{id:guid}/simulate", async (Guid id, string outcome, ClaimsPrincipal user, PaymentDb db,
            IPaymentGateway gateway, PaymentWorkflow workflow, CancellationToken ct) =>
        {
            if (gateway is not FakeGateway) return Results.NotFound();

            var uid = user.GetUserId();
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == uid, ct);
            if (payment is null) return Results.NotFound();

            if (outcome.Equals("success", StringComparison.OrdinalIgnoreCase))
                await workflow.MarkSucceededAsync(payment, "fake_pay_" + Guid.NewGuid().ToString("N"), ct);
            else
                await workflow.MarkFailedAsync(payment, "Simulated failure", ct);

            return Results.Ok(PaymentDto.From(payment));
        });

        // Gateway -> us. No bearer token: the HMAC signature IS the authentication.
        app.MapPost("/webhooks/razorpay", RazorpayWebhook).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> RazorpayWebhook(HttpRequest request, PaymentDb db, IPaymentGateway gateway,
        PaymentWorkflow workflow, ILogger<PaymentWorkflow> log, CancellationToken ct)
    {
        // The signature is computed over the EXACT raw bytes, so read the body as-is before parsing anything.
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        if (!gateway.VerifyWebhookSignature(body, request.Headers["X-Razorpay-Signature"].ToString()))
        {
            log.LogWarning("Rejected webhook with invalid signature");
            return Results.Unauthorized();
        }

        var eventId = request.Headers["X-Razorpay-Event-Id"].ToString();
        if (!string.IsNullOrEmpty(eventId))
        {
            if (await db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct)) return Results.Ok();   // duplicate delivery
            db.WebhookEvents.Add(new WebhookEvent { EventId = eventId, ReceivedAt = DateTimeOffset.UtcNow });
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var eventName = doc.RootElement.GetProperty("event").GetString();
            var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            var orderId = entity.GetProperty("order_id").GetString();
            var gatewayPaymentId = entity.GetProperty("id").GetString()!;

            var payment = await db.Payments.FirstOrDefaultAsync(p => p.GatewayOrderId == orderId, ct);
            if (payment is null)
            {
                log.LogWarning("Webhook {Event} for unknown order {OrderId}", eventName, orderId);
            }
            else if (eventName is "payment.captured" or "order.paid")
            {
                await workflow.MarkSucceededAsync(payment, gatewayPaymentId, ct);
            }
            else if (eventName == "payment.failed")
            {
                var reason = entity.TryGetProperty("error_description", out var d) ? d.GetString() ?? "Payment failed" : "Payment failed";
                await workflow.MarkFailedAsync(payment, reason, ct);
            }
            await db.SaveChangesAsync(ct);   // persists the webhook-event row if no transition did
            return Results.Ok();
        }
        catch (DbUpdateException ex)
        {
            log.LogError(ex, "Webhook caused a DB conflict (possible duplicate payment for a shipment: refund required)");
            return Results.Ok();             // acknowledge: retrying would never succeed
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogError(ex, "Malformed webhook payload");
            return Results.BadRequest();
        }
    }
}
