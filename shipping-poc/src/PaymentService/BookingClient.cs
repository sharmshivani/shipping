using System.Net;

namespace Shipping.PaymentService;

public interface IBookingClient
{
    /// <summary>Fetches the shipment AS THE CALLING USER. booking-service returns 404 for shipments the user doesn't own.</summary>
    Task<ShipmentInfo?> GetShipmentAsync(Guid shipmentId, CancellationToken ct);
}

/// <summary>
/// Synchronous service-to-service call inside the cluster (http://booking-service). It forwards the caller's
/// bearer token: the amount to charge always comes from booking-service, never from the browser.
/// Alternative for stricter setups: call with a client-credentials token and a dedicated scope.
/// </summary>
public sealed class BookingClient(HttpClient http, IHttpContextAccessor accessor) : IBookingClient
{
    public async Task<ShipmentInfo?> GetShipmentAsync(Guid shipmentId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/shipments/{shipmentId}");
        var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ShipmentInfo>(ct);
    }
}
