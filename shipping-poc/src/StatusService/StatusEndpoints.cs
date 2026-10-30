using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Shipping.Contracts;
using Shipping.Platform;

namespace Shipping.StatusService;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/status");

        // Customer: all my shipments' statuses.
        api.MapGet("/shipments", async (ClaimsPrincipal user, StatusDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var rows = await db.Statuses.AsNoTracking().Where(s => s.UserId == uid)
                .OrderByDescending(s => s.UpdatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(StatusDto.From));
        }).RequireAuthorization("status.read");

        // Customer: one shipment + its timeline.
        api.MapGet("/shipments/{id:guid}", async (Guid id, ClaimsPrincipal user, StatusDb db, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            var row = await db.Statuses.AsNoTracking().FirstOrDefaultAsync(s => s.ShipmentId == id && s.UserId == uid, ct);
            if (row is null) return Results.NotFound();

            var history = await db.History.AsNoTracking().Where(h => h.ShipmentId == id)
                .OrderBy(h => h.OccurredAt).ThenBy(h => h.Id)
                .Select(h => new HistoryDto(h.Status, h.Source, h.Detail, h.OccurredAt)).ToListAsync(ct);
            return Results.Ok(new StatusDetailDto(StatusDto.From(row), history));
        }).RequireAuthorization("status.read");

        // Carrier / ops system: push a delivery milestone. Machine-to-machine: needs a client_credentials
        // token with scope "status.write" (client "carrier-sim"), NOT a user token.
        api.MapPost("/shipments/{id:guid}/events", async (Guid id, CarrierEventRequest req, StatusDb db, CancellationToken ct) =>
        {
            var next = req.Status?.Trim().ToUpperInvariant();
            var row = await db.Statuses.FindAsync(new object[] { id }, ct);
            if (row is null) return Results.NotFound();

            if (!ShipmentStatuses.CanCarrierAdvance(row.Status, next))
                return Results.Conflict(new { error = "invalid_transition", from = row.Status, to = next });

            var now = DateTimeOffset.UtcNow;
            row.Status = next!;
            row.UpdatedAt = now;
            db.History.Add(new StatusHistory { ShipmentId = id, Status = next!, Source = "carrier", Detail = req.Note, OccurredAt = now });
            await db.SaveChangesAsync(ct);

            return Results.Ok(StatusDto.From(row));
        }).RequireAuthorization("status.write");

        return app;
    }
}
