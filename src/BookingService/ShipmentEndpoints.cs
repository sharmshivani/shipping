using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Shipping.Contracts;
using Shipping.Platform;

namespace Shipping.BookingService;

public static class ShipmentEndpoints
{
    public static IEndpointRouteBuilder MapShipmentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/shipments").RequireAuthorization("booking");   // needs scope "booking"

        api.MapPost("/", async (CreateShipmentRequest req, ClaimsPrincipal user, BookingDb db, CancellationToken ct) =>
        {
            var errors = req.Validate();
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = DateTimeOffset.UtcNow;
            var shipment = new Shipment
            {
                UserId = user.GetUserId()!,
                TrackingNumber = Pricing.NewTrackingNumber(),
                Origin = req.Origin!.Trim(),
                Destination = req.Destination!.Trim(),
                ReceiverName = req.ReceiverName!.Trim(),
                WeightKg = req.WeightKg,
                ServiceLevel = (req.ServiceLevel ?? ServiceLevels.Standard).ToUpperInvariant(),
                AmountMinor = Pricing.Calculate(req.WeightKg, req.ServiceLevel),
                CreatedAt = now,
            };

            db.Shipments.Add(shipment);
            // Outbox pattern: the event is saved in the SAME transaction as the shipment, so we can never
            // have a booked shipment without its event (or the reverse). The relay publishes it to Pub/Sub.
            db.Outbox.Add(OutboxMessage.Create(
                EventTypes.ShipmentBooked, shipment.Id.ToString(),
                new ShipmentBooked(shipment.Id, shipment.UserId, shipment.TrackingNumber, shipment.AmountMinor, shipment.Currency, now),
                now));
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/shipments/{shipment.Id}", ShipmentDto.From(shipment));
        });

        api.MapGet("/", async (ClaimsPrincipal user, BookingDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var items = await db.Shipments.AsNoTracking()
                .Where(s => s.UserId == uid)
                .OrderByDescending(s => s.CreatedAt)
                .Take(100)
                .ToListAsync(ct);
            return Results.Ok(items.Select(ShipmentDto.From));
        });

        // Returns 404 (not 403) for other users' shipments so ids can't be probed.
        api.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, BookingDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var shipment = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid, ct);
            return shipment is null ? Results.NotFound() : Results.Ok(ShipmentDto.From(shipment));
        });

        return app;
    }
}
