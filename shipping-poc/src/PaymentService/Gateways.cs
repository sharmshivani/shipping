using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Shipping.PaymentService;

public sealed record GatewayOrder(string OrderId);

public sealed class GatewayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Everything the rest of the service knows about a payment provider. Add Stripe / PayU / Cashfree by
/// implementing this interface and registering it in Program.cs.
///
/// Flow (works the same for every "order + hosted checkout" provider):
///   1. server  -> gateway : create an ORDER for amount X            (CreateOrderAsync)
///   2. browser -> gateway : customer pays inside the gateway's checkout widget (card data never touches us)
///   3. browser -> server  : sends back (paymentId, signature)        (VerifyPaymentSignature)
///   4. gateway -> server  : signed WEBHOOK, the authoritative result (VerifyWebhookSignature)
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }
    /// <summary>Public key the browser needs to open the checkout (safe to expose).</summary>
    string? PublicKey { get; }
    Task<GatewayOrder> CreateOrderAsync(long amountMinor, string currency, string receipt, CancellationToken ct);
    bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
    bool VerifyWebhookSignature(string rawBody, string signature);
}

public sealed class RazorpayOptions
{
    public string KeyId { get; set; } = "";
    public string KeySecret { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.razorpay.com/v1/";
}

/// <summary>Razorpay Orders API + HMAC-SHA256 signature checks, no SDK needed.</summary>
public sealed class RazorpayGateway(HttpClient http, IOptions<RazorpayOptions> options) : IPaymentGateway
{
    private readonly RazorpayOptions _o = options.Value;

    public string Name => "razorpay";
    public string? PublicKey => _o.KeyId;

    public async Task<GatewayOrder> CreateOrderAsync(long amountMinor, string currency, string receipt, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "orders")
        {
            // receipt max 40 chars; amount is in the smallest currency unit (paise for INR)
            Content = JsonContent.Create(new { amount = amountMinor, currency, receipt }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.KeyId}:{_o.KeySecret}")));

        try
        {
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new GatewayException($"Razorpay order creation failed: HTTP {(int)res.StatusCode} {body}");

            using var doc = JsonDocument.Parse(body);
            return new GatewayOrder(doc.RootElement.GetProperty("id").GetString()!);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayException("Razorpay is unreachable", ex);
        }
    }

    // Checkout success callback: HMAC_SHA256(order_id + "|" + payment_id, key_secret)
    public bool VerifyPaymentSignature(string orderId, string paymentId, string signature) =>
        Signatures.HexHmacEquals(_o.KeySecret, $"{orderId}|{paymentId}", signature);

    // Webhook: HMAC_SHA256(raw request body, webhook_secret) in header X-Razorpay-Signature
    public bool VerifyWebhookSignature(string rawBody, string signature) =>
        !string.IsNullOrEmpty(_o.WebhookSecret) && Signatures.HexHmacEquals(_o.WebhookSecret, rawBody, signature);
}

/// <summary>
/// Local-dev gateway: no network, no keys. The dashboard shows "Simulate success / failure" buttons that call
/// POST /api/payments/{id}/simulate. Never register this in production (Helm default = fake only for first deploy; switch to razorpay).
/// </summary>
public sealed class FakeGateway : IPaymentGateway
{
    private const string Secret = "fake-gateway-secret";
    public string Name => "fake";
    public string? PublicKey => "fake";

    public Task<GatewayOrder> CreateOrderAsync(long amountMinor, string currency, string receipt, CancellationToken ct) =>
        Task.FromResult(new GatewayOrder("fake_order_" + Guid.NewGuid().ToString("N")));

    public bool VerifyPaymentSignature(string orderId, string paymentId, string signature) =>
        Signatures.HexHmacEquals(Secret, $"{orderId}|{paymentId}", signature);

    public bool VerifyWebhookSignature(string rawBody, string signature) =>
        Signatures.HexHmacEquals(Secret, rawBody, signature);
}

public static class Signatures
{
    public static string HexHmac(string secret, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    /// <summary>Constant-time comparison so response timing can't be used to guess a signature.</summary>
    public static bool HexHmacEquals(string secret, string message, string providedHexSignature)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(providedHexSignature)) return false;
        var expected = Encoding.ASCII.GetBytes(HexHmac(secret, message));
        var provided = Encoding.ASCII.GetBytes(providedHexSignature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
